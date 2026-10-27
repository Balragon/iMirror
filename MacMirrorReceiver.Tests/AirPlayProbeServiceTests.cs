using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MacMirrorReceiver.Networking;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class AirPlayProbeServiceTests
{
	[Theory]
	[InlineData("AirPlay", true)]
	[InlineData("airplay", true)]
	[InlineData("RAOP", false)]
	[InlineData("AirPlay data", false)]
	[InlineData("", false)]
	public void IsMirrorControlLabel_MatchesOnlyAirPlayControlConnection(string label, bool expected)
	{
		Assert.Equal(expected, AirPlayProbeService.IsMirrorControlLabel(label));
	}

	[Fact]
	public async Task StartAsync_CleansUpPartialMdnsStartAndCanRetry()
	{
		using UdpClient blocker = BindExclusiveUdpPort();
		int mdnsPort = ((IPEndPoint)blocker.Client.LocalEndPoint!).Port;
		using var service = new AirPlayProbeService("iMirror Test", mdnsPort: mdnsPort);

		await service.StartAsync();

		Assert.False(service.IsMdnsBound);
		Assert.False(service.IsAirPlayListenerBound);

		blocker.Dispose();
		await service.StartAsync();

		Assert.True(service.IsMdnsBound);
	}

	[Theory]
	[InlineData("standard", 0, false, "standard")]
	[InlineData("standard", 7, false, "standard-payload-offset7")]
	[InlineData("standard-skip5", 0, false, "standard-skip5")]
	[InlineData("uxplay-direct-eiv-reverse", 0, false, "uxplay-direct-eiv-reverse")]
	[InlineData("rtsp-target-signed-stream-id", 0, false, "rtsp-target-signed-stream-id")]
	[InlineData("standard", 0, true, "standard")]
	public void TryFindMirrorDecryptor_SelectsSameCandidateAsFullDecryptSweep(string encryptingCandidate, int clearPrefixBytes, bool annexB, string expectedCandidate)
	{
		Random random = new Random(21);
		IReadOnlyList<AirPlayMirrorDecryptorCandidate> candidates = BuildMacLikeCandidates(random);
		AirPlayMirrorDecryptorCandidate sender = candidates.Single(candidate => candidate.Name == encryptingCandidate);
		byte[] firstFrame = annexB ? BuildAnnexBPayload(random, 3000) : BuildAvccPayload(random, 3000, idr: true);
		byte[] nextFrame = BuildAvccPayload(random, 1500, idr: false);
		byte[] encryptedFirst;
		byte[] encryptedNext;
		using (AirPlayMirrorDecryptor senderStream = AirPlayMirrorDecryptor.Create(sender))
		{
			// Bytes ahead of the encrypted body are left in the clear, which is what a payload-offset candidate expects.
			encryptedFirst = RandomBytes(random, clearPrefixBytes).Concat(senderStream.Decrypt(firstFrame)).ToArray();
			encryptedNext = RandomBytes(random, clearPrefixBytes).Concat(senderStream.Decrypt(nextFrame)).ToArray();
		}

		SweepResult expected = FullDecryptSweep(candidates, encryptedFirst);
		List<string> report = new List<string>();
		bool found = AirPlayProbeService.TryFindMirrorDecryptor(candidates, encryptedFirst, report, out AirPlayMirrorDecryptor? decryptor, out byte[] selectedAnnexB, out string format, out int tested);

		Assert.Equal(expectedCandidate, expected.Name);
		Assert.True(found);
		using (decryptor)
		{
			Assert.Equal(expected.Name, decryptor!.Name);
			Assert.Equal(expected.AnnexB, selectedAnnexB);
			Assert.Equal(expected.Format, format);
			Assert.Equal(expected.Tested, tested);
			Assert.Equal(expected.Report, report);
			// The selected decryptor has consumed the first frame, so it decrypts the next one in place.
			Assert.Equal(nextFrame, decryptor.Decrypt(encryptedNext));
		}
	}

	[Fact]
	public void TryFindMirrorDecryptor_ReportsSameMissAsFullDecryptSweep()
	{
		Random random = new Random(22);
		IReadOnlyList<AirPlayMirrorDecryptorCandidate> candidates = BuildMacLikeCandidates(random);
		byte[] undecryptable = RandomBytes(random, 2000);

		SweepResult expected = FullDecryptSweep(candidates, undecryptable);
		List<string> report = new List<string>();
		bool found = AirPlayProbeService.TryFindMirrorDecryptor(candidates, undecryptable, report, out AirPlayMirrorDecryptor? decryptor, out _, out _, out int tested);

		Assert.Null(expected.Name);
		Assert.False(found);
		Assert.Null(decryptor);
		Assert.Equal(expected.Tested, tested);
		Assert.Equal(expected.Report, report);
	}

	[Fact]
	public void MayBeClearH264Payload_AcceptsEveryPayloadThatNormalizes()
	{
		Random random = new Random(23);
		int shapes = 0;
		foreach (byte[] payload in NormalizablePayloadShapes(random))
		{
			shapes++;
			byte[] prefix = payload.Take(AirPlayProbeService.ClearH264ProbeBytes).ToArray();
			string description = $"length={payload.Length}, head={Convert.ToHexString(prefix)}";
			Assert.True(AirPlayProbeService.TryNormalizeClearH264Payload(payload, out _, out _), description);
			Assert.True(AirPlayProbeService.MayBeClearH264Payload(prefix, payload.Length), description);
		}

		Assert.True(shapes > 8_000, $"shapes={shapes}");
	}

	[Fact]
	public void MayBeClearH264Payload_RejectsRandomBytes()
	{
		Random random = new Random(24);
		int accepted = 0;
		for (int i = 0; i < 10_000; i++)
		{
			byte[] payload = RandomBytes(random, 4096);
			if (AirPlayProbeService.MayBeClearH264Payload(payload, payload.Length))
			{
				accepted++;
			}
		}

		Assert.True(accepted <= 1, $"accepted={accepted}");
	}

	private sealed record SweepResult(string? Name, byte[] AnnexB, string Format, int Tested, List<string> Report);

	// Candidate selection as it ran before prefix screening: decrypt the whole payload for each
	// candidate and keep the first one that normalizes to clear H.264.
	private static SweepResult FullDecryptSweep(IReadOnlyList<AirPlayMirrorDecryptorCandidate> baseCandidates, byte[] payload)
	{
		List<string> report = new List<string>();
		int tested = 0;
		foreach (AirPlayMirrorDecryptorCandidate baseCandidate in baseCandidates)
		{
			foreach (AirPlayMirrorDecryptorCandidate candidate in AirPlayProbeService.ExpandPayloadOffsetCandidates(baseCandidate, payload.Length))
			{
				tested++;
				byte[] preview;
				using (AirPlayMirrorDecryptor decryptor = AirPlayMirrorDecryptor.Create(candidate))
				{
					preview = decryptor.Decrypt(payload);
				}

				if (AirPlayProbeService.TryNormalizeClearH264Payload(preview, out byte[] annexB, out string format))
				{
					return new SweepResult(candidate.Name, annexB, format, tested, report);
				}

				if (report.Count < 32)
				{
					report.Add(candidate.Name + "/" + AirPlayProbeService.DescribeH264Probe(preview));
				}
			}
		}

		return new SweepResult(null, Array.Empty<byte>(), string.Empty, tested, report);
	}

	// Stream and RTSP target IDs with the top bit set, as a Mac sends them, so the signed-ID
	// variants are generated too and 'standard' sits ~2,400 candidates into the sweep.
	private static IReadOnlyList<AirPlayMirrorDecryptorCandidate> BuildMacLikeCandidates(Random random)
	{
		return AirPlayMirrorDecryptor.BuildCandidates(
			RandomBytes(random, 16),
			RandomBytes(random, 32),
			streamConnectionId: 15941247475471896336UL,
			eiv: RandomBytes(random, 16),
			rtspTargetSessionId: 13994913985331938571UL);
	}

	// Two AVCC NAL units like a Mac's first frame (avcc-be/nal=2): a short SEI and a slice.
	private static byte[] BuildAvccPayload(Random random, int size, bool idr)
	{
		byte[] payload = RandomBytes(random, size);
		const int seiLength = 16;
		BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0, 4), seiLength);
		payload[4] = 0x06;
		BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4 + seiLength, 4), size - 8 - seiLength);
		payload[8 + seiLength] = idr ? (byte)0x65 : (byte)0x41;
		return payload;
	}

	private static byte[] BuildAnnexBPayload(Random random, int size)
	{
		byte[] payload = RandomBytes(random, size);
		payload[0] = 0;
		payload[1] = 0;
		payload[2] = 0;
		payload[3] = 1;
		payload[4] = 0x65;
		return payload;
	}

	// Payloads TryNormalizeClearH264Payload can accept: AVCC NAL units behind 0-64 leading bytes and
	// Annex B start codes, every allowed NAL header, lengths from the 5-byte minimum to past the
	// screened prefix.
	private static IEnumerable<byte[]> NormalizablePayloadShapes(Random random)
	{
		byte[] nalTypes = { 1, 5, 6, 7, 8, 9, 14 };
		for (int length = 5; length <= 160; length++)
		{
			for (int offset = 0; offset <= Math.Min(64, length - 5); offset++)
			{
				byte[] avcc = RandomBytes(random, length);
				int firstNalLength = length - offset - 4;
				if (firstNalLength > 8 && random.Next(2) == 0)
				{
					// A second NAL unit, so the parse has to follow the first length to the end.
					int secondNalLength = random.Next(1, firstNalLength - 4);
					firstNalLength -= secondNalLength + 4;
					int secondOffset = offset + 4 + firstNalLength;
					BinaryPrimitives.WriteInt32BigEndian(avcc.AsSpan(secondOffset, 4), secondNalLength);
					avcc[secondOffset + 4] = NalHeader(random, nalTypes);
				}

				BinaryPrimitives.WriteInt32BigEndian(avcc.AsSpan(offset, 4), firstNalLength);
				avcc[offset + 4] = NalHeader(random, nalTypes);
				yield return avcc;
			}

			foreach (int startCodeLength in new[] { 3, 4 })
			{
				if (length <= startCodeLength)
				{
					continue;
				}

				byte[] annexB = RandomBytes(random, length);
				annexB[0] = 0;
				annexB[1] = 0;
				annexB[2] = 0;
				annexB[startCodeLength - 1] = 1;
				annexB[startCodeLength] = NalHeader(random, nalTypes);
				yield return annexB;
			}
		}
	}

	private static byte NalHeader(Random random, byte[] nalTypes)
	{
		return (byte)((random.Next(4) << 5) | nalTypes[random.Next(nalTypes.Length)]);
	}

	private static byte[] RandomBytes(Random random, int count)
	{
		byte[] bytes = new byte[count];
		random.NextBytes(bytes);
		return bytes;
	}

	[Fact]
	public async Task ReadRequestAsync_ParsesHeadersAndBody()
	{
		byte[] raw = Encoding.ASCII.GetBytes("SETUP rtsp://x/1 RTSP/1.0\r\nCSeq: 3\r\nContent-Length: 5\r\n\r\nhello");
		using var stream = new MemoryStream(raw);

		var request = await AirPlayProbeService.ReadRequestAsync(stream, CancellationToken.None);

		Assert.NotNull(request);
		Assert.Equal("SETUP", request!.Method);
		Assert.Equal("rtsp://x/1", request.Target);
		Assert.Equal("3", request.Headers["CSeq"]);
		Assert.Equal("hello", Encoding.ASCII.GetString(request.Body));
	}

	[Theory]
	[InlineData("2000000000")]
	[InlineData("1048577")]
	public async Task ReadRequestAsync_RejectsOversizedContentLengthWithoutAllocating(string contentLength)
	{
		byte[] raw = Encoding.ASCII.GetBytes($"POST /feedback RTSP/1.0\r\nContent-Length: {contentLength}\r\n\r\n");
		using var stream = new MemoryStream(raw);

		var request = await AirPlayProbeService.ReadRequestAsync(stream, CancellationToken.None);

		Assert.Null(request);
	}

	[Theory]
	[InlineData("-5")]
	[InlineData("abc")]
	[InlineData("")]
	public async Task ReadRequestAsync_TreatsUnparsableContentLengthAsEmptyBody(string contentLength)
	{
		byte[] raw = Encoding.ASCII.GetBytes($"GET /info RTSP/1.0\r\nContent-Length: {contentLength}\r\n\r\n");
		using var stream = new MemoryStream(raw);

		var request = await AirPlayProbeService.ReadRequestAsync(stream, CancellationToken.None);

		Assert.NotNull(request);
		Assert.Empty(request!.Body);
	}

	[Fact]
	public async Task ReadRequestAsync_RejectsHeaderBlockLargerThanCap()
	{
		byte[] raw = Encoding.ASCII.GetBytes("GET /info RTSP/1.0\r\nX: " + new string('a', AirPlayProbeService.MaxRequestHeaderBytes) + "\r\n\r\n");
		using var stream = new MemoryStream(raw);

		var request = await AirPlayProbeService.ReadRequestAsync(stream, CancellationToken.None);

		Assert.Null(request);
	}

	[Fact]
	public async Task ReadRequestAsync_TimesOutWhenBodyNeverArrives()
	{
		byte[] raw = Encoding.ASCII.GetBytes("POST /feedback RTSP/1.0\r\nContent-Length: 10\r\n\r\n");
		using var stream = new StallingStream(raw);

		var request = await AirPlayProbeService.ReadRequestAsync(stream, CancellationToken.None, TimeSpan.FromMilliseconds(200));

		Assert.Null(request);
	}

	[Fact]
	public async Task ReadRequestAsync_DoesNotTimeOutWhileWaitingForTheFirstByte()
	{
		// The control connection idles between feedback posts; the timeout must only start once a
		// request has begun, so an idle stream stays open until the caller cancels.
		using var stream = new StallingStream(Array.Empty<byte>());
		using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => AirPlayProbeService.ReadRequestAsync(stream, cts.Token, TimeSpan.FromMilliseconds(50)));
	}

	/// <summary>Serves a fixed prefix and then blocks every further read until cancelled.</summary>
	private sealed class StallingStream : Stream
	{
		private readonly byte[] _prefix;
		private int _position;

		public StallingStream(byte[] prefix) => _prefix = prefix;

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			if (_position < _prefix.Length)
			{
				int count = Math.Min(buffer.Length, _prefix.Length - _position);
				_prefix.AsMemory(_position, count).CopyTo(buffer);
				_position += count;
				return count;
			}

			await Task.Delay(Timeout.Infinite, cancellationToken);
			return 0;
		}

		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
			=> ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
		public override void Flush() { }
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}

	private static UdpClient BindExclusiveUdpPort()
	{
		var client = new UdpClient(AddressFamily.InterNetwork);
		try
		{
			client.Client.ExclusiveAddressUse = true;
			client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
			return client;
		}
		catch
		{
			client.Dispose();
			throw;
		}
	}
}
