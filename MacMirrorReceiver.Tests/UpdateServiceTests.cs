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
	public void TryParseSha256Sums_SupportsClassicAndOpenSslFormats()
	{
		const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

		Assert.Equal(hash, UpdateService.TryParseSha256Sums(hash + " *iMirror-0.4.0-setup.exe", "iMirror-0.4.0-setup.exe"));
		Assert.Equal(hash, UpdateService.TryParseSha256Sums("SHA256(iMirror-0.4.0-setup.exe)= " + hash, "iMirror-0.4.0-setup.exe"));
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
