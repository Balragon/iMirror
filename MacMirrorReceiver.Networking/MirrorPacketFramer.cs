using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MacMirrorReceiver;
using MacMirrorReceiver.Protocol;

namespace MacMirrorReceiver.Networking;

/// <summary>
/// Frames the bytes of the AirPlay mirror data connection into 128-byte-header packets and
/// turns them into events: type 1 carries the SPS/PPS and the stream dimensions
/// (<see cref="StreamConfigReceived"/> then the parameter sets as the first payload), type 0
/// carries encrypted H.264 that is decrypted through the <see cref="MirrorDecryptorSelector"/>
/// and normalised to Annex B (<see cref="VideoPayloadReceived"/>); everything else is logged
/// once per type. Also keeps the per-stream notices, the keyframe-loss instrumentation for the
/// decryptor warm-up window and the 10-second payload-shape telemetry. Called from the data
/// receive thread only, apart from <see cref="ResetNotices"/> from the control thread.
/// </summary>
internal sealed class MirrorPacketFramer
{
	private const int PacketHeaderBytes = 128;

	private readonly MirrorDecryptorSelector _decryptors;
	private readonly Func<int, int, int> _resolveAdvertisedFps;
	private readonly bool _audioDiscoveryLogging;
	private readonly int _maxPayloadBytes;
	private readonly object _noticeGate = new object();

	private bool _loggedWaitingForMirrorDecryptor;
	private readonly HashSet<int> _loggedSkippedPacketTypes = new HashSet<int>();
	private readonly Dictionary<int, long> _audioDiscoveryPacketCounts = new Dictionary<int, long>();
	// Instrumentation for the decryptor warm-up keyframe-loss race. A mirror stream sends a single IDR
	// at session start, so if it arrives while the FairPlay stream decryptor is still being selected it
	// cannot be decrypted and is dropped, and the decoder then never receives a keyframe. These track,
	// per warm-up, how many video packets were dropped before the first decodable frame and whether that
	// first surviving frame was actually a keyframe.
	private bool _loggedFirstDecodableVideoFrame;
	private long _droppedVideoBeforeFirstFrame;
	private long _firstEncryptedWaitTick;

	// Lightweight access-unit shape telemetry for the type=0 video path. Logs one summary
	// line every ~10s with NAL composition counts only (no payload bytes, no key material,
	// no screen content). Used to compare sender behaviour (e.g. Mac vs iPhone): whether a
	// sender splits access units across payloads, how often IDR/parameter sets arrive, and
	// whether the received-payload rate matches the decoded-frame rate.
	private readonly object _payloadShapeGate = new object();

	private long _shapeWindowStartTick;

	private int _shapePayloads;

	private int _shapeIdr;

	private int _shapeSlice;

	private int _shapeParamSetOnly;

	private int _shapeSeiOnly;

	private int _shapeOther;

	private int _shapeMultiNal;

	private long _shapeBytes;

	/// <param name="decryptors">The session's key and decryptor state.</param>
	/// <param name="resolveAdvertisedFps">The frame rate to put in the StreamConfig for a given source size.</param>
	/// <param name="audioDiscoveryLogging">Verbose logging of non-video packet types (IMIRROR_AUDIO_DISCOVERY).</param>
	/// <param name="maxPayloadBytes">Largest payload size a packet header may declare.</param>
	public MirrorPacketFramer(MirrorDecryptorSelector decryptors, Func<int, int, int> resolveAdvertisedFps, bool audioDiscoveryLogging, int maxPayloadBytes)
	{
		_decryptors = decryptors;
		_resolveAdvertisedFps = resolveAdvertisedFps;
		_audioDiscoveryLogging = audioDiscoveryLogging;
		_maxPayloadBytes = maxPayloadBytes;
	}

	public event Action<StreamConfig>? StreamConfigReceived;

	public event Action<byte[], ulong, long>? VideoPayloadReceived;

	/// <summary>
	/// Re-arms the once-per-stream notices ("waiting for decryptor", "packet type skipped") when the
	/// key set changes: a new ekey, a changed streamConnectionID or a rebuilt probe pool.
	/// </summary>
	public void ResetNotices()
	{
		lock (_noticeGate)
		{
			_loggedWaitingForMirrorDecryptor = false;
			_loggedSkippedPacketTypes.Clear();
		}
	}

	/// <summary>Emits every complete packet at the head of <paramref name="pending"/>; returns how many video payloads were raised.</summary>
	public int Emit(List<byte> pending)
	{
		int emitted = 0;
		while (pending.Count >= PacketHeaderBytes)
		{
			byte[] header = pending.Take(PacketHeaderBytes).ToArray();
			int payloadSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
			int payloadType = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4, 2)) & 0xFF;
			int payloadOption = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6, 2));
			if (payloadSize < 0 || payloadSize > _maxPayloadBytes)
			{
				AppLog.Write($"AirPlay mirror packet header invalid: size={payloadSize}, type={payloadType}, option={payloadOption}.");
				pending.RemoveAt(0);
				continue;
			}

			if (pending.Count < PacketHeaderBytes + payloadSize)
			{
				return emitted;
			}

			byte[] payload = pending.Skip(PacketHeaderBytes).Take(payloadSize).ToArray();
			pending.RemoveRange(0, PacketHeaderBytes + payloadSize);

			if (payloadType == 1)
			{
				if (TryBuildSpsPpsPayload(header, payload, out byte[] spsPps, out StreamConfig streamConfig))
				{
					StreamConfigReceived?.Invoke(streamConfig);
					VideoPayloadReceived?.Invoke(spsPps, 0, Stopwatch.GetTimestamp());
					emitted++;
				}
				continue;
			}

			if (payloadType == 0)
			{
				byte[] videoPayload = payload;
				if (_decryptors.TryDecryptMirrorPayload(payload, out byte[] decryptedPayload))
				{
					videoPayload = decryptedPayload;
				}
				else if (MarkWaitingForDecryptor())
				{
					// Start of a decryptor warm-up window: reset the keyframe-loss instrumentation so it
					// reports once per session (this branch re-fires after each session resets the flag).
					_firstEncryptedWaitTick = Stopwatch.GetTimestamp();
					_loggedFirstDecodableVideoFrame = false;
					_droppedVideoBeforeFirstFrame = 0;
					AppLog.Write("AirPlay mirror video payload is encrypted; waiting for FairPlay stream decryptor.");
				}

					if (MirrorH264Payloads.TryNormalizeClearH264Payload(videoPayload, out byte[] annexB, out string clearFormat))
					{
						RecordVideoPayloadShape(annexB);
						VideoPayloadReceived?.Invoke(annexB, 0, Stopwatch.GetTimestamp());
						emitted++;
					}
						else if (_decryptors.TryProbeMirrorDecryptors(payloadType, header, payload, out annexB, out string selectedName, out string selectedFormat))
						{
							RecordVideoPayloadShape(annexB);
							VideoPayloadReceived?.Invoke(annexB, 0, Stopwatch.GetTimestamp());
							AppLog.Write($"AirPlay mirror video type={payloadType} decrypted with stateful candidate '{selectedName}' ({selectedFormat}).");
							emitted++;
						}
						else if (_decryptors.TrySelectMirrorDecryptor(payloadType, header, payload, out annexB, out selectedName, out selectedFormat))
						{
							RecordVideoPayloadShape(annexB);
							VideoPayloadReceived?.Invoke(annexB, 0, Stopwatch.GetTimestamp());
							AppLog.Write($"AirPlay mirror video type={payloadType} decrypted with candidate '{selectedName}' ({selectedFormat}).");
						emitted++;
					}
					else
					{
						// This type=0 video packet could not be decrypted/normalized and is dropped. Count
						// drops before the first decodable frame to quantify keyframe loss during warm-up.
						if (!_loggedFirstDecodableVideoFrame)
						{
							_droppedVideoBeforeFirstFrame++;
						}
						if (_decryptors.ShouldLogDecryptPayloadFailure(payloadType))
						{
							AppLog.Write($"AirPlay mirror video type={payloadType} payload could not be converted after decrypt attempt: size={payloadSize}, probe={MirrorH264Payloads.DescribeH264Probe(videoPayload)}.");
						}
					}
				continue;
			}

			if (_audioDiscoveryLogging)
			{
				long discoveryCount = _audioDiscoveryPacketCounts.TryGetValue(payloadType, out long existing) ? existing + 1 : 1;
				_audioDiscoveryPacketCounts[payloadType] = discoveryCount;
				// Log the first few of each non-video type (with head bytes to reveal RTP/ADTS/codec
				// structure) and then periodically, to identify the audio stream on the TCP data path.
				if (discoveryCount <= 5 || discoveryCount % 250 == 0)
				{
					AppLog.Write($"AUDIO-DISCOVERY data-stream packet: type={payloadType}, option={payloadOption}, size={payloadSize}, count={discoveryCount}, head={MirrorH264Payloads.DescribeHeadBytes(payload, 24)}");
				}
				continue;
			}

			if (MarkSkippedPacketType(payloadType))
			{
				AppLog.Write($"AirPlay mirror packet skipped: type={payloadType}, option={payloadOption}, size={payloadSize}");
			}
		}

		return emitted;
	}

	private bool TryBuildSpsPpsPayload(byte[] header, byte[] payload, out byte[] spsPps, out StreamConfig streamConfig)
	{
		spsPps = Array.Empty<byte>();
		streamConfig = new StreamConfig
		{
			Width = 1920,
			Height = 1080,
			Fps = _resolveAdvertisedFps(1920, 1080),
			Codec = "h264-annexb"
		};

		if (payload.Length < 11)
		{
			return false;
		}

		float sourceWidth = BitConverter.ToSingle(header, 40);
		float sourceHeight = BitConverter.ToSingle(header, 44);
		float width = BitConverter.ToSingle(header, 56);
		float height = BitConverter.ToSingle(header, 60);

		int spsSize = (payload[6] << 8) | payload[7];
		if (spsSize <= 0 || payload.Length < 8 + spsSize + 3)
		{
			return false;
		}

		int ppsCountOffset = 8 + spsSize;
		int ppsSizeOffset = ppsCountOffset + 1;
		int ppsOffset = ppsSizeOffset + 2;
		int ppsSize = (payload[ppsSizeOffset] << 8) | payload[ppsSizeOffset + 1];
		if (ppsSize <= 0 || payload.Length < ppsOffset + ppsSize)
		{
			return false;
		}

		spsPps = new byte[8 + spsSize + ppsSize];
		spsPps[0] = 0;
		spsPps[1] = 0;
		spsPps[2] = 0;
		spsPps[3] = 1;
		Buffer.BlockCopy(payload, 8, spsPps, 4, spsSize);
		spsPps[4 + spsSize] = 0;
		spsPps[5 + spsSize] = 0;
		spsPps[6 + spsSize] = 0;
		spsPps[7 + spsSize] = 1;
		Buffer.BlockCopy(payload, ppsOffset, spsPps, 8 + spsSize, ppsSize);
		int streamWidth = RoundPositiveDimension(sourceWidth, RoundPositiveDimension(width, 1920));
		int streamHeight = RoundPositiveDimension(sourceHeight, RoundPositiveDimension(height, 1080));
		streamConfig = new StreamConfig
		{
			Width = streamWidth,
			Height = streamHeight,
			Fps = _resolveAdvertisedFps(streamWidth, streamHeight),
			Codec = "h264-annexb"
		};
		AppLog.Write($"AirPlay mirror SPS/PPS received: source={sourceWidth}x{sourceHeight}, display={width}x{height}, sps={spsSize}, pps={ppsSize}.");
		return true;
	}

	private static int RoundPositiveDimension(float value, int fallback)
	{
		if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0)
		{
			return fallback;
		}

		return Math.Clamp((int)Math.Round(value), 2, 8192);
	}

	private void RecordVideoPayloadShape(byte[] annexB)
	{
		bool hasIdr = false;
		bool hasSlice = false;
		bool hasParamSet = false;
		bool hasSei = false;
		int nalCount = 0;
		int offset = 0;
		while (true)
		{
			int start = MirrorH264Payloads.FindStartCode(annexB, offset, annexB.Length - 3);
			if (start < 0)
			{
				break;
			}
			int nalOffset = start + MirrorH264Payloads.StartCodeLengthAt(annexB, start);
			if (nalOffset >= annexB.Length)
			{
				break;
			}
			nalCount++;
			switch (annexB[nalOffset] & 0x1F)
			{
				case 5:
					hasIdr = true;
					break;
				case 1:
					hasSlice = true;
					break;
				case 7:
				case 8:
					hasParamSet = true;
					break;
				case 6:
					hasSei = true;
					break;
			}
			offset = nalOffset + 1;
		}

		if (!_loggedFirstDecodableVideoFrame)
		{
			_loggedFirstDecodableVideoFrame = true;
			long warmupMs = _firstEncryptedWaitTick == 0
				? 0L
				: (long)((Stopwatch.GetTimestamp() - _firstEncryptedWaitTick) * 1000.0 / Stopwatch.Frequency);
			AppLog.Write(
				$"AirPlay mirror first decodable video frame: keyframe(IDR)={hasIdr}, " +
				$"droppedBeforeFirst={_droppedVideoBeforeFirstFrame}, decryptorWarmup={warmupMs}ms." +
				(hasIdr
					? string.Empty
					: " WARNING: first surviving frame is not a keyframe; the single initial IDR was lost during decryptor warm-up and AirPlay mirror streams send no further keyframes, so the decoder stays starved."));
		}

		string? summary = null;
		lock (_payloadShapeGate)
		{
			long now = Stopwatch.GetTimestamp();
			if (_shapeWindowStartTick == 0)
			{
				_shapeWindowStartTick = now;
			}

			_shapePayloads++;
			_shapeBytes += annexB.Length;
			if (hasIdr)
			{
				_shapeIdr++;
			}
			else if (hasSlice)
			{
				_shapeSlice++;
			}
			else if (hasParamSet)
			{
				_shapeParamSetOnly++;
			}
			else if (hasSei)
			{
				_shapeSeiOnly++;
			}
			else
			{
				_shapeOther++;
			}
			if (nalCount > 1)
			{
				_shapeMultiNal++;
			}

			double seconds = (now - _shapeWindowStartTick) / (double)Stopwatch.Frequency;
			if (seconds >= 10.0)
			{
				summary = $"AirPlay mirror video payload shape ({seconds:N1}s): payloads={_shapePayloads} ({_shapePayloads / seconds:N1}/s), " +
					$"idr={_shapeIdr}, slice={_shapeSlice}, paramSetOnly={_shapeParamSetOnly}, seiOnly={_shapeSeiOnly}, other={_shapeOther}, " +
					$"multiNal={_shapeMultiNal}, avgSize={(_shapePayloads > 0 ? _shapeBytes / _shapePayloads : 0):N0}.";
				_shapeWindowStartTick = now;
				_shapePayloads = 0;
				_shapeIdr = 0;
				_shapeSlice = 0;
				_shapeParamSetOnly = 0;
				_shapeSeiOnly = 0;
				_shapeOther = 0;
				_shapeMultiNal = 0;
				_shapeBytes = 0;
			}
		}

		if (summary != null)
		{
			AppLog.Write(summary);
		}
	}

	// True the first time per key set; later calls return false until ResetNotices.
	private bool MarkWaitingForDecryptor()
	{
		lock (_noticeGate)
		{
			if (_loggedWaitingForMirrorDecryptor)
			{
				return false;
			}
			_loggedWaitingForMirrorDecryptor = true;
			return true;
		}
	}

	private bool MarkSkippedPacketType(int payloadType)
	{
		lock (_noticeGate)
		{
			return _loggedSkippedPacketTypes.Add(payloadType);
		}
	}
}
