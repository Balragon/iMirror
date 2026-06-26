using System.Diagnostics;
using System.Reflection;

namespace MacMirrorReceiver;

internal static class AppVersionInfo
{
	public const string SourceUrl = AppUpdateConstants.GitHubRepoUrl;
	public const string LicenseUrl = AppUpdateConstants.GitHubLicenseUrl;
	public const string ReleasesUrl = AppUpdateConstants.GitHubReleasesUrl;

	public static string InformationalVersion =>
		Assembly.GetExecutingAssembly()
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
			?.InformationalVersion
		?? "unknown";

	public static string DisplayText => "iMirror " + InformationalVersion;

	public static void OpenReleasesPage()
	{
		OpenUrl(ReleasesUrl);
	}

	public static void OpenSourcePage()
	{
		OpenUrl(SourceUrl);
	}

	public static void OpenLicensePage()
	{
		OpenUrl(LicenseUrl);
	}

	private static void OpenUrl(string url)
	{
		Process.Start(new ProcessStartInfo
		{
			FileName = url,
			UseShellExecute = true
		});
	}
}
