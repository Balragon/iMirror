using System;
using Microsoft.Win32;

namespace MacMirrorReceiver;

/// <summary>
/// Chooses the dark or light app icon from the Windows theme. The executable and installer
/// always carry the dark icon; only the window title and tray icons follow the theme.
/// </summary>
internal static class AppIconTheme
{
	internal const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

	// SystemUsesLightTheme governs the taskbar/tray/system surfaces, which is where the tray
	// icon lives and the window icon is seen in the taskbar; AppsUseLightTheme is the app
	// chrome, which iMirror keeps dark.
	internal const string SystemUsesLightThemeValueName = "SystemUsesLightTheme";

	public static Uri DarkIconUri { get; } = CreatePackUri("/Assets/iMirror.ico");
	public static Uri LightIconUri { get; } = CreatePackUri("/Assets/iMirror-light.ico");

	private static Uri CreatePackUri(string resourcePath)
	{
		// The pack:// scheme is registered by PackUriHelper's static initializer, which WPF
		// runs when an Application is created. Outside that (unit tests), constructing the
		// Uri first throws UriFormatException, so touch the helper to force registration.
		_ = System.IO.Packaging.PackUriHelper.UriSchemePack;
		return new Uri("pack://application:,,," + resourcePath, UriKind.Absolute);
	}

	/// <summary>True when Windows is set to a light system theme; false (dark) when unset or unreadable.</summary>
	public static bool IsSystemLightTheme()
	{
		try
		{
			using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
			return ParseSystemUsesLightTheme(key?.GetValue(SystemUsesLightThemeValueName));
		}
		catch (Exception ex)
		{
			AppLog.Write("Could not read the Windows theme; using the dark icon: " + ex.Message);
			return false;
		}
	}

	/// <summary>Interprets the registry value: a non-zero integer means light; anything else means dark.</summary>
	internal static bool ParseSystemUsesLightTheme(object? value)
	{
		return value switch
		{
			int number => number != 0,
			long number => number != 0,
			_ => false
		};
	}

	internal static Uri IconUriFor(bool lightTheme) => lightTheme ? LightIconUri : DarkIconUri;
}
