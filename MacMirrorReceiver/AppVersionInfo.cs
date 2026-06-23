using System.Diagnostics;
using System.Reflection;

namespace MacMirrorReceiver;

internal static class AppVersionInfo
{
	public const string ReleasesUrl = "https://github.com/Balragon/iMirror/releases";

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
