using System;
using System.Collections.Generic;

namespace MacMirrorReceiver.Networking;

// A candidate that decrypted FirstPayload (and has consumed it), held until the next packet confirms it.
// Tested is its 1-based position in the sweep over FirstPayload, so a search can resume after it.
internal sealed record PendingMirrorDecryptor(
	IReadOnlyList<AirPlayMirrorDecryptorCandidate> BaseCandidates,
	byte[] FirstPayload,
	AirPlayMirrorDecryptor Decryptor,
	byte[] FirstAnnexB,
	string FirstFormat,
	int Tested) : IDisposable
{
	public void Dispose()
	{
		Decryptor.Dispose();
	}
}

internal enum MirrorDecryptorSelectionStatus
{
	NotFound,
	Held,
	Selected
}

// Outcome of AirPlayProbeService.SelectMirrorDecryptor for one packet. Selected carries the committed
// decryptor and the frames to emit in order (the held first frame, then this packet's); Held carries the
// new pending candidate and no frames. Rejected names held candidates the packet failed to confirm.
internal sealed class MirrorDecryptorSelection
{
	private MirrorDecryptorSelection(
		MirrorDecryptorSelectionStatus status,
		AirPlayMirrorDecryptor? decryptor,
		PendingMirrorDecryptor? pending,
		string format,
		int tested,
		IReadOnlyList<byte[]> annexBFrames,
		IReadOnlyList<string> rejected)
	{
		Status = status;
		Decryptor = decryptor;
		Pending = pending;
		Format = format;
		Tested = tested;
		AnnexBFrames = annexBFrames;
		Rejected = rejected;
	}

	public MirrorDecryptorSelectionStatus Status { get; }

	public AirPlayMirrorDecryptor? Decryptor { get; }

	public PendingMirrorDecryptor? Pending { get; }

	public string Format { get; }

	public int Tested { get; }

	public IReadOnlyList<byte[]> AnnexBFrames { get; }

	public IReadOnlyList<string> Rejected { get; }

	public static MirrorDecryptorSelection Selected(AirPlayMirrorDecryptor decryptor, string format, int tested, IReadOnlyList<byte[]> annexBFrames, IReadOnlyList<string> rejected)
	{
		return new MirrorDecryptorSelection(MirrorDecryptorSelectionStatus.Selected, decryptor, null, format, tested, annexBFrames, rejected);
	}

	public static MirrorDecryptorSelection Held(PendingMirrorDecryptor pending, IReadOnlyList<string> rejected)
	{
		return new MirrorDecryptorSelection(MirrorDecryptorSelectionStatus.Held, null, pending, pending.FirstFormat, pending.Tested, Array.Empty<byte[]>(), rejected);
	}

	public static MirrorDecryptorSelection NotFound(int tested, IReadOnlyList<string> rejected)
	{
		return new MirrorDecryptorSelection(MirrorDecryptorSelectionStatus.NotFound, null, null, string.Empty, tested, Array.Empty<byte[]>(), rejected);
	}
}
