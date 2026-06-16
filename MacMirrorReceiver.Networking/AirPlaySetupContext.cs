using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace MacMirrorReceiver.Networking;

internal sealed class AirPlaySetupContext
{
	private const int RaopEventPort = 5000;
	private const int DataPort = 7100;
	private const int TimingPort = 7102;

	private readonly AirPlayTimingClient _timingClient;
	private readonly AirPlayAudioReceiver _audioReceiver;
	private readonly Func<AirPlayAudioCrypto?> _audioCryptoProvider;

	public AirPlaySetupContext(
		AirPlayTimingClient timingClient,
		AirPlayAudioReceiver audioReceiver,
		Func<AirPlayAudioCrypto?> audioCryptoProvider)
	{
		_timingClient = timingClient;
		_audioReceiver = audioReceiver;
		_audioCryptoProvider = audioCryptoProvider;
	}

	public byte[] BuildSetupResponse(byte[] requestBody, IPAddress? remoteAddress)
	{
		Dictionary<string, object?>? request = AirPlayBinaryPlist.Read(requestBody) as Dictionary<string, object?>;
		if (request == null)
		{
			AppLog.Write("AirPlay SETUP request was not a readable binary plist; using fallback response.");
			return BuildFallbackResponse();
		}

		List<Dictionary<string, object?>> requestStreams = ReadStreams(request);
		int timingPort = ReadInt(request, "timingPort") ?? 0;
		AppLog.Write($"AirPlay SETUP parsed keys={string.Join(",", request.Keys)}, timingPort={timingPort}, streamTypes={string.Join(",", requestStreams.Select(s => ReadInt(s, "type")?.ToString() ?? "?"))}.");
		if (remoteAddress != null && timingPort > 0)
		{
			_timingClient.Start(remoteAddress, timingPort);
		}

		Dictionary<string, object> response = new Dictionary<string, object>
		{
			["eventPort"] = RaopEventPort,
			["timingPort"] = TimingPort
		};

		if (requestStreams.Count > 0)
		{
			List<object> streams = new List<object>();
			foreach (Dictionary<string, object?> requestStream in requestStreams)
			{
				int streamType = ReadInt(requestStream, "type") ?? -1;
				if (streamType == 110)
				{
					streams.Add(new Dictionary<string, object>
					{
						["dataPort"] = DataPort,
						["type"] = 110
					});
				}
				else if (streamType == 96)
				{
					Dictionary<string, object>? audioStream = BuildAudioStreamResponse(requestStream, remoteAddress);
					if (audioStream != null)
					{
						streams.Add(audioStream);
					}
				}
				else
				{
					AppLog.Write($"AirPlay SETUP ignored unsupported stream type={streamType}.");
				}
			}

			if (streams.Count > 0)
			{
				response["streams"] = streams;
			}
		}

		return AirPlayBinaryPlist.Write(response);
	}

	// type=96 is the AirPlay 2 screen-mirroring audio stream (AAC-ELD over UDP RTP). The sender
	// gives its own controlPort; we must open our own UDP data/control sockets and answer with the
	// actual bound ports (the previous hardcoded TCP dataPort=7100 / controlPort=5000 meant the
	// Mac sent audio nowhere and the session played silently). The FairPlay-unwrapped session AES
	// key + eiv (from the et=32 session SETUP) are handed to the receiver for decryption.
	private Dictionary<string, object>? BuildAudioStreamResponse(Dictionary<string, object?> requestStream, IPAddress? remoteAddress)
	{
		int macControlPort = ReadInt(requestStream, "controlPort") ?? 0;
		AirPlayAudioStreamInfo info = new AirPlayAudioStreamInfo(
			CompressionType: ReadInt(requestStream, "ct") ?? 0,
			SamplesPerFrame: ReadInt(requestStream, "spf") ?? 0,
			SampleRate: ReadInt(requestStream, "sr") ?? 44100,
			Channels: ReadInt(requestStream, "ch") ?? 2,
			AudioFormat: ReadLong(requestStream, "audioFormat") ?? 0,
			RedundantAudio: ReadInt(requestStream, "redundantAudio") ?? 0);

		AirPlayAudioCrypto? crypto = _audioCryptoProvider();
		if (crypto == null)
		{
			AppLog.Write("AirPlay SETUP audio stream (type=96) received before FairPlay session key was available; audio decryption will be unavailable.");
		}

		if (!_audioReceiver.Start(remoteAddress, macControlPort, info, crypto))
		{
			AppLog.Write("AirPlay SETUP audio stream (type=96) could not start UDP receiver; responding without an audio stream.");
			return null;
		}

		return new Dictionary<string, object>
		{
			["type"] = 96,
			["dataPort"] = _audioReceiver.DataPort,
			["controlPort"] = _audioReceiver.ControlPort
		};
	}

	private static byte[] BuildFallbackResponse()
	{
		return AirPlayBinaryPlist.Write(new Dictionary<string, object>
		{
			["eventPort"] = RaopEventPort,
			["timingPort"] = TimingPort,
			["streams"] = new List<object>
			{
				new Dictionary<string, object>
				{
					["dataPort"] = DataPort,
					["type"] = 110
				}
			}
		});
	}

	private static List<Dictionary<string, object?>> ReadStreams(Dictionary<string, object?> request)
	{
		if (!request.TryGetValue("streams", out object? streamsObject) || streamsObject is not List<object?> streams)
		{
			return new List<Dictionary<string, object?>>();
		}

		return streams.OfType<Dictionary<string, object?>>().ToList();
	}

	private static int? ReadInt(Dictionary<string, object?> dictionary, string key)
	{
		long? value = ReadLong(dictionary, key);
		return value.HasValue ? checked((int)value.Value) : null;
	}

	private static long? ReadLong(Dictionary<string, object?> dictionary, string key)
	{
		if (!dictionary.TryGetValue(key, out object? value))
		{
			return null;
		}

		return value switch
		{
			byte number => number,
			short number => number,
			int number => number,
			long number => number,
			ulong number => unchecked((long)number),
			_ => null
		};
	}
}
