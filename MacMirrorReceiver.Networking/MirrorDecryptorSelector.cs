using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using MacMirrorReceiver;
using MacMirrorReceiver.Protocol;

namespace MacMirrorReceiver.Networking;

/// <summary>
/// Owns the mirror session's key material (ekey, eiv, streamConnectionID, RTSP target session
/// id, the FairPlay-unwrapped AES key and the pair-verify shared secret) and chooses the
/// decryptor for the data stream: first the small stateful probe pool built as soon as the
/// keys are known, then, bounded per session, the full candidate sweep screened on a short
/// decrypted prefix. Also provides the audio receiver's crypto and the one-per-session
/// diagnostic snapshot when no candidate produced clear H.264.
///
/// Thread-safe: the data-receive thread decrypts and probes while the control thread observes
/// SETUP requests. Everything is guarded by <c>_gate</c>; nothing slow runs under it except the
/// stateful probe pool pass (a handful of AES passes over one payload).
/// </summary>
internal sealed class MirrorDecryptorSelector : IDisposable
{
	// Bound on the exhaustive key/IV/offset candidate sweep (~4,000 AES-CTR passes over the
	// packet per attempt). It exists to recover from an unknown key derivation once per
	// session, not to run on every packet that fails H.264 normalization.
	internal const int MaxDecryptorSweepAttemptsPerSession = 4;

	// TryNormalizeClearH264Payload settles on a first NAL unit using only this many leading bytes (an
	// AVCC length + NAL header at each probed offset, or a leading Annex B start code), and
	// DescribeH264Probe reads no further either.
	internal const int ClearH264ProbeBytes = MirrorH264Payloads.MaxH264PrefixProbeBytes + 5;

	private readonly object _gate = new object();
	private readonly Func<byte[], byte[]?> _decryptAesKey;
	private readonly Func<byte[]?> _getSharedSecret;
	private readonly Func<byte[]?> _getKeyMessage;
	private readonly bool _writeDiagnostics;

	private byte[]? _encryptedKey;
	private byte[]? _fairPlayAesKey;
	private byte[]? _sharedSecret;
	private byte[]? _eiv;
	private ulong? _streamConnectionId;
	private ulong? _rtspTargetSessionId;
	private ulong? _decryptorStreamConnectionId;
	private AirPlayMirrorDecryptor? _decryptor;
	private List<AirPlayMirrorDecryptor>? _probePool;
	private int _sweepAttempts;
	private readonly HashSet<int> _loggedDecryptPayloadFailureTypes = new HashSet<int>();
	private readonly HashSet<int> _loggedDecryptCandidateReportTypes = new HashSet<int>();
	private bool _loggedDecryptorInUse;
	private bool _diagnosticWritten;

	/// <param name="decryptAesKey">FairPlay unwrap of the SETUP ekey (AirPlayFairPlayContext.TryDecryptAesKey).</param>
	/// <param name="getSharedSecret">The pair-verify shared secret, when pairing has completed.</param>
	/// <param name="getKeyMessage">The fp-setup key message, for the diagnostic snapshot only.</param>
	/// <param name="writeDiagnostics">Whether the no-candidate snapshot may be written to disk.</param>
	public MirrorDecryptorSelector(Func<byte[], byte[]?> decryptAesKey, Func<byte[]?> getSharedSecret, Func<byte[]?> getKeyMessage, bool writeDiagnostics)
	{
		_decryptAesKey = decryptAesKey;
		_getSharedSecret = getSharedSecret;
		_getKeyMessage = getKeyMessage;
		_writeDiagnostics = writeDiagnostics;
	}

	/// <summary>Records the RTSP target session id from the SETUP URL.</summary>
	public void ObserveRtspTargetSessionId(ulong rtspTargetSessionId)
	{
		lock (_gate)
		{
			_rtspTargetSessionId = rtspTargetSessionId;
		}
	}

	/// <summary>Forgets the RTSP target session id when the owning session ends.</summary>
	public void ClearRtspTargetSessionId()
	{
		lock (_gate)
		{
			_rtspTargetSessionId = null;
		}
	}

	/// <summary>A new ekey starts a new key set: the chosen decryptor, pool and derived keys are dropped.</summary>
	public void ObserveEncryptedKey(byte[] encryptedKey)
	{
		lock (_gate)
		{
			_decryptor?.Dispose();
			_decryptor = null;
			DisposeProbePoolLocked();
			_streamConnectionId = null;
			_decryptorStreamConnectionId = null;
			_encryptedKey = MirrorH264Payloads.CopyOf(encryptedKey);
			_fairPlayAesKey = null;
			_sharedSecret = null;
			ResetSelectionLogsLocked();
		}
	}

	public void ObserveEiv(byte[] eiv)
	{
		lock (_gate)
		{
			_eiv = MirrorH264Payloads.CopyOf(eiv);
		}
	}

	/// <summary>
	/// Records the streamConnectionID. Returns true when it replaced a different one, which
	/// drops the chosen decryptor and pool (the caller resets its own per-stream logging too).
	/// </summary>
	public bool ObserveStreamConnectionId(ulong streamConnectionId)
	{
		lock (_gate)
		{
			bool replaced = false;
			if (_streamConnectionId != null && _streamConnectionId.Value != streamConnectionId)
			{
				_decryptor?.Dispose();
				_decryptor = null;
				DisposeProbePoolLocked();
				_decryptorStreamConnectionId = null;
				ResetSelectionLogsLocked();
				replaced = true;
			}
			_streamConnectionId = streamConnectionId;
			return replaced;
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			_decryptor?.Dispose();
			_decryptor = null;
			DisposeProbePoolLocked();
			_decryptorStreamConnectionId = null;
		}
	}

	private void ResetSelectionLogsLocked()
	{
		_loggedDecryptPayloadFailureTypes.Clear();
		_loggedDecryptCandidateReportTypes.Clear();
		_sweepAttempts = 0;
		_loggedDecryptorInUse = false;
		_diagnosticWritten = false;
	}

	// Provides the FairPlay-unwrapped session AES key + eiv to the audio receiver. The audio
	// realtime stream (type=96) reuses the same et=32 session ekey/eiv the video path already
	// captures in ObserveSetupRequest. The receiver combines the first 16 bytes of that key with
	// the pair-verify shared secret for the confirmed AAC-ELD RTP CBC key derivation.
	internal AirPlayAudioCrypto? TryGetAudioCrypto()
	{
		byte[]? encryptedKey;
		byte[]? eiv;
		ulong? rtspTargetSessionId;
		lock (_gate)
		{
			encryptedKey = _encryptedKey == null ? null : MirrorH264Payloads.CopyOf(_encryptedKey);
			eiv = _eiv == null ? null : MirrorH264Payloads.CopyOf(_eiv);
			rtspTargetSessionId = _rtspTargetSessionId;
		}

		if (encryptedKey == null)
		{
			return null;
		}

		byte[]? aesKey = _decryptAesKey(encryptedKey);
		if (aesKey == null)
		{
			return null;
		}

		byte[]? sharedSecret = _getSharedSecret();
		return new AirPlayAudioCrypto(aesKey, eiv, sharedSecret, encryptedKey, rtspTargetSessionId);
	}

	internal bool TryDecryptMirrorPayload(byte[] payload, out byte[] decryptedPayload)
	{
		lock (_gate)
		{
			if (_decryptor == null)
			{
				decryptedPayload = Array.Empty<byte>();
				return false;
			}

			decryptedPayload = _decryptor.Decrypt(payload);
			if (!_loggedDecryptorInUse)
			{
				_loggedDecryptorInUse = true;
				AppLog.Write($"AirPlay mirror decryptor in use: {_decryptor.Name}.");
			}
			return true;
		}
	}

	internal bool ShouldLogDecryptPayloadFailure(int payloadType)
	{
		lock (_gate)
		{
			return _loggedDecryptPayloadFailureTypes.Add(payloadType);
		}
	}

	internal bool TryProbeMirrorDecryptors(int payloadType, byte[] packetHeader, byte[] encryptedPayload, out byte[] annexB, out string selectedName, out string selectedFormat)
	{
		annexB = Array.Empty<byte>();
		selectedName = string.Empty;
		selectedFormat = string.Empty;

		byte[]? encryptedKey = null;
		byte[]? aesKey = null;
		byte[]? sharedSecret = null;
		byte[]? eiv = null;
		ulong? streamConnectionId = null;
		ulong? rtspTargetSessionId = null;
		List<string> report = new List<string>();
		int tested = 0;
		int baseCandidateCount = 0;
		bool shouldWriteDiagnostic = false;

		lock (_gate)
		{
			if (_probePool == null || _probePool.Count == 0)
			{
				return false;
			}

			baseCandidateCount = _probePool.Count;
			foreach (AirPlayMirrorDecryptor decryptor in _probePool)
			{
				tested++;
				byte[] preview = decryptor.Decrypt(encryptedPayload);
				if (MirrorH264Payloads.TryNormalizeClearH264Payload(preview, out byte[] candidateAnnexB, out string candidateFormat))
				{
					annexB = candidateAnnexB;
					selectedName = decryptor.Name;
					selectedFormat = candidateFormat;

					foreach (AirPlayMirrorDecryptor other in _probePool)
					{
						if (!ReferenceEquals(other, decryptor))
						{
							other.Dispose();
						}
					}

					_probePool = null;
					_decryptor?.Dispose();
					_decryptor = decryptor;
					_decryptorStreamConnectionId = _streamConnectionId;
					_loggedDecryptPayloadFailureTypes.Remove(payloadType);
					_loggedDecryptCandidateReportTypes.Add(payloadType);
					AppLog.Write($"AirPlay mirror decryptor stateful auto-selected '{decryptor.Name}' for type={payloadType}, streamConnectionID={_streamConnectionId}, format={candidateFormat}.");
					return true;
				}

				if (report.Count < 32)
				{
					report.Add(decryptor.Name + "/" + MirrorH264Payloads.DescribeH264Probe(preview));
				}
			}

			if (_loggedDecryptCandidateReportTypes.Add(payloadType))
			{
				AppLog.Write($"AirPlay mirror stateful decryptor probes did not produce clear H.264 for type={payloadType}. tested={tested}, probes=" + string.Join(", ", report));
				shouldWriteDiagnostic = true;
			}

			encryptedKey = _encryptedKey == null ? null : MirrorH264Payloads.CopyOf(_encryptedKey);
			aesKey = _fairPlayAesKey == null ? null : MirrorH264Payloads.CopyOf(_fairPlayAesKey);
			sharedSecret = _sharedSecret == null ? null : MirrorH264Payloads.CopyOf(_sharedSecret);
			eiv = _eiv == null ? null : MirrorH264Payloads.CopyOf(_eiv);
			streamConnectionId = _streamConnectionId;
			rtspTargetSessionId = _rtspTargetSessionId;
		}

		if (shouldWriteDiagnostic && aesKey != null && sharedSecret != null && streamConnectionId != null)
		{
			TryWriteMirrorDiagnosticSnapshot(payloadType, packetHeader, encryptedPayload, encryptedKey, aesKey, sharedSecret, eiv, streamConnectionId.Value, rtspTargetSessionId, tested, baseCandidateCount, report);
		}

		return false;
	}

	internal bool TrySelectMirrorDecryptor(int payloadType, byte[] packetHeader, byte[] encryptedPayload, out byte[] annexB, out string selectedName, out string selectedFormat)
	{
		byte[]? aesKey;
		byte[]? sharedSecret;
		byte[]? eiv;
		byte[]? encryptedKey;
		ulong? streamConnectionId;
		lock (_gate)
		{
			aesKey = _fairPlayAesKey == null ? null : MirrorH264Payloads.CopyOf(_fairPlayAesKey);
			sharedSecret = _sharedSecret == null ? null : MirrorH264Payloads.CopyOf(_sharedSecret);
			eiv = _eiv == null ? null : MirrorH264Payloads.CopyOf(_eiv);
			encryptedKey = _encryptedKey == null ? null : MirrorH264Payloads.CopyOf(_encryptedKey);
			streamConnectionId = _streamConnectionId;
		}

		annexB = Array.Empty<byte>();
		selectedName = string.Empty;
		selectedFormat = string.Empty;
		if (aesKey == null || sharedSecret == null || streamConnectionId == null)
		{
			return false;
		}

		List<string> report = new List<string>();
		ulong? rtspTargetSessionId;
		lock (_gate)
		{
			if (_decryptor != null)
			{
				// A decryptor is already selected for this session. A packet that still fails
				// H.264 normalization (e.g. a NAL type outside the plausibility whitelist) is a
				// dropped packet, not evidence that the key derivation changed; re-running the
				// full sweep here cost ~4,000 AES passes per packet and stalled the data loop.
				return false;
			}
			if (_sweepAttempts >= MaxDecryptorSweepAttemptsPerSession)
			{
				return false;
			}
			_sweepAttempts++;
			rtspTargetSessionId = _rtspTargetSessionId;
		}

		IReadOnlyList<AirPlayMirrorDecryptorCandidate> baseCandidates = AirPlayMirrorDecryptor.BuildCandidates(aesKey, sharedSecret, streamConnectionId.Value, eiv, rtspTargetSessionId);
		long searchStartTick = Stopwatch.GetTimestamp();
		if (TryFindMirrorDecryptor(baseCandidates, encryptedPayload, report, out AirPlayMirrorDecryptor? decryptor, out byte[] selectedAnnexB, out string selectedPayloadFormat, out int tested))
		{
			lock (_gate)
			{
				_decryptor?.Dispose();
				DisposeProbePoolLocked();
				_decryptor = decryptor;
				_decryptorStreamConnectionId = streamConnectionId.Value;
				_loggedDecryptPayloadFailureTypes.Remove(payloadType);
				_loggedDecryptCandidateReportTypes.Add(payloadType);
			}

			annexB = selectedAnnexB;
			selectedName = decryptor.Name;
			selectedFormat = selectedPayloadFormat;
			long searchMs = (long)((Stopwatch.GetTimestamp() - searchStartTick) * 1000.0 / Stopwatch.Frequency);
			AppLog.Write($"AirPlay mirror decryptor auto-selected '{decryptor.Name}' for type={payloadType}, streamConnectionID={streamConnectionId.Value}, format={selectedPayloadFormat}, tested={tested}, payloadBytes={encryptedPayload.Length}, search={searchMs}ms.");
			return true;
		}

		lock (_gate)
		{
			if (_loggedDecryptCandidateReportTypes.Add(payloadType))
			{
				AppLog.Write($"AirPlay mirror decryptor candidates did not produce clear H.264 for type={payloadType}. tested={tested}, baseCandidates={baseCandidates.Count}, probes=" + string.Join(", ", report));
			}
		}

		TryWriteMirrorDiagnosticSnapshot(payloadType, packetHeader, encryptedPayload, encryptedKey, aesKey, sharedSecret, eiv, streamConnectionId.Value, rtspTargetSessionId, tested, baseCandidates.Count, report);
		return false;
	}

	// Finds the first candidate, in BuildCandidates order with each base expanded over payload offsets,
	// whose decryption of encryptedPayload normalizes to clear H.264, and returns a decryptor that has
	// consumed encryptedPayload. A Mac sender's 'standard' key comes after ~2,400 candidates, and
	// decrypting the whole first IDR for each of them took seconds on the data-receive thread, so each
	// candidate is screened on a short decrypted prefix and only survivors decrypt the full payload.
	internal static bool TryFindMirrorDecryptor(
		IReadOnlyList<AirPlayMirrorDecryptorCandidate> baseCandidates,
		byte[] encryptedPayload,
		List<string> report,
		[NotNullWhen(true)] out AirPlayMirrorDecryptor? decryptor,
		out byte[] annexB,
		out string format,
		out int tested)
	{
		decryptor = null;
		annexB = Array.Empty<byte>();
		format = string.Empty;
		tested = 0;
		foreach (AirPlayMirrorDecryptorCandidate baseCandidate in baseCandidates)
		{
			foreach (AirPlayMirrorDecryptorCandidate candidate in ExpandPayloadOffsetCandidates(baseCandidate, encryptedPayload.Length))
			{
				tested++;
				byte[] preview = AirPlayMirrorDecryptor.DecryptPrefixOnce(candidate, encryptedPayload, ClearH264ProbeBytes);
				int decryptedLength = encryptedPayload.Length - Math.Max(0, candidate.PayloadOffset);
				if (MirrorH264Payloads.MayBeClearH264Payload(preview, decryptedLength))
				{
					AirPlayMirrorDecryptor candidateDecryptor = AirPlayMirrorDecryptor.Create(candidate);
					if (MirrorH264Payloads.TryNormalizeClearH264Payload(candidateDecryptor.Decrypt(encryptedPayload), out byte[] candidateAnnexB, out string candidateFormat))
					{
						decryptor = candidateDecryptor;
						annexB = candidateAnnexB;
						format = candidateFormat;
						return true;
					}

					candidateDecryptor.Dispose();
				}

				if (report.Count < 32)
				{
					report.Add(candidate.Name + "/" + MirrorH264Payloads.DescribeH264Probe(preview));
				}
			}
		}

		return false;
	}

	private void TryWriteMirrorDiagnosticSnapshot(
		int payloadType,
		byte[] packetHeader,
		byte[] encryptedPayload,
		byte[]? encryptedKey,
		byte[] aesKey,
		byte[] sharedSecret,
		byte[]? eiv,
		ulong streamConnectionId,
		ulong? rtspTargetSessionId,
		int testedCandidates,
		int baseCandidateCount,
		IReadOnlyList<string> candidateReport)
	{
		lock (_gate)
		{
			if (_diagnosticWritten)
			{
				return;
			}
			_diagnosticWritten = true;
		}

		if (!_writeDiagnostics)
		{
			AppLog.Write("AirPlay mirror diagnostic snapshot skipped; enable Write diagnostics in Settings or set IMIRROR_WRITE_DIAGNOSTICS=1.");
			return;
		}

		try
		{
			byte[]? keyMessage = _getKeyMessage();
			Dictionary<string, object?> snapshot = new Dictionary<string, object?>
			{
				["createdLocal"] = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
				["payloadType"] = payloadType,
				["testedCandidates"] = testedCandidates,
				["baseCandidateCount"] = baseCandidateCount,
				["streamConnectionId"] = streamConnectionId.ToString(CultureInfo.InvariantCulture),
				["rtspTargetSessionId"] = rtspTargetSessionId?.ToString(CultureInfo.InvariantCulture),
				["packetHeaderHex"] = Convert.ToHexString(packetHeader),
				["encryptedPayloadHex"] = Convert.ToHexString(encryptedPayload),
				["fpSetupKeyMessageHex"] = HexOrNull(keyMessage),
				["encryptedEkeyHex"] = HexOrNull(encryptedKey),
				["eivHex"] = HexOrNull(eiv),
				["fairPlayAesKeyHex"] = HexOrNull(aesKey),
				["fairPlayAesKeySha256"] = Sha256HexOrNull(aesKey),
				["pairVerifySharedSecretSha256"] = Sha256HexOrNull(sharedSecret),
				["candidateReport"] = candidateReport.ToArray()
			};

			string directory = AppPaths.DiagnosticsDirectory;
			Directory.CreateDirectory(directory);
			string fileName = "airplay-mirror-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".json";
			string path = Path.Combine(directory, fileName);
			File.WriteAllText(path, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
			AppLog.Write("AirPlay mirror diagnostic snapshot written: " + path);
		}
		catch (Exception ex)
		{
			AppLog.Write("AirPlay mirror diagnostic snapshot failed: " + ex.Message);
		}
	}

	private static string? HexOrNull(byte[]? bytes)
	{
		return bytes == null ? null : Convert.ToHexString(bytes);
	}

	private static string? Sha256HexOrNull(byte[]? bytes)
	{
		return bytes == null ? null : Convert.ToHexString(SHA256.HashData(bytes));
	}

	internal static IEnumerable<AirPlayMirrorDecryptorCandidate> ExpandPayloadOffsetCandidates(AirPlayMirrorDecryptorCandidate candidate, int payloadLength)
	{
		yield return candidate;
		if (candidate.InitialSkipBytes != 0 || candidate.PayloadOffset != 0)
		{
			yield break;
		}

		int maxOffset = Math.Min(MirrorH264Payloads.MaxH264PrefixProbeBytes, Math.Max(0, payloadLength - 5));
		for (int offset = 1; offset <= maxOffset; offset++)
		{
			yield return candidate with
			{
				Name = candidate.Name + "-payload-offset" + offset.ToString(CultureInfo.InvariantCulture),
				PayloadOffset = offset
			};
		}
	}

	internal bool TryInitializeMirrorDecryptor()
	{
		byte[]? encryptedKey;
		ulong? streamConnectionId;
		byte[]? eiv;
		ulong? rtspTargetSessionId;
				lock (_gate)
				{
					if ((_decryptor != null || _probePool != null) && _decryptorStreamConnectionId == _streamConnectionId)
					{
						return false;
					}

				encryptedKey = _encryptedKey == null ? null : MirrorH264Payloads.CopyOf(_encryptedKey);
				streamConnectionId = _streamConnectionId;
				eiv = _eiv == null ? null : MirrorH264Payloads.CopyOf(_eiv);
				rtspTargetSessionId = _rtspTargetSessionId;
			}

		if (encryptedKey == null || streamConnectionId == null)
		{
			return false;
		}

		byte[]? sharedSecret = _getSharedSecret();
		if (sharedSecret == null)
		{
			AppLog.Write("AirPlay mirror decryptor waiting for pair-verify shared secret.");
			return false;
		}

		byte[]? aesKey = _decryptAesKey(encryptedKey);
		if (aesKey == null)
		{
			return false;
		}

		List<AirPlayMirrorDecryptor> probePool = AirPlayMirrorDecryptor.BuildCandidates(aesKey, sharedSecret, streamConnectionId.Value, eiv, rtspTargetSessionId)
			.Where(IsStatefulMirrorProbeCandidate)
			.Select(AirPlayMirrorDecryptor.Create)
			.ToList();

		if (probePool.Count == 0)
		{
			AppLog.Write("AirPlay mirror decryptor probe pool could not be built.");
			return false;
		}

		lock (_gate)
		{
			DisposeProbePoolLocked();
			_fairPlayAesKey = MirrorH264Payloads.CopyOf(aesKey);
			_sharedSecret = MirrorH264Payloads.CopyOf(sharedSecret);
			_probePool = probePool;
			_decryptorStreamConnectionId = streamConnectionId.Value;
			ResetSelectionLogsLocked();
			AppLog.Write($"AirPlay mirror decryptor probe pool ready for streamConnectionID={streamConnectionId.Value}; candidates={probePool.Count}.");
		}
		return true;
	}

	private static bool IsStatefulMirrorProbeCandidate(AirPlayMirrorDecryptorCandidate candidate)
	{
		return candidate.InitialSkipBytes == 0 &&
			candidate.PayloadOffset == 0 &&
			candidate.Name.Contains("uxplay-direct", StringComparison.Ordinal);
	}

	private void DisposeProbePoolLocked()
	{
		if (_probePool == null)
		{
			return;
		}

		foreach (AirPlayMirrorDecryptor decryptor in _probePool)
		{
			decryptor.Dispose();
		}
		_probePool = null;
	}
}
