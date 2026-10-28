using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MacMirrorReceiver;

namespace MacMirrorReceiver.Networking;

/// <summary>One parsed RTSP/HTTP request from a sender's control connection.</summary>
internal sealed record AirPlayRequest(
	string Method,
	string Target,
	string Protocol,
	string FirstLine,
	Dictionary<string, string> Headers,
	byte[] Body);

/// <summary>
/// The RTSP/HTTP wire format of the AirPlay control connections: reading one request with
/// the size and time limits, and writing one response. Stateless; which method does what
/// stays in AirPlayProbeService.
/// </summary>
internal static class AirPlayRtspCodec
{
	/// <summary>The AirTunes version advertised in the Server header, /info and the TXT records.</summary>
	public const string AirTunesVersion = "220.68";

	internal const int MaxRequestHeaderBytes = 64 * 1024;
	internal const int MaxRequestBodyBytes = 1024 * 1024;
	internal static readonly TimeSpan DefaultRequestReadTimeout = TimeSpan.FromSeconds(15);

	/// <summary>
	/// Reads one RTSP/HTTP request. Waiting for the first byte of a request is unbounded (the
	/// control connection idles between the sender's periodic feedback posts), but once a request
	/// has started the rest of it must arrive within <paramref name="requestTimeout"/>, the header
	/// block is capped at <see cref="MaxRequestHeaderBytes"/>, and a declared body larger than
	/// <see cref="MaxRequestBodyBytes"/> is rejected before anything is allocated for it.
	/// Returns null when the connection should be closed.
	/// </summary>
	internal static async Task<AirPlayRequest?> ReadRequestAsync(Stream stream, CancellationToken token, TimeSpan? requestTimeout = null)
	{
		TimeSpan timeout = requestTimeout ?? DefaultRequestReadTimeout;
		using CancellationTokenSource requestCts = CancellationTokenSource.CreateLinkedTokenSource(token);
		try
		{
			return await ReadRequestCoreAsync(stream, requestCts, timeout);
		}
		catch (OperationCanceledException) when (!token.IsCancellationRequested)
		{
			AppLog.Write($"AirPlay request did not complete within {timeout.TotalSeconds:N0}s; closing connection.");
			return null;
		}
	}

	private static async Task<AirPlayRequest?> ReadRequestCoreAsync(Stream stream, CancellationTokenSource requestCts, TimeSpan timeout)
	{
		List<byte> headerBytes = new List<byte>();
		byte[] single = new byte[1];
		CancellationToken requestToken = requestCts.Token;
		bool headerComplete = false;
		while (headerBytes.Count < MaxRequestHeaderBytes)
		{
			int count = await stream.ReadAsync(single, requestToken);
			if (count == 0)
			{
				return null;
			}
			if (headerBytes.Count == 0)
			{
				// First byte of a request: from here on the sender must finish it promptly.
				requestCts.CancelAfter(timeout);
			}
			headerBytes.Add(single[0]);
			int n = headerBytes.Count;
			if (n >= 4 &&
				headerBytes[n - 4] == '\r' &&
				headerBytes[n - 3] == '\n' &&
				headerBytes[n - 2] == '\r' &&
				headerBytes[n - 1] == '\n')
			{
				headerComplete = true;
				break;
			}
		}

		if (!headerComplete)
		{
			AppLog.Write($"AirPlay request header exceeded {MaxRequestHeaderBytes:N0} bytes; closing connection.");
			return null;
		}

		string headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
		string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
		string firstLine = lines.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? string.Empty;
		string[] parts = firstLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length < 2)
		{
			return null;
		}

		Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (string line in lines.Skip(1))
		{
			int colon = line.IndexOf(':');
			if (colon <= 0)
			{
				continue;
			}
			headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
		}

		long contentLength = 0;
		if (headers.TryGetValue("Content-Length", out string? contentLengthValue) &&
			!long.TryParse(contentLengthValue, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength))
		{
			contentLength = 0;
		}
		if (contentLength > MaxRequestBodyBytes)
		{
			AppLog.Write($"AirPlay request declared Content-Length {contentLength:N0} exceeds {MaxRequestBodyBytes:N0} bytes; closing connection.");
			return null;
		}

		byte[] body = new byte[(int)Math.Max(0, contentLength)];
		int offset = 0;
		while (offset < body.Length)
		{
			int count = await stream.ReadAsync(body.AsMemory(offset, body.Length - offset), requestToken);
			if (count == 0)
			{
				break;
			}
			offset += count;
		}

		return new AirPlayRequest(parts[0], parts[1], parts.Length > 2 ? parts[2] : "RTSP/1.0", firstLine, headers, body);
	}

	internal static byte[] BuildRtspResponse(
		AirPlayRequest request,
		int statusCode,
		string reason,
		byte[] body,
		string contentType,
		IReadOnlyDictionary<string, string>? extraHeaders = null)
	{
		string protocol = request.Protocol.StartsWith("HTTP", StringComparison.OrdinalIgnoreCase) ? "HTTP/1.1" : "RTSP/1.0";
		StringBuilder builder = new StringBuilder();
		builder.Append(protocol).Append(' ').Append(statusCode).Append(' ').Append(reason).Append("\r\n");
		if (request.Headers.TryGetValue("CSeq", out string? cseq))
		{
			builder.Append("CSeq: ").Append(cseq).Append("\r\n");
		}
	builder.Append("Server: AirTunes/").Append(AirTunesVersion).Append("\r\n");
		builder.Append("Date: ").Append(DateTimeOffset.UtcNow.ToString("r")).Append("\r\n");
		if (extraHeaders != null)
		{
			foreach (KeyValuePair<string, string> header in extraHeaders)
			{
				builder.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
			}
		}
		builder.Append("Content-Type: ").Append(contentType).Append("\r\n");
		builder.Append("Content-Length: ").Append(body.Length).Append("\r\n");
		builder.Append("\r\n");
		byte[] headerBytes = Encoding.ASCII.GetBytes(builder.ToString());
		byte[] response = new byte[headerBytes.Length + body.Length];
		Buffer.BlockCopy(headerBytes, 0, response, 0, headerBytes.Length);
		Buffer.BlockCopy(body, 0, response, headerBytes.Length, body.Length);
		return response;
	}

	internal static void LogPairBody(string label, byte[] body)
	{
		AppLog.Write($"AirPlay {label} body length={body.Length}.");
	}
}
