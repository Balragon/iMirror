using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MacMirrorReceiver;

/// <summary>
/// The notification-area menu: a borderless topmost window styled like a Windows 11 menu
/// (rounded corners, acrylic backdrop, theme-following colours) with a status header and the
/// Show / Settings / Exit items. It opens at the cursor and closes when it loses activation, on
/// Escape, or when an item is chosen; the chosen action runs after the menu has closed.
/// </summary>
public partial class TrayMenuWindow : Window
{
	// Windows 11 21H2 introduced rounded corner preferences; 22H2 the system backdrop types.
	private const int RoundedCornersMinimumBuild = 22000;
	private const int SystemBackdropMinimumBuild = 22621;

	private const int DwmUseImmersiveDarkMode = 20;
	private const int DwmWindowCornerPreference = 33;
	private const int DwmSystemBackdropType = 38;
	private const int DwmCornerRoundSmall = 3;
	private const int DwmBackdropTransientWindow = 3;

	private const int GclStyle = -26;
	private const int ClassStyleDropShadow = 0x00020000;

	private const uint SetWindowPosNoSize = 0x0001;
	private const uint SetWindowPosNoZOrder = 0x0004;
	private const uint SetWindowPosNoActivate = 0x0010;
	private const uint MonitorDefaultToNearest = 2;

	private readonly bool _lightTheme;
	private readonly int _cursorX;
	private readonly int _cursorY;
	private Action? _pendingAction;
	private bool _closing;
	private bool _placing;

	internal TrayMenuWindow(TrayMenuStatus status, bool lightTheme, int cursorX, int cursorY)
	{
		InitializeComponent();
		_lightTheme = lightTheme;
		_cursorX = cursorX;
		_cursorY = cursorY;

		TrayMenuPalette palette = TrayMenuPalette.For(lightTheme);
		SetBrush("MenuForegroundBrush", palette.Foreground);
		SetBrush("MenuMutedBrush", palette.MutedForeground);
		SetBrush("MenuHoverBrush", palette.HoverFill);
		SetBrush("MenuPressedBrush", palette.PressedFill);
		SetBrush("MenuDividerBrush", palette.Divider);
		SetBrush("MenuStatusBrush", TrayMenuPalette.ToneColor(status.Tone, lightTheme));
		StatusTextBlock.Text = status.Text;
		TrySetAppIcon(lightTheme);

		SourceInitialized += TrayMenuWindow_SourceInitialized;
		Loaded += delegate
		{
			MoveToCursor();
			Activate();
		};
		SizeChanged += delegate
		{
			MoveToCursor();
		};
		ContentRendered += delegate
		{
			MoveToCursor();
		};
		Deactivated += delegate
		{
			CloseMenu(null);
		};
		PreviewKeyDown += TrayMenuWindow_PreviewKeyDown;
		Closed += delegate
		{
			Action? action = _pendingAction;
			_pendingAction = null;
			if (action != null)
			{
				// Let the menu disappear before the action opens or closes other windows.
				Dispatcher.BeginInvoke(action);
			}
		};
	}

	/// <summary>Raised for the chosen item after the menu has closed.</summary>
	internal event Action? ShowRequested;

	internal event Action? SettingsRequested;

	internal event Action? ExitRequested;

	private void ShowButton_Click(object sender, RoutedEventArgs e) => CloseMenu(() => ShowRequested?.Invoke());

	private void SettingsButton_Click(object sender, RoutedEventArgs e) => CloseMenu(() => SettingsRequested?.Invoke());

	private void ExitButton_Click(object sender, RoutedEventArgs e) => CloseMenu(() => ExitRequested?.Invoke());

	private void TrayMenuWindow_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Escape)
		{
			e.Handled = true;
			CloseMenu(null);
		}
		else if ((e.Key == Key.Down || e.Key == Key.Up) && Keyboard.FocusedElement is not System.Windows.Controls.Button)
		{
			// The first arrow press enters the items; after that the StackPanel cycles focus.
			e.Handled = true;
			(e.Key == Key.Down ? ShowButton : ExitButton).Focus();
		}
	}

	private void CloseMenu(Action? action)
	{
		if (_closing)
		{
			return;
		}
		_closing = true;
		_pendingAction = action;
		Close();
	}

	private void SetBrush(string key, Color color)
	{
		var brush = new SolidColorBrush(color);
		brush.Freeze();
		Resources[key] = brush;
	}

	private void TrySetAppIcon(bool lightTheme)
	{
		try
		{
			AppIconImage.Source = BitmapFrame.Create(AppIconTheme.IconUriFor(lightTheme), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
		}
		catch (Exception ex)
		{
			AppLog.Write("Tray menu icon could not be loaded: " + ex.Message);
		}
	}

	private void TrayMenuWindow_SourceInitialized(object? sender, EventArgs e)
	{
		IntPtr hwnd = new WindowInteropHelper(this).Handle;
		// Open on the cursor's monitor so WPF lays the menu out at that monitor's DPI.
		_ = SetWindowPos(hwnd, IntPtr.Zero, _cursorX, _cursorY, 0, 0, SetWindowPosNoSize | SetWindowPosNoZOrder | SetWindowPosNoActivate);

		TrayMenuPalette palette = TrayMenuPalette.For(_lightTheme);
		int build = Environment.OSVersion.Version.Build;
		bool backdrop = false;
		try
		{
			int dark = _lightTheme ? 0 : 1;
			_ = DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
			if (build >= RoundedCornersMinimumBuild)
			{
				int corner = DwmCornerRoundSmall;
				_ = DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref corner, sizeof(int));
			}
			if (build >= SystemBackdropMinimumBuild)
			{
				var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
				int backdropType = DwmBackdropTransientWindow;
				backdrop = DwmExtendFrameIntoClientArea(hwnd, ref margins) == 0
					&& DwmSetWindowAttribute(hwnd, DwmSystemBackdropType, ref backdropType, sizeof(int)) == 0;
			}
			nint classStyle = GetClassLongPtr(hwnd, GclStyle);
			_ = SetClassLongPtr(hwnd, GclStyle, new IntPtr(classStyle.ToInt64() | ClassStyleDropShadow));
		}
		catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
		{
			backdrop = false;
		}

		if (backdrop)
		{
			// Let the DWM backdrop show through the client area.
			if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
			{
				target.BackgroundColor = Colors.Transparent;
			}
		}
		else
		{
			MenuRoot.Background = new SolidColorBrush(palette.SolidBackground);
			if (build < RoundedCornersMinimumBuild)
			{
				// No DWM border on older Windows: draw one.
				MenuRoot.BorderBrush = new SolidColorBrush(palette.SolidBorder);
				MenuRoot.BorderThickness = new Thickness(1);
				MenuRoot.CornerRadius = new CornerRadius(0);
			}
		}
	}

	// Moves the menu next to the cursor. WPF sizes the window in steps (the default window size
	// first, then SizeToContent, and again after a DPI change on another monitor), and a move can
	// itself trigger a resize, so keep placing until the position matches the current size.
	// Nested calls from SizeChanged during a move are ignored; the loop picks the new size up.
	private void MoveToCursor()
	{
		if (_placing)
		{
			return;
		}
		IntPtr hwnd = new WindowInteropHelper(this).Handle;
		if (hwnd == IntPtr.Zero)
		{
			return;
		}

		var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
		IntPtr monitor = MonitorFromPoint(new NativePoint { X = _cursorX, Y = _cursorY }, MonitorDefaultToNearest);
		if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
		{
			AppLog.Write($"Tray menu placement skipped: no monitor information for {_cursorX},{_cursorY}.");
			return;
		}

		_placing = true;
		try
		{
			for (int attempt = 0; attempt < 4 && GetWindowRect(hwnd, out NativeRect rect); attempt++)
			{
				(int x, int y) = TrayMenuPlacement.Place(
					_cursorX,
					_cursorY,
					rect.Right - rect.Left,
					rect.Bottom - rect.Top,
					monitorInfo.Work.Left,
					monitorInfo.Work.Top,
					monitorInfo.Work.Right,
					monitorInfo.Work.Bottom);
				if (x == rect.Left && y == rect.Top)
				{
					return;
				}
				_ = SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SetWindowPosNoSize | SetWindowPosNoZOrder | SetWindowPosNoActivate);
			}
		}
		finally
		{
			_placing = false;
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Margins
	{
		public int Left;
		public int Right;
		public int Top;
		public int Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativePoint
	{
		public int X;
		public int Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativeRect
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MonitorInfo
	{
		public int Size;
		public NativeRect Monitor;
		public NativeRect Work;
		public uint Flags;
	}

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

	[DllImport("dwmapi.dll")]
	private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

	[DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
	private static extern nint GetClassLongPtr(IntPtr hwnd, int index);

	[DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
	private static extern nint SetClassLongPtr(IntPtr hwnd, int index, nint value);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

	[DllImport("user32.dll")]
	private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
