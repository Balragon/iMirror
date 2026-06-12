using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace MacMirrorReceiver.Networking;

internal sealed class AirPlayTimingClient : IDisposable
{
	private static readonly DateTimeOffset NtpEpoch = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private readonly int _localPort;
	private readonly object _gate = new object();
	private CancellationTokenSource? _cts;
	private UdpClient? _client;

	public AirPlayTimingClient(int localPort)
	{
		_localPort = localPort;
	}

	public void Start(IPAddress remoteAddress, int remotePort)
	{
		if (remotePort <= 0 || remotePort > ushort.MaxValue)
		{
			return;
		}

		lock (_gate)
		{
			_cts?.Cancel();
			_client?.Dispose();
			_cts = new CancellationTokenSource();
			_client = new UdpClient(new IPEndPoint(IPAddress.Any, _localPort));
			_client.Client.ReceiveTimeout = 100;
			_ = Task.Run(() => SendTimingLoopAsync(_client, new IPEndPoint(remoteAddress, remotePort), _cts.Token));
		}

		AppLog.Write($"AirPlay timing UDP started from local port {_localPort} to {remoteAddress}:{remotePort}.");
	}

	private static async Task SendTimingLoopAsync(UdpClient client, IPEndPoint endpoint, CancellationToken token)
	{
		byte[] packet = new byte[32];
		packet[0] = 0x80;
		packet[1] = 0xD2;
		packet[3] = 0x07;

		for (int i = 0; i < 40 && !token.IsCancellationRequested; i++)
		{
			try
			{
				BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(24, 8), GetNtpTimestamp());
				await client.SendAsync(packet, packet.Length, endpoint).WaitAsync(token);
				await Task.Delay(250, token);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (ObjectDisposedException)
			{
				break;
			}
			catch (Exception ex)
			{
				AppLog.Write("AirPlay timing UDP send failed: " + ex.Message);
				break;
			}
		}
	}

	private static ulong GetNtpTimestamp()
	{
		TimeSpan elapsed = DateTimeOffset.UtcNow - NtpEpoch;
		ulong seconds = (ulong)elapsed.TotalSeconds;
		ulong fraction = (ulong)((elapsed.TotalSeconds - Math.Truncate(elapsed.TotalSeconds)) * 4294967296.0);
		return (seconds << 32) | fraction;
	}

	public void Dispose()
	{
		lock (_gate)
		{
			_cts?.Cancel();
			_client?.Dispose();
			_cts?.Dispose();
			_cts = null;
			_client = null;
		}
	}
}
