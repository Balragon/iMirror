using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MacMirrorReceiver.Protocol;

namespace MacMirrorReceiver.Networking;

public sealed class MirrorClient : IAsyncDisposable
{
	private const int MaxControlPayloadLength = 1024 * 1024;

	private const int MaxVideoPayloadLength = 64 * 1024 * 1024;

	private readonly CancellationTokenSource _cts = new CancellationTokenSource();

	private TcpClient? _tcpClient;

	private NetworkStream? _stream;

	private Task? _receiveTask;

	private int _disposeStarted;

	public event Action<string>? StatusChanged;

	public event Action<StreamConfig>? ConfigReceived;

	public event Action<byte[], ulong, long>? VideoReceived;

	public event Action<CursorState, ulong, long>? CursorReceived;

	public event Action? ConnectionClosed;

	public async Task ConnectAsync(IPEndPoint endpoint, string pin)
	{
		_tcpClient = new TcpClient
		{
			NoDelay = true,
			ReceiveBufferSize = 4 * 1024 * 1024,
			SendBufferSize = 65536
		};
		await _tcpClient.ConnectAsync(endpoint.Address, endpoint.Port, _cts.Token);
		_stream = _tcpClient.GetStream();
		this.StatusChanged?.Invoke("TCP connected. Sending PIN.");
		byte[] array = JsonSerializer.SerializeToUtf8Bytes(new AuthRequest
		{
			Pin = pin
		});
		byte[] array2 = MirrorProtocol.BuildFrame(MirrorMessageType.Auth, array);
		await _stream.WriteAsync(array2, _cts.Token);
		await _stream.FlushAsync(_cts.Token);
		this.StatusChanged?.Invoke("PIN sent. Waiting for sender stream.");
		_receiveTask = Task.Run(ReceiveLoopAsync);
	}

	private async Task ReceiveLoopAsync()
	{
		_ = 1;
		try
		{
			if (_stream == null)
			{
				return;
			}
			byte[] headerBuffer = new byte[MirrorProtocol.HeaderSize];
			while (!_cts.IsCancellationRequested)
			{
				await ReadExactAsync(_stream, headerBuffer, _cts.Token);
				MirrorHeader header = MirrorProtocol.ParseHeader(headerBuffer);
				int maxPayloadLength = header.Type == MirrorMessageType.Video ? MaxVideoPayloadLength : MaxControlPayloadLength;
				if (header.PayloadLength < 0 || header.PayloadLength > maxPayloadLength)
				{
					throw new InvalidDataException("Invalid payload length.");
				}
				byte[] payload = new byte[header.PayloadLength];
				if (payload.Length != 0)
				{
					await ReadExactAsync(_stream, payload, _cts.Token);
				}
				HandleMessage(header.Type, payload, header.Timestamp, Stopwatch.GetTimestamp());
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex2)
		{
			this.StatusChanged?.Invoke("Connection closed: " + ex2.Message);
		}
		finally
		{
			if (!_cts.IsCancellationRequested)
			{
				this.ConnectionClosed?.Invoke();
			}
		}
	}

	private void HandleMessage(MirrorMessageType type, byte[] payload, ulong timestamp, long receivedTick)
	{
		switch (type)
		{
		case MirrorMessageType.AuthResult:
		{
			AuthResult? authResult = JsonSerializer.Deserialize<AuthResult>(payload);
			this.StatusChanged?.Invoke(authResult?.Message ?? "Authenticated.");
			if (authResult != null && !authResult.Accepted)
			{
				_ = DisposeAsync().AsTask();
			}
			break;
		}
		case MirrorMessageType.StreamConfig:
		{
			StreamConfig? streamConfig = JsonSerializer.Deserialize<StreamConfig>(payload);
			if (streamConfig != null)
			{
				this.StatusChanged?.Invoke($"Stream config received: {streamConfig.Width}x{streamConfig.Height} @ {streamConfig.Fps} fps.");
				this.ConfigReceived?.Invoke(streamConfig);
			}
			break;
		}
		case MirrorMessageType.Video:
			this.VideoReceived?.Invoke(payload, timestamp, receivedTick);
			break;
			case MirrorMessageType.Cursor:
			{
				Action<CursorState, ulong, long>? cursorReceived = this.CursorReceived;
				if (cursorReceived == null)
				{
					break;
				}
				CursorState? cursorState = JsonSerializer.Deserialize<CursorState>(payload);
				if (cursorState != null)
				{
					cursorReceived(cursorState, timestamp, receivedTick);
				}
				break;
			}
		case MirrorMessageType.Error:
		{
			StatusMessage? statusMessage = JsonSerializer.Deserialize<StatusMessage>(payload);
			this.StatusChanged?.Invoke(statusMessage?.Message ?? "Sender reported an error.");
			break;
		}
		default:
			this.StatusChanged?.Invoke($"Unhandled message type {type}.");
			break;
		}
	}

	private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
	{
		int num;
		for (int offset = 0; offset < buffer.Length; offset += num)
		{
			num = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
			if (num == 0)
			{
				throw new EndOfStreamException();
			}
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
		{
			return;
		}
		_cts.Cancel();
		_stream?.Dispose();
		_tcpClient?.Dispose();
		if (_receiveTask != null)
		{
			try
			{
				await _receiveTask;
			}
			catch
			{
			}
		}
		_cts.Dispose();
	}
}
