using System;
using System.Windows.Media;

namespace MacMirrorReceiver;

/// <summary>What the tray menu header shows: the receiver state line and its status colour.</summary>
internal readonly record struct TrayMenuStatus(string Text, ReceiverStatusTone Tone);

/// <summary>
/// The tray menu colours for one Windows theme, following the Windows 11 Fluent menu tokens:
/// text, secondary text, hover/pressed fills, divider, and a solid background for systems
/// without the acrylic backdrop.
/// </summary>
internal sealed record TrayMenuPalette(
	Color Foreground,
	Color MutedForeground,
	Color HoverFill,
	Color PressedFill,
	Color Divider,
	Color SolidBackground,
	Color SolidBorder)
{
	public static TrayMenuPalette Dark { get; } = new TrayMenuPalette(
		Foreground: Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
		MutedForeground: Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF),
		HoverFill: Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF),
		PressedFill: Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF),
		Divider: Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF),
		SolidBackground: Color.FromRgb(0x2C, 0x2C, 0x2C),
		SolidBorder: Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

	public static TrayMenuPalette Light { get; } = new TrayMenuPalette(
		Foreground: Color.FromArgb(0xE4, 0x00, 0x00, 0x00),
		MutedForeground: Color.FromArgb(0x9E, 0x00, 0x00, 0x00),
		HoverFill: Color.FromArgb(0x09, 0x00, 0x00, 0x00),
		PressedFill: Color.FromArgb(0x06, 0x00, 0x00, 0x00),
		Divider: Color.FromArgb(0x0F, 0x00, 0x00, 0x00),
		SolidBackground: Color.FromRgb(0xF9, 0xF9, 0xF9),
		SolidBorder: Color.FromArgb(0x24, 0x00, 0x00, 0x00));

	public static TrayMenuPalette For(bool lightTheme) => lightTheme ? Light : Dark;

	/// <summary>The status dot colour, with darker variants on light backgrounds so they stay legible.</summary>
	public static Color ToneColor(ReceiverStatusTone tone, bool lightTheme)
	{
		return tone switch
		{
			ReceiverStatusTone.Live => lightTheme ? Color.FromRgb(0x0F, 0x7B, 0x0F) : Color.FromRgb(0x46, 0xD5, 0x78),
			ReceiverStatusTone.Warning or ReceiverStatusTone.Connecting => lightTheme ? Color.FromRgb(0x9D, 0x5D, 0x00) : Color.FromRgb(0xFF, 0xB3, 0x40),
			ReceiverStatusTone.LegacySenderAvailable => lightTheme ? Color.FromRgb(0x00, 0x5F, 0xB8) : Color.FromRgb(0x60, 0xCD, 0xFF),
			_ => lightTheme ? Color.FromRgb(0x8A, 0x8A, 0x8A) : Color.FromRgb(0x9D, 0x9D, 0x9D)
		};
	}
}

/// <summary>
/// Where the tray menu opens, in physical pixels. Like the shell's own tray menus it opens up
/// and to the left of the cursor (the taskbar is usually at the bottom right), flips to the other
/// side when that would leave the work area, and is finally clamped inside the work area, which
/// also handles a taskbar on the top or left edge.
/// </summary>
internal static class TrayMenuPlacement
{
	public static (int X, int Y) Place(int cursorX, int cursorY, int width, int height, int workLeft, int workTop, int workRight, int workBottom)
	{
		int x = cursorX - width;
		if (x < workLeft)
		{
			x = cursorX;
		}
		int y = cursorY - height;
		if (y < workTop)
		{
			y = cursorY;
		}

		x = Math.Max(workLeft, Math.Min(x, workRight - width));
		y = Math.Max(workTop, Math.Min(y, workBottom - height));
		return (x, y);
	}
}
