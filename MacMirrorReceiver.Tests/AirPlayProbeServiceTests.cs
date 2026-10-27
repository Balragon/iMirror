using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
	public void SelectMirrorDecryptor_HoldsFalseFirstMatchAndCommitsCandidateConfirmedByNextPacket()
	{
		Random random = new Random(31);
		IReadOnlyList<AirPlayMirrorDecryptorCandidate> candidates = BuildMacLikeCandidates(random);
		SenderStream stream = BuildFalseMatchStream(random, candidates, "standard", "rtsp-target-uxplay-direct");

		// The sweep alone settles on the decoy, as the 2026-10-27 09:27:26 session did.
		Assert.True(AirPlayProbeService.TryFindMirrorDecryptor(candidates, stream.Encrypted[0], new List<string>(), out AirPlayMirrorDecryptor? sweepWinner, out _, out string sweepFormat, out int decoyTested));
		using (sweepWinner)
		{
			Assert.Equal("rtsp-target-uxplay-direct", sweepWinner!.Name);
			Assert.Equal("avcc-be@8/nal=1", sweepFormat);
		}

		MirrorDecryptorSelection first = AirPlayProbeService.SelectMirrorDecryptor(candidates, stream.Encrypted[0], rememberedName: null, pending: null, new List<string>());
		Assert.Equal(MirrorDecryptorSelectionStatus.Held, first.Status);
		Assert.Equal("rtsp-target-uxplay-direct", first.Pending!.Decryptor.Name);
		Assert.Empty(first.AnnexBFrames);

		MirrorDecryptorSelection second = AirPlayProbeService.SelectMirrorDecryptor(candidates, stream.Encrypted[1], rememberedName: null, first.Pending, new List<string>());
		Assert.Equal(MirrorDecryptorSelectionStatus.Selected, second.Status);
		using AirPlayMirrorDecryptor decryptor = second.Decryptor!;
		Assert.Equal("standard", decryptor.Name);
		Assert.Equal(new[] { "rtsp-target-uxplay-direct" }, second.Rejected);
		Assert.True(second.Tested > decoyTested);
		Assert.Equal(new[] { Normalize(stream.Plain[0]), Normalize(stream.Plain[1]) }, second.AnnexBFrames);
		Assert.Equal(stream.Plain[2], decryptor.Decrypt(stream.Encrypted[2]));
	}

	[Theory]
	[InlineData("standard", 0, false)]
	[InlineData("standard", 7, false)]
	[InlineData("standard-skip5", 0, false)]
	[InlineData("uxplay-direct-eiv-reverse", 0, false)]
	[InlineData("standard", 0, true)]
	public void SelectMirrorDecryptor_CommitsSweepWinnerOnceNextPacketConfirmsIt(string encryptingCandidate, int clearPrefixBytes, bool annexB)
	{
		Random random = new Random(32);
		IReadOnlyList<AirPlayMirrorDecryptorCandidate> candidates = BuildMacLikeCandidates(random);
		SenderStream stream = BuildSenderStream(random, candidates, encryptingCandidate, clearPrefixBytes, annexB);
		List<string> sweepReport = new List<string>();
		Assert.True(AirPlayProbeService.TryFindMirrorDecryptor(candidates, stream.Encrypted[0], sweepReport, out AirPlayMirrorDecryptor? sweepWinner, out byte[] sweepAnnexB, out string sweepFormat, out int sweepTested));
		sweepWinner!.Dispose();

		List<string> report = new List<string>();
		MirrorDecryptorSelection first = AirPlayProbeService.SelectMirrorDecryptor(candidates, stream.Encrypted[0], rememberedName: null, pending: null, report);
		Assert.Equal(MirrorDecryptorSelectionStatus.Held, first.Status);
		Assert.Equal(sweepReport, report);
		MirrorDecryptorSelection second = AirPlayProbeService.SelectMirrorDecryptor(candidates, stream.Encrypted[1], rememberedName: null, first.Pending, new List<string>());

		// Same candidate, frame, format and position as the sweep: confirmation only delays the first frame by one packet.
		Assert.Equal(MirrorDecryptorSelectionStatus.Selected, second.Status);
		using AirPlayMirrorDecryptor decryptor = second.Decryptor!;
		Assert.Equal(sweepWinner.Name, decryptor.Name);
		Assert.Equal(sweepFormat, second.Format);
		Assert.Equal(sweepTested, second.Tested);
		Assert.Empty(second.Rejected);
		Assert.Equal(new[] { sweepAnnexB, Normalize(stream.Plain[1]) }, second.AnnexBFrames);
		Assert.Equal(stream.Plain[2], decryptor.Decrypt(stream.Encrypted[2]));
	}

	[Theory]
	[InlineData("standard", 0)]
	[InlineData("standard-payload-offset7", 7)]
	[InlineData("standard-skip5", 0)]
	[InlineData("rtsp-target-signed-stream-id", 0)]
	public void SelectMirrorDecryptor_CommitsRememberedCandidateOnFirstPacket(string encryptingCandidate, int clearPrefixBytes)
	{
		Random random = new Random(33);
		IReadOnlyList<AirPlayMirrorDecryptorCandidate> candidates = BuildMacLikeCandidates(random);
		string baseName = encryptingCandidate.Replace("-payload-offset7", string.Empty, StringComparison.Ordinal);
		SenderStream stream = BuildSenderStream(random, candidates, baseName, clearPrefixBytes, annexB: false);

		MirrorDecryptorSelection selection = AirPlayProbeService.SelectMirrorDecryptor(candidates, stream.Encrypted[0], encryptingCandidate, pending: null, new List<string>());

		Assert.Equal(MirrorDecryptorSelectionStatus.Selected, selection.Status);
		using AirPlayMirrorDecryptor decryptor = selection.Decryptor!;
		Assert.Equal(encryptingCandidate, decryptor.Name);
		Assert.Equal(1, selection.Tested);
		Assert.Equal(new[] { Normalize(stream.Plain[0]) }, selection.AnnexBFrames);
		Assert.Equal(stream.Plain[1], decryptor.Decrypt(stream.Encrypted[1]));
	}

	[Fact]
	public void SelectMirrorDecryptor_RememberedCandidateSkipsEarlierFalseMatch()
	{
		Random random = new Random(34);
		IReadOnlyList<AirPlayMirrorDecryptorCandidate> candidates = BuildMacLikeCandidates(random);
		SenderStream stream = BuildFalseMatchStream(random, candidates, "standard", "rtsp-target-uxplay-direct");

		MirrorDecryptorSelection selection = AirPlayProbeService.SelectMirrorDecryptor(candidates, stream.Encrypted[0], "standard", pending: null, new List<string>());

		Assert.Equal(MirrorDecryptorSelectionStatus.Selected, selection.Status);
		using AirPlayMirrorDecryptor decryptor = selection.Decryptor!;
		Assert.Equal("standard", decryptor.Name);
		Assert.Equal(new[] { Normalize(stream.Plain[0]) }, selection.AnnexBFrames);
		Assert.Equal(stream.Plain[1], decryptor.Decrypt(stream.Encrypted[1]));
	}

	[Fact]
	public void SelectMirrorDecryptor_HoldsSweepWinnerWhenRememberedCandidateFails()
	{
		Random random = new Random(35);
		IReadOnlyList<AirPlayMirrorDecryptorCandidate> candidates = BuildMacLikeCandidates(random);
		SenderStream stream = BuildSenderStream(random, candidates, "uxplay-direct-eiv-reverse", 0, annexB: false);

		MirrorDecryptorSelection first = AirPlayProbeService.SelectMirrorDecryptor(candidates, stream.Encrypted[0], "standard", pending: null, new List<string>());

		Assert.Equal(MirrorDecryptorSelectionStatus.Held, first.Status);
		Assert.Equal("uxplay-direct-eiv-reverse", first.Pending!.Decryptor.Name);
		MirrorDecryptorSelection second = AirPlayProbeService.SelectMirrorDecryptor(candidates, stream.Encrypted[1], "standard", first.Pending, new List<string>());
		Assert.Equal(MirrorDecryptorSelectionStatus.Selected, second.Status);
		using AirPlayMirrorDecryptor decryptor = second.Decryptor!;
		Assert.Equal("uxplay-direct-eiv-reverse", decryptor.Name);
		Assert.Equal(2, second.AnnexBFrames.Count);
	}

	[Fact]
	public void SelectMirrorDecryptor_ReportsMissWhenNoCandidateDecryptsBothPackets()
	{
		Random random = new Random(36);
		IReadOnlyList<AirPlayMirrorDecryptorCandidate> candidates = BuildMacLikeCandidates(random);
		SenderStream stream = BuildFalseMatchStream(random, candidates, "standard", "rtsp-target-uxplay-direct");
		MirrorDecryptorSelection first = AirPlayProbeService.SelectMirrorDecryptor(candidates, stream.Encrypted[0], rememberedName: null, pending: null, new List<string>());
		byte[] undecryptable = RandomBytes(random, 2000);

		MirrorDecryptorSelection second = AirPlayProbeService.SelectMirrorDecryptor(candidates, undecryptable, rememberedName: null, first.Pending, new List<string>());

		Assert.Equal(MirrorDecryptorSelectionStatus.NotFound, second.Status);
		Assert.Null(second.Decryptor);
		Assert.Null(second.Pending);
		Assert.Empty(second.AnnexBFrames);
		// 'rtsp-target-direct-fairplay' derives the decoy's key and IV, so it matches the first packet too.
		Assert.Equal(new[] { "rtsp-target-uxplay-direct", "standard", "rtsp-target-direct-fairplay" }, second.Rejected);
	}

	[Fact]
	public void TryFindMirrorDecryptor_SkipCandidatesResumesSweepAfterEarlierMatch()
	{
		Random random = new Random(37);
		IReadOnlyList<AirPlayMirrorDecryptorCandidate> candidates = BuildMacLikeCandidates(random);
		SenderStream stream = BuildFalseMatchStream(random, candidates, "standard", "rtsp-target-uxplay-direct");
		Assert.True(AirPlayProbeService.TryFindMirrorDecryptor(candidates, stream.Encrypted[0], new List<string>(), out AirPlayMirrorDecryptor? decoy, out _, out _, out int decoyTested));
		decoy!.Dispose();
		SweepResult expected = FullDecryptSweep(candidates.SkipWhile(candidate => candidate.Name != "standard").ToArray(), stream.Encrypted[0]);

		Assert.True(AirPlayProbeService.TryFindMirrorDecryptor(candidates, stream.Encrypted[0], new List<string>(), decoyTested, out AirPlayMirrorDecryptor? resumed, out byte[] annexB, out _, out int tested));

		using (resumed)
		{
			Assert.Equal("standard", resumed!.Name);
			Assert.Equal(expected.AnnexB, annexB);
			Assert.True(tested > decoyTested);
		}
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

	private sealed record SenderStream(byte[][] Plain, byte[][] Encrypted);

	// Three frames (an IDR, then two slices) encrypted in sequence by one sender stream, each behind
	// clearPrefixBytes of clear bytes.
	private static SenderStream BuildSenderStream(Random random, IReadOnlyList<AirPlayMirrorDecryptorCandidate> candidates, string encryptingCandidate, int clearPrefixBytes, bool annexB)
	{
		byte[][] plain =
		{
			annexB ? BuildAnnexBPayload(random, 3000) : BuildAvccPayload(random, 3000, idr: true),
			BuildAvccPayload(random, 1500, idr: false),
			BuildAvccPayload(random, 1200, idr: false)
		};
		return EncryptStream(random, candidates.Single(candidate => candidate.Name == encryptingCandidate), plain, clearPrefixBytes);
	}

	// Like BuildSenderStream, but the first frame's SEI body is chosen so that decoyCandidate, which
	// comes earlier in the sweep, decrypts it to one AVCC NAL unit at offset 8 spanning the rest of the
	// payload: a false match of the shape seen on 2026-10-27 (avcc-be@9/nal=1). The frame still
	// decrypts to the same valid two-NAL frame with the sender's key.
	private static SenderStream BuildFalseMatchStream(Random random, IReadOnlyList<AirPlayMirrorDecryptorCandidate> candidates, string encryptingCandidate, string decoyCandidate)
	{
		const int decoyNalOffset = 8;
		AirPlayMirrorDecryptorCandidate sender = candidates.Single(candidate => candidate.Name == encryptingCandidate);
		AirPlayMirrorDecryptorCandidate decoy = candidates.Single(candidate => candidate.Name == decoyCandidate);
		byte[] first = BuildAvccPayload(random, 3000, idr: true);
		byte[] decoyClear = new byte[5];
		BinaryPrimitives.WriteInt32BigEndian(decoyClear, first.Length - decoyNalOffset - 4);
		decoyClear[4] = 0x41;
		byte[] senderKeyStream = AirPlayMirrorDecryptor.DecryptPrefixOnce(sender, new byte[decoyNalOffset + 5], decoyNalOffset + 5);
		byte[] decoyKeyStream = AirPlayMirrorDecryptor.DecryptPrefixOnce(decoy, new byte[decoyNalOffset + 5], decoyNalOffset + 5);
		for (int i = 0; i < decoyClear.Length; i++)
		{
			int index = decoyNalOffset + i;
			first[index] = (byte)(decoyClear[i] ^ senderKeyStream[index] ^ decoyKeyStream[index]);
		}

		byte[][] plain = { first, BuildAvccPayload(random, 1500, idr: false), BuildAvccPayload(random, 1200, idr: false) };
		return EncryptStream(random, sender, plain, clearPrefixBytes: 0);
	}

	private static SenderStream EncryptStream(Random random, AirPlayMirrorDecryptorCandidate sender, byte[][] plain, int clearPrefixBytes)
	{
		using AirPlayMirrorDecryptor senderStream = AirPlayMirrorDecryptor.Create(sender);
		// Bytes ahead of the encrypted body are left in the clear, which is what a payload-offset candidate expects.
		byte[][] encrypted = plain.Select(frame => RandomBytes(random, clearPrefixBytes).Concat(senderStream.Decrypt(frame)).ToArray()).ToArray();
		return new SenderStream(plain, encrypted);
	}

	private static byte[] Normalize(byte[] clearPayload)
	{
		Assert.True(AirPlayProbeService.TryNormalizeClearH264Payload(clearPayload, out byte[] annexB, out _));
		return annexB;
	}

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
