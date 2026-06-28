namespace MacMirrorReceiver;

internal static class AppUpdateConstants
{
	public const string ApplicationMutexName = @"Local\iMirror.App";
	public const string GitHubRepoUrl = "https://github.com/Balragon/iMirror";
	public const string GitHubLicenseUrl = "https://github.com/Balragon/iMirror/blob/main/LICENSE";
	public const string GitHubReleasesUrl = "https://github.com/Balragon/iMirror/releases";
	public const string GitHubLatestReleaseApiUrl = "https://api.github.com/repos/Balragon/iMirror/releases/latest";
	public const string GitHubReleasesApiUrl = "https://api.github.com/repos/Balragon/iMirror/releases";

	public static string SetupAssetNameForVersion(string version)
	{
		return "iMirror-" + version + "-setup.exe";
	}
}
