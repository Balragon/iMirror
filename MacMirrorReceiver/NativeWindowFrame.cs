using System;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace MacMirrorReceiver;

/// <summary>
/// The Win32 side of fullscreen: strips the caption/frame styles from the window, pins it
/// topmost over the monitor it is on, and restores the captured styles on exit. WPF-level
/// state (WindowStyle, WindowChrome, Topmost, layout) stays in MainWindow.
/// </summary>
internal sealed class NativeWindowFrame
{
	private const int GwlStyle = -16;
	private const int GwlExtendedStyle = -20;

	private const long WindowStyleCaption = 0x00C00000L;
	private const long WindowStyleThickFrame = 0x00040000L;
	private const long WindowStyleSystemMenu = 0x00080000L;
	private const long WindowStyleMinimizeBox = 0x00020000L;
	private const long WindowStyleMaximizeBox = 0x00010000L;

	private const uint SetWindowPosNoSize = 0x0001;
	private const uint SetWindowPosNoMove = 0x0002;
	private const uint SetWindowPosNoZOrder = 0x0004;
	private const uint SetWindowPosNoActivate = 0x0010;
	private const uint SetWindowPosFrameChanged = 0x0020;
	private const uint SetWindowPosShowWindow = 0x0040;

	private static readonly IntPtr HwndTopmost = new IntPtr(-1);
	private static readonly IntPtr HwndNotTopmost = new IntPtr(-2);

	private nint _previousStyle;
	private nint _previousExtendedStyle;
	private bool _captured;

	/// <summary>Remembers the current styles so <see cref="RestoreStyles"/> can put them back.</summary>
	public void CaptureStyles(IntPtr hwnd)
	{
		_previousStyle = GetWindowLongPtr(hwnd, GwlStyle);
		_previousExtendedStyle = GetWindowLongPtr(hwnd, GwlExtendedStyle);
		_captured = true;
	}

	/// <summary>Removes caption, thick frame, system menu and min/max boxes and raises the window topmost.</summary>
	public void ApplyFullscreenFrame(IntPtr hwnd)
	{
		nint style = GetWindowLongPtr(hwnd, GwlStyle);
		style = new IntPtr(style.ToInt64() & ~(
			WindowStyleCaption |
			WindowStyleThickFrame |
			WindowStyleSystemMenu |
			WindowStyleMinimizeBox |
			WindowStyleMaximizeBox));
		_ = SetWindowLongPtr(hwnd, GwlStyle, style);
		_ = SetWindowPos(
			hwnd,
			HwndTopmost,
			0,
			0,
			0,
			0,
			SetWindowPosNoMove | SetWindowPosNoSize | SetWindowPosNoActivate | SetWindowPosFrameChanged);
	}

	/// <summary>Puts back the styles from <see cref="CaptureStyles"/>; no-op when nothing was captured.</summary>
	public void RestoreStyles(IntPtr hwnd)
	{
		if (!_captured)
		{
			return;
		}

		_ = SetWindowLongPtr(hwnd, GwlStyle, _previousStyle);
		_ = SetWindowLongPtr(hwnd, GwlExtendedStyle, _previousExtendedStyle);
		_ = SetWindowPos(
			hwnd,
			IntPtr.Zero,
			0,
			0,
			0,
			0,
			SetWindowPosNoMove | SetWindowPosNoSize | SetWindowPosNoZOrder | SetWindowPosNoActivate | SetWindowPosFrameChanged);
		_captured = false;
	}

	/// <summary>Sizes the window to the bounds of the monitor it is currently on, topmost.</summary>
	public static void MoveToCurrentMonitor(IntPtr hwnd)
	{
		Forms.Screen screen = Forms.Screen.FromHandle(hwnd);
		System.Drawing.Rectangle bounds = screen.Bounds;
		_ = SetWindowPos(
			hwnd,
			HwndTopmost,
			bounds.Left,
			bounds.Top,
			bounds.Width,
			bounds.Height,
			SetWindowPosNoActivate | SetWindowPosFrameChanged | SetWindowPosShowWindow);
	}

	/// <summary>Drops the topmost flag without moving, resizing or activating the window.</summary>
	public static void ClearTopmost(IntPtr hwnd)
	{
		_ = SetWindowPos(
			hwnd,
			HwndNotTopmost,
			0,
			0,
			0,
			0,
			SetWindowPosNoMove | SetWindowPosNoSize | SetWindowPosNoActivate | SetWindowPosFrameChanged);
	}

	private static nint GetWindowLongPtr(IntPtr hwnd, int index)
	{
		return IntPtr.Size == 8
			? GetWindowLongPtr64(hwnd, index)
			: new IntPtr(GetWindowLong32(hwnd, index));
	}

	private static nint SetWindowLongPtr(IntPtr hwnd, int index, nint value)
	{
		return IntPtr.Size == 8
			? SetWindowLongPtr64(hwnd, index, value)
			: new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));
	}

	[DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
	private static extern nint GetWindowLongPtr64(IntPtr hwnd, int index);

	[DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
	private static extern int GetWindowLong32(IntPtr hwnd, int index);

	[DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
	private static extern nint SetWindowLongPtr64(IntPtr hwnd, int index, nint value);

	[DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
	private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SetWindowPos(
		IntPtr hwnd,
		IntPtr hwndInsertAfter,
		int x,
		int y,
		int width,
		int height,
		uint flags);
}
