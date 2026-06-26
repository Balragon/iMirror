using System.Diagnostics;
using System.Reflection;

namespace MacMirrorReceiver;

internal static class AppVersionInfo
{
	public const string ReleasesUrl = AppUpdateConstants.GitHubReleasesUrl;

	public static string InformationalVersion =>
		Assembly.GetExecutingAssembly()
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
			?.InformationalVersion
		?? "unknown";

	public static string DisplayText => "iMirror " + InformationalVersion;

	public static void OpenReleasesPage()
	{
		Process.Start(new ProcessStartInfo
		{
			FileName = ReleasesUrl,
			UseShellExecute = true
		});
	}
}
