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

	/// <summary>"iMirror 0.8.0 (25aa284)": the informational version with the commit cut to 7 characters.</summary>
	public static string ShortDisplayText => "iMirror " + ShortenVersion(InformationalVersion);

	/// <summary>
	/// Shortens "0.8.0+25aa28455baeee7d..." to "0.8.0 (25aa284)". A version without "+metadata"
	/// is returned as is; metadata of 7 characters or fewer is kept whole.
	/// </summary>
	internal static string ShortenVersion(string informationalVersion)
	{
		int plus = informationalVersion.IndexOf('+');
		if (plus < 0)
		{
			return informationalVersion;
		}

		string version = informationalVersion.Substring(0, plus);
		string metadata = informationalVersion.Substring(plus + 1);
		if (metadata.Length == 0)
		{
			return version;
		}
		return $"{version} ({(metadata.Length > 7 ? metadata.Substring(0, 7) : metadata)})";
	}

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
