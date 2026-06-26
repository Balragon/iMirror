using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MacMirrorReceiver;

internal sealed class UpdateService
{
	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		PropertyNameCaseInsensitive = true
	};

	private static readonly HttpClient SharedHttpClient = new HttpClient();

	private readonly HttpClient _httpClient;
	private readonly string _currentVersion;
	private readonly string _downloadDirectory;

	public UpdateService()
		: this(SharedHttpClient, AppVersionInfo.InformationalVersion, AppPaths.UpdateDownloadsDirectory)
	{
	}

	internal UpdateService(HttpClient httpClient, string currentVersion, string downloadDirectory)
	{
		_httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
		_currentVersion = currentVersion ?? string.Empty;
		_downloadDirectory = downloadDirectory ?? throw new ArgumentNullException(nameof(downloadDirectory));
	}

	public async Task<UpdateInfo?> CheckAsync(bool includePrerelease, CancellationToken cancellationToken)
	{
		try
		{
			IReadOnlyList<GitHubRelease> releases = includePrerelease
				? await GetReleaseListAsync(cancellationToken).ConfigureAwait(false)
				: new[] { await GetReleaseAsync(AppUpdateConstants.GitHubLatestReleaseApiUrl, cancellationToken).ConfigureAwait(false) };

			UpdateInfo? best = null;
			foreach (GitHubRelease release in releases)
			{
				if (release.Draft || (!includePrerelease && release.Prerelease))
				{
					continue;
				}
				if (!TryBuildUpdateInfo(release, out UpdateInfo? updateInfo))
				{
					continue;
				}
				if (best == null || IsVersionNewer(updateInfo.LatestVersion, best.LatestVersion))
				{
					best = updateInfo;
				}
			}

			return best;
		}
		catch (Exception ex) when (ex is HttpRequestException
			or IOException
			or JsonException
			or OperationCanceledException
			or TaskCanceledException
			or UriFormatException
			or InvalidOperationException)
		{
			if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
			{
				throw;
			}

			AppLog.Write("Update check failed: " + ex.Message);
			return null;
		}
	}

	public async Task<string> DownloadSetupAsync(UpdateInfo updateInfo, IProgress<double>? progress, CancellationToken cancellationToken)
	{
		if (updateInfo == null)
		{
			throw new ArgumentNullException(nameof(updateInfo));
		}
		if (!Uri.TryCreate(updateInfo.SetupAssetUrl, UriKind.Absolute, out Uri? setupUri) || setupUri.Scheme != Uri.UriSchemeHttps)
		{
			throw new InvalidOperationException("Update asset URL must be HTTPS.");
		}

		Directory.CreateDirectory(_downloadDirectory);
		string fileName = SanitizeFileName(updateInfo.SetupAssetName);
		string finalPath = Path.Combine(_downloadDirectory, fileName);
		string partialPath = finalPath + ".download";

		try
		{
			if (File.Exists(partialPath))
			{
				File.Delete(partialPath);
			}

			using HttpResponseMessage response = await SendAsync(setupUri.ToString(), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
				.ConfigureAwait(false);
			response.EnsureSuccessStatusCode();

			await using (Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
			await using (FileStream output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				await CopyWithProgressAsync(input, output, updateInfo.SetupAssetSize, progress, cancellationToken).ConfigureAwait(false);
			}

			long downloadedBytes = new FileInfo(partialPath).Length;
			if (updateInfo.SetupAssetSize > 0 && downloadedBytes != updateInfo.SetupAssetSize)
			{
				throw new InvalidOperationException(
					$"Downloaded update size mismatch. Expected {updateInfo.SetupAssetSize:N0} bytes, got {downloadedBytes:N0} bytes.");
			}

			string? expectedSha256 = await TryGetExpectedSha256Async(updateInfo, cancellationToken).ConfigureAwait(false);
			if (!string.IsNullOrWhiteSpace(expectedSha256))
			{
				string actualSha256 = await ComputeSha256Async(partialPath, cancellationToken).ConfigureAwait(false);
				if (!string.Equals(expectedSha256, actualSha256, StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidOperationException("Downloaded update SHA-256 did not match SHA256SUMS.");
				}
			}

			if (File.Exists(finalPath))
			{
				File.Delete(finalPath);
			}
			File.Move(partialPath, finalPath);
			progress?.Report(1.0);
			return finalPath;
		}
		catch
		{
			try
			{
				if (File.Exists(partialPath))
				{
					File.Delete(partialPath);
				}
			}
			catch
			{
			}
			throw;
		}
	}

	internal static bool IsVersionNewer(string latestVersion, string currentVersion)
	{
		return SemanticVersion.TryParse(latestVersion, out SemanticVersion latest)
			&& SemanticVersion.TryParse(currentVersion, out SemanticVersion current)
			&& latest.CompareTo(current) > 0;
	}

	internal static string? TryParseSha256Sums(string text, string assetName)
	{
		if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(assetName))
		{
			return null;
		}

		using var reader = new StringReader(text);
		string? line;
		while ((line = reader.ReadLine()) != null)
		{
			string trimmed = line.Trim();
			if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
			{
				continue;
			}

			string? parsed = TryParseClassicSha256Line(trimmed, assetName);
			if (parsed != null)
			{
				return parsed;
			}

			parsed = TryParseOpenSslSha256Line(trimmed, assetName);
			if (parsed != null)
			{
				return parsed;
			}
		}

		return null;
	}

	private async Task<IReadOnlyList<GitHubRelease>> GetReleaseListAsync(CancellationToken cancellationToken)
	{
		using HttpResponseMessage response = await SendAsync(AppUpdateConstants.GitHubReleasesApiUrl, HttpCompletionOption.ResponseContentRead, cancellationToken)
			.ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		return await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
			?? new List<GitHubRelease>();
	}

	private async Task<GitHubRelease> GetReleaseAsync(string url, CancellationToken cancellationToken)
	{
		using HttpResponseMessage response = await SendAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken)
			.ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		return await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
			?? throw new JsonException("GitHub release response was empty.");
	}

	private async Task<HttpResponseMessage> SendAsync(string url, HttpCompletionOption completionOption, CancellationToken cancellationToken)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, url);
		request.Headers.UserAgent.Add(new ProductInfoHeaderValue("iMirror-Updater", "1.0"));
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
		return await _httpClient.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
	}

	private bool TryBuildUpdateInfo(GitHubRelease release, [NotNullWhen(true)] out UpdateInfo? updateInfo)
	{
		updateInfo = null;
		string latestVersion = StripTagPrefix(release.TagName);
		if (!SemanticVersion.TryParse(latestVersion, out _))
		{
			return false;
		}

		GitHubAsset? setupAsset = SelectSetupAsset(release.Assets, latestVersion);
		if (setupAsset == null
			|| string.IsNullOrWhiteSpace(setupAsset.Name)
			|| string.IsNullOrWhiteSpace(setupAsset.BrowserDownloadUrl)
			|| setupAsset.Size <= 0)
		{
			return false;
		}

		GitHubAsset? shaAsset = release.Assets?.FirstOrDefault(asset =>
			string.Equals(asset.Name, "SHA256SUMS", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(asset.Name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));

		updateInfo = new UpdateInfo(
			latestVersion,
			IsVersionNewer(latestVersion, _currentVersion),
			release.Prerelease,
			setupAsset.Name,
			setupAsset.BrowserDownloadUrl,
			setupAsset.Size,
			shaAsset?.BrowserDownloadUrl,
			release.HtmlUrl ?? AppUpdateConstants.GitHubReleasesUrl);
		return true;
	}

	private static GitHubAsset? SelectSetupAsset(IReadOnlyList<GitHubAsset>? assets, string latestVersion)
	{
		if (assets == null || assets.Count == 0)
		{
			return null;
		}

		string expectedName = AppUpdateConstants.SetupAssetNameForVersion(latestVersion);
		return assets.FirstOrDefault(asset => string.Equals(asset.Name, expectedName, StringComparison.OrdinalIgnoreCase))
			?? assets.FirstOrDefault(asset =>
				asset.Name.StartsWith("iMirror-", StringComparison.OrdinalIgnoreCase)
				&& asset.Name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase));
	}

	private async Task<string?> TryGetExpectedSha256Async(UpdateInfo updateInfo, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(updateInfo.Sha256SumsAssetUrl))
		{
			return null;
		}

		try
		{
			using HttpResponseMessage response = await SendAsync(updateInfo.Sha256SumsAssetUrl, HttpCompletionOption.ResponseContentRead, cancellationToken)
				.ConfigureAwait(false);
			response.EnsureSuccessStatusCode();
			string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			return TryParseSha256Sums(text, updateInfo.SetupAssetName);
		}
		catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
		{
			AppLog.Write("Could not read SHA256SUMS for update: " + ex.Message);
			return null;
		}
	}

	private static async Task CopyWithProgressAsync(
		Stream input,
		Stream output,
		long expectedBytes,
		IProgress<double>? progress,
		CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[128 * 1024];
		long totalBytes = 0;
		while (true)
		{
			int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
			if (read == 0)
			{
				break;
			}

			await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
			totalBytes += read;
			if (expectedBytes > 0)
			{
				progress?.Report(Math.Clamp((double)totalBytes / expectedBytes, 0.0, 1.0));
			}
		}
	}

	private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
	{
		await using FileStream stream = File.OpenRead(path);
		byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
		return Convert.ToHexString(hash);
	}

	private static string StripTagPrefix(string? tagName)
	{
		string value = (tagName ?? string.Empty).Trim();
		return value.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? value.Substring(1) : value;
	}

	private static string SanitizeFileName(string value)
	{
		char[] invalid = Path.GetInvalidFileNameChars();
		var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
		string sanitized = new string(chars);
		return string.IsNullOrWhiteSpace(sanitized) ? "iMirror-update-setup.exe" : sanitized;
	}

	private static string? TryParseClassicSha256Line(string line, string assetName)
	{
		string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length < 2 || !IsSha256Hex(parts[0]))
		{
			return null;
		}

		string fileName = parts[^1].TrimStart('*');
		return string.Equals(fileName, assetName, StringComparison.OrdinalIgnoreCase) ? parts[0] : null;
	}

	private static string? TryParseOpenSslSha256Line(string line, string assetName)
	{
		string prefix = "SHA256(";
		if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		int close = line.IndexOf(')');
		if (close <= prefix.Length)
		{
			return null;
		}

		string fileName = line.Substring(prefix.Length, close - prefix.Length);
		if (!string.Equals(fileName, assetName, StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		int equals = line.IndexOf('=', close);
		if (equals < 0)
		{
			return null;
		}

		string hash = line.Substring(equals + 1).Trim();
		return IsSha256Hex(hash) ? hash : null;
	}

	private static bool IsSha256Hex(string value)
	{
		if (value.Length != 64)
		{
			return false;
		}

		foreach (char ch in value)
		{
			if (!Uri.IsHexDigit(ch))
			{
				return false;
			}
		}
		return true;
	}

	private sealed class GitHubRelease
	{
		[JsonPropertyName("tag_name")]
		public string? TagName { get; set; }

		[JsonPropertyName("html_url")]
		public string? HtmlUrl { get; set; }

		[JsonPropertyName("prerelease")]
		public bool Prerelease { get; set; }

		[JsonPropertyName("draft")]
		public bool Draft { get; set; }

		[JsonPropertyName("assets")]
		public List<GitHubAsset>? Assets { get; set; }
	}

	private sealed class GitHubAsset
	{
		[JsonPropertyName("name")]
		public string Name { get; set; } = string.Empty;

		[JsonPropertyName("browser_download_url")]
		public string BrowserDownloadUrl { get; set; } = string.Empty;

		[JsonPropertyName("size")]
		public long Size { get; set; }
	}
}
