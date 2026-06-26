namespace MacMirrorReceiver;

internal sealed record UpdateInfo(
	string LatestVersion,
	bool IsNewer,
	bool IsPrerelease,
	string SetupAssetName,
	string SetupAssetUrl,
	long SetupAssetSize,
	string? Sha256SumsAssetUrl,
	string ReleaseHtmlUrl);

internal sealed record UpdateCheckResult(
	UpdateInfo? Update,
	string Message,
	bool Failed);
