using System;
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
