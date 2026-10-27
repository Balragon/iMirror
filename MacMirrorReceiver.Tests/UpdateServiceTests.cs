using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MacMirrorReceiver;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class UpdateServiceTests
{
	[Theory]
	[InlineData("0.10.0", "0.9.9", true)]
	[InlineData("0.2.0", "0.10.0", false)]
	[InlineData("0.4.0", "0.4.0-rc.1+abc1234", true)]
	[InlineData("0.4.0-rc.2", "0.4.0-rc.1", true)]
	[InlineData("0.4.0", "0.4.0+abc1234", false)]
	public void IsVersionNewer_UsesSemverComparison(string latest, string current, bool expected)
	{
		Assert.Equal(expected, UpdateService.IsVersionNewer(latest, current));
	}

	[Fact]
	public async Task CheckAsync_ReturnsStableSetupAssetFromLatestRelease()
	{
		const string json = """
		{
		  "tag_name": "v0.4.0",
		  "html_url": "https://github.com/Balragon/iMirror/releases/tag/v0.4.0",
		  "prerelease": false,
		  "draft": false,
		  "assets": [
		    {
		      "name": "iMirror-0.4.0-win-x64.zip",
		      "browser_download_url": "https://example.test/iMirror-0.4.0-win-x64.zip",
		      "size": 100
		    },
		    {
		      "name": "iMirror-0.4.0-setup.exe",
		      "browser_download_url": "https://example.test/iMirror-0.4.0-setup.exe",
		      "size": 200
		    },
		    {
		      "name": "SHA256SUMS",
		      "browser_download_url": "https://example.test/SHA256SUMS",
		      "size": 80
		    }
		  ]
		}
		""";
		var client = new HttpClient(new StubHttpMessageHandler(_ => JsonResponse(json)));
		var service = new UpdateService(client, "0.3.0+abc1234", CreateTempDirectory());

		UpdateInfo? update = await service.CheckAsync(includePrerelease: false, CancellationToken.None);

		Assert.NotNull(update);
		Assert.True(update.IsNewer);
		Assert.False(update.IsPrerelease);
		Assert.Equal("0.4.0", update.LatestVersion);
		Assert.Equal("iMirror-0.4.0-setup.exe", update.SetupAssetName);
		Assert.Equal("https://example.test/iMirror-0.4.0-setup.exe", update.SetupAssetUrl);
		Assert.Equal("https://example.test/SHA256SUMS", update.Sha256SumsAssetUrl);
	}

	[Fact]
	public async Task CheckAsync_ReturnsNullInsteadOfThrowingOnMalformedJson()
	{
		var client = new HttpClient(new StubHttpMessageHandler(_ => JsonResponse("{not json")));
		var service = new UpdateService(client, "0.3.0", CreateTempDirectory());

		UpdateInfo? update = await service.CheckAsync(includePrerelease: false, CancellationToken.None);

		Assert.Null(update);
	}

	[Fact]
	public async Task DownloadSetupAsync_VerifiesSizeAndSha256()
	{
		byte[] setupBytes = Encoding.UTF8.GetBytes("fake setup bytes");
		string setupHash = Convert.ToHexString(SHA256.HashData(setupBytes)).ToLowerInvariant();
		string sha256Sums = setupHash + "  iMirror-0.4.0-setup.exe";
		var update = new UpdateInfo(
			"0.4.0",
			IsNewer: true,
			IsPrerelease: false,
			"iMirror-0.4.0-setup.exe",
			"https://example.test/iMirror-0.4.0-setup.exe",
			setupBytes.Length,
			"https://example.test/SHA256SUMS",
			"https://github.com/Balragon/iMirror/releases/tag/v0.4.0");
		var client = new HttpClient(new StubHttpMessageHandler(request =>
		{
			return request.RequestUri?.AbsolutePath.EndsWith("SHA256SUMS", StringComparison.OrdinalIgnoreCase) == true
				? TextResponse(sha256Sums)
				: BytesResponse(setupBytes);
		}));
		string downloadDirectory = CreateTempDirectory();
		var service = new UpdateService(client, "0.3.0", downloadDirectory);

		string setupPath = await service.DownloadSetupAsync(update, progress: null, CancellationToken.None);

		Assert.Equal(Path.Combine(downloadDirectory, "iMirror-0.4.0-setup.exe"), setupPath);
		Assert.Equal(setupBytes, File.ReadAllBytes(setupPath));
		Assert.False(File.Exists(setupPath + ".download"));
	}

	[Fact]
	public async Task DownloadSetupAsync_FailsWhenTheDownloadStalls()
	{
		var update = new UpdateInfo(
			"0.4.0",
			IsNewer: true,
			IsPrerelease: false,
			"iMirror-0.4.0-setup.exe",
			"https://example.test/iMirror-0.4.0-setup.exe",
			1024,
			"https://example.test/SHA256SUMS",
			"https://github.com/Balragon/iMirror/releases/tag/v0.4.0");
		var client = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StreamContent(new StallingStream(Encoding.ASCII.GetBytes("partial")))
		}));
		string downloadDirectory = CreateTempDirectory();
		var service = new UpdateService(client, "0.3.0", downloadDirectory) { DownloadReadTimeout = TimeSpan.FromMilliseconds(200) };

		await Assert.ThrowsAsync<TimeoutException>(() => service.DownloadSetupAsync(update, progress: null, CancellationToken.None));

		Assert.Empty(Directory.GetFiles(downloadDirectory));
	}

	[Fact]
	public async Task DownloadSetupAsync_AbortsWhenTheBodyExceedsTheDeclaredSize()
	{
		byte[] oversized = new byte[600 * 1024];
		var update = new UpdateInfo(
			"0.4.0",
			IsNewer: true,
			IsPrerelease: false,
			"iMirror-0.4.0-setup.exe",
			"https://example.test/iMirror-0.4.0-setup.exe",
			100,
			"https://example.test/SHA256SUMS",
			"https://github.com/Balragon/iMirror/releases/tag/v0.4.0");
		var client = new HttpClient(new StubHttpMessageHandler(_ => BytesResponse(oversized)));
		string downloadDirectory = CreateTempDirectory();
		var service = new UpdateService(client, "0.3.0", downloadDirectory);

		InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
			() => service.DownloadSetupAsync(update, progress: null, CancellationToken.None));

		Assert.Contains("exceeded", ex.Message);
		Assert.Empty(Directory.GetFiles(downloadDirectory));
	}

	[Fact]
	public async Task DownloadSetupAsync_FailsClosed_WhenChecksumUrlIsNotHttps()
	{
		byte[] setupBytes = Encoding.UTF8.GetBytes("fake setup bytes");
		string sha256Sums = Convert.ToHexString(SHA256.HashData(setupBytes)).ToLowerInvariant() + "  iMirror-0.4.0-setup.exe";
		var update = new UpdateInfo(
			"0.4.0",
			IsNewer: true,
			IsPrerelease: false,
			"iMirror-0.4.0-setup.exe",
			"https://example.test/iMirror-0.4.0-setup.exe",
			setupBytes.Length,
			"http://example.test/SHA256SUMS",
			"https://github.com/Balragon/iMirror/releases/tag/v0.4.0");
		var client = new HttpClient(new StubHttpMessageHandler(request =>
			request.RequestUri?.AbsolutePath.EndsWith("SHA256SUMS", StringComparison.OrdinalIgnoreCase) == true
				? TextResponse(sha256Sums)
				: BytesResponse(setupBytes)));
		string downloadDirectory = CreateTempDirectory();
		var service = new UpdateService(client, "0.3.0", downloadDirectory);

		InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
			() => service.DownloadSetupAsync(update, progress: null, CancellationToken.None));

		Assert.Contains("Cannot verify", ex.Message);
		Assert.Empty(Directory.GetFiles(downloadDirectory));
	}

	[Fact]
	public async Task DownloadSetupAsync_FailsClosed_WhenNoChecksumAssetAvailable()
	{
		byte[] setupBytes = Encoding.UTF8.GetBytes("fake setup bytes");
		UpdateInfo update = SetupUpdate(setupBytes.Length, sha256SumsAssetUrl: null);
		var client = new HttpClient(new StubHttpMessageHandler(_ => BytesResponse(setupBytes)));
		string downloadDirectory = CreateTempDirectory();
		var service = new UpdateService(client, "0.3.0", downloadDirectory);

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => service.DownloadSetupAsync(update, progress: null, CancellationToken.None));

		AssertNoSetupLeftBehind(downloadDirectory);
	}

	[Fact]
	public async Task DownloadSetupAsync_FailsClosed_WhenChecksumDownloadFails()
	{
		byte[] setupBytes = Encoding.UTF8.GetBytes("fake setup bytes");
		UpdateInfo update = SetupUpdate(setupBytes.Length, "https://example.test/SHA256SUMS");
		var client = new HttpClient(new StubHttpMessageHandler(request =>
			IsChecksumRequest(request)
				? new HttpResponseMessage(HttpStatusCode.NotFound)
				: BytesResponse(setupBytes)));
		string downloadDirectory = CreateTempDirectory();
		var service = new UpdateService(client, "0.3.0", downloadDirectory);

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => service.DownloadSetupAsync(update, progress: null, CancellationToken.None));

		AssertNoSetupLeftBehind(downloadDirectory);
	}

	[Fact]
	public async Task DownloadSetupAsync_FailsClosed_WhenChecksumOmitsTheSetupAsset()
	{
		byte[] setupBytes = Encoding.UTF8.GetBytes("fake setup bytes");
		string unrelatedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("other"))).ToLowerInvariant();
		string sha256Sums = unrelatedHash + "  iMirror-0.4.0-win-x64.zip"; // no line for the setup.exe asset
		UpdateInfo update = SetupUpdate(setupBytes.Length, "https://example.test/SHA256SUMS");
		var client = new HttpClient(new StubHttpMessageHandler(request =>
			IsChecksumRequest(request) ? TextResponse(sha256Sums) : BytesResponse(setupBytes)));
		string downloadDirectory = CreateTempDirectory();
		var service = new UpdateService(client, "0.3.0", downloadDirectory);

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => service.DownloadSetupAsync(update, progress: null, CancellationToken.None));

		AssertNoSetupLeftBehind(downloadDirectory);
	}

	[Fact]
	public async Task DownloadSetupAsync_FailsClosed_WhenHashDoesNotMatch()
	{
		byte[] setupBytes = Encoding.UTF8.GetBytes("fake setup bytes");
		string wrongHash = new string('a', 64);
		string sha256Sums = wrongHash + "  iMirror-0.4.0-setup.exe";
		UpdateInfo update = SetupUpdate(setupBytes.Length, "https://example.test/SHA256SUMS");
		var client = new HttpClient(new StubHttpMessageHandler(request =>
			IsChecksumRequest(request) ? TextResponse(sha256Sums) : BytesResponse(setupBytes)));
		string downloadDirectory = CreateTempDirectory();
		var service = new UpdateService(client, "0.3.0", downloadDirectory);

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => service.DownloadSetupAsync(update, progress: null, CancellationToken.None));

		AssertNoSetupLeftBehind(downloadDirectory);
	}

	[Fact]
	public void TryParseSha256Sums_SupportsClassicAndOpenSslFormats()
	{
		const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

		Assert.Equal(hash, UpdateService.TryParseSha256Sums(hash + " *iMirror-0.4.0-setup.exe", "iMirror-0.4.0-setup.exe"));
		Assert.Equal(hash, UpdateService.TryParseSha256Sums("SHA256(iMirror-0.4.0-setup.exe)= " + hash, "iMirror-0.4.0-setup.exe"));
	}

	[Fact]
	public void TryCleanUpDownloadedInstallers_RemovesInstallerAndPartialFilesOnly()
	{
		string directory = CreateTempDirectory();
		string oldSetup = Path.Combine(directory, "iMirror-0.3.0-setup.exe");
		string partial = Path.Combine(directory, "iMirror-0.4.0-setup.exe.download");
		string unrelated = Path.Combine(directory, "notes.txt");
		File.WriteAllText(oldSetup, "old installer");
		File.WriteAllText(partial, "partial download");
		File.WriteAllText(unrelated, "keep me");
		var service = new UpdateService(new HttpClient(new StubHttpMessageHandler(_ => TextResponse(""))), "0.4.0", directory);

		service.TryCleanUpDownloadedInstallers();

		Assert.False(File.Exists(oldSetup));
		Assert.False(File.Exists(partial));
		Assert.True(File.Exists(unrelated));
	}

	[Fact]
	public void TryCleanUpDownloadedInstallers_SkipsLockedFilesWithoutThrowing()
	{
		string directory = CreateTempDirectory();
		string lockedSetup = Path.Combine(directory, "iMirror-0.4.0-setup.exe");
		string deletableSetup = Path.Combine(directory, "iMirror-0.3.0-setup.exe");
		File.WriteAllText(lockedSetup, "running installer");
		File.WriteAllText(deletableSetup, "old installer");
		var service = new UpdateService(new HttpClient(new StubHttpMessageHandler(_ => TextResponse(""))), "0.4.0", directory);

		using (new FileStream(lockedSetup, FileMode.Open, FileAccess.Read, FileShare.None))
		{
			service.TryCleanUpDownloadedInstallers();

			Assert.True(File.Exists(lockedSetup), "a locked (in-use) installer must be skipped, not deleted");
		}
		Assert.False(File.Exists(deletableSetup));
	}

	[Fact]
	public void TryCleanUpDownloadedInstallers_DoesNothing_WhenDirectoryIsMissing()
	{
		string directory = Path.Combine(Path.GetTempPath(), "iMirror-tests", Guid.NewGuid().ToString("N"));
		var service = new UpdateService(new HttpClient(new StubHttpMessageHandler(_ => TextResponse(""))), "0.4.0", directory);

		service.TryCleanUpDownloadedInstallers();

		Assert.False(Directory.Exists(directory));
	}

	private static UpdateInfo SetupUpdate(long setupAssetSize, string? sha256SumsAssetUrl)
	{
		return new UpdateInfo(
			"0.4.0",
			IsNewer: true,
			IsPrerelease: false,
			"iMirror-0.4.0-setup.exe",
			"https://example.test/iMirror-0.4.0-setup.exe",
			setupAssetSize,
			sha256SumsAssetUrl,
			"https://github.com/Balragon/iMirror/releases/tag/v0.4.0");
	}

	private static bool IsChecksumRequest(HttpRequestMessage request)
	{
		return request.RequestUri?.AbsolutePath.EndsWith("SHA256SUMS", StringComparison.OrdinalIgnoreCase) == true;
	}

	private static void AssertNoSetupLeftBehind(string downloadDirectory)
	{
		string finalPath = Path.Combine(downloadDirectory, "iMirror-0.4.0-setup.exe");
		Assert.False(File.Exists(finalPath), "fail-closed download must not leave an installable setup file");
		Assert.False(File.Exists(finalPath + ".download"), "fail-closed download must not leave a partial file");
	}

	private static string CreateTempDirectory()
	{
		string path = Path.Combine(Path.GetTempPath(), "iMirror-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(path);
		return path;
	}

	private static HttpResponseMessage JsonResponse(string json)
	{
		return TextResponse(json, "application/json");
	}

	private static HttpResponseMessage TextResponse(string text, string mediaType = "text/plain")
	{
		return new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(text, Encoding.UTF8, mediaType)
		};
	}

	private static HttpResponseMessage BytesResponse(byte[] bytes)
	{
		return new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new ByteArrayContent(bytes)
		};
	}

	/// <summary>Serves a fixed prefix, then blocks every further read until cancelled.</summary>
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

	private sealed class StubHttpMessageHandler : HttpMessageHandler
	{
		private readonly Func<HttpRequestMessage, HttpResponseMessage> _handle;

		public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handle)
		{
			_handle = handle;
		}

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			return Task.FromResult(_handle(request));
		}
	}
}
