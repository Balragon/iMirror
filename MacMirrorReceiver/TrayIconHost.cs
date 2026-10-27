using System;
using System.Threading.Tasks;
using System.Windows;
using DrawingIcon = System.Drawing.Icon;
using Forms = System.Windows.Forms;

namespace MacMirrorReceiver;

/// <summary>
/// Owns the notification-area icon and its menu (Show, Settings..., Exit). The window supplies
/// the three actions and the menu's status line; this class owns the NotifyIcon and the icon
/// image, opens a <see cref="TrayMenuWindow"/> on right-click, swaps the image when the theme
/// changes, and disposes the icon and any open menu together.
/// </summary>
internal sealed class TrayIconHost : IDisposable
{
	private readonly Action _showWindow;
	private readonly Action _showSettings;
	private readonly Func<Task> _exit;
	private readonly Func<TrayMenuStatus> _status;
	private Forms.NotifyIcon? _trayIcon;
	private TrayMenuWindow? _openMenu;
	private DrawingIcon? _trayIconImage;

	/// <summary>
	/// Creates the icon. A failure (no shell, broken icon resource) is logged and leaves the host
	/// inactive rather than throwing, so the window still opens.
	/// </summary>
	public TrayIconHost(Action showWindow, Action showSettings, Func<Task> exit, Func<TrayMenuStatus> status)
	{
		_showWindow = showWindow;
		_showSettings = showSettings;
		_exit = exit;
		_status = status;
		try
		{
			_trayIconImage = CreateTrayIconImage();
			_trayIcon = new Forms.NotifyIcon
			{
				Icon = _trayIconImage,
				Text = "iMirror",
				Visible = true
			};
			_trayIcon.MouseUp += (_, e) =>
			{
				if (e.Button == Forms.MouseButtons.Left)
				{
					showWindow();
				}
				else if (e.Button == Forms.MouseButtons.Right)
				{
					System.Drawing.Point cursor = Forms.Cursor.Position;
					ShowMenu(cursor.X, cursor.Y);
				}
			};
			_trayIcon.DoubleClick += delegate
			{
				showWindow();
			};
		}
		catch (Exception ex)
		{
			AppLog.Write("Tray icon initialization failed: " + ex);
			Dispose();
		}
	}

	public bool IsActive => _trayIcon != null;

	/// <summary>
	/// Opens the menu at a screen point in physical pixels, replacing a menu that is still open.
	/// A failure is logged; the tray icon keeps working for left-click.
	/// </summary>
	private void ShowMenu(int cursorX, int cursorY)
	{
		CloseMenu();
		try
		{
			var menu = new TrayMenuWindow(_status(), AppIconTheme.IsSystemLightTheme(), cursorX, cursorY);
			menu.ShowRequested += _showWindow;
			menu.SettingsRequested += _showSettings;
			menu.ExitRequested += async () => await _exit();
			menu.Closed += delegate
			{
				if (ReferenceEquals(_openMenu, menu))
				{
					_openMenu = null;
				}
			};
			_openMenu = menu;
			menu.Show();
		}
		catch (Exception ex)
		{
			_openMenu = null;
			AppLog.Write("Tray menu could not be shown: " + ex);
		}
	}

	private void CloseMenu()
	{
		TrayMenuWindow? menu = _openMenu;
		_openMenu = null;
		menu?.Close();
	}

	/// <summary>
	/// Replaces the tray image with the icon at <paramref name="resourceUri"/> (a pack URI to a
	/// multi-size .ico). The previous image is disposed after the new one is assigned. A failure
	/// is logged and keeps the current image.
	/// </summary>
	public void ApplyIcon(Uri resourceUri)
	{
		Forms.NotifyIcon? trayIcon = _trayIcon;
		if (trayIcon == null)
		{
			return;
		}

		DrawingIcon? previous = _trayIconImage;
		try
		{
			DrawingIcon trayImage = LoadTrayIconImage(resourceUri);
			_trayIconImage = trayImage;
			trayIcon.Icon = trayImage;
			previous?.Dispose();
		}
		catch (Exception ex)
		{
			AppLog.Write($"Tray icon update failed ({resourceUri}): {ex.Message}");
		}
	}

	public void Dispose()
	{
		Forms.NotifyIcon? trayIcon = _trayIcon;
		_trayIcon = null;
		if (trayIcon != null)
		{
			trayIcon.Visible = false;
			trayIcon.Dispose();
		}

		CloseMenu();

		DrawingIcon? trayIconImage = _trayIconImage;
		_trayIconImage = null;
		trayIconImage?.Dispose();
	}

	private static DrawingIcon LoadTrayIconImage(Uri resourceUri)
	{
		System.Windows.Resources.StreamResourceInfo? resource = Application.GetResourceStream(resourceUri);
		if (resource == null)
		{
			throw new InvalidOperationException("Icon resource not found: " + resourceUri);
		}
		using System.IO.Stream stream = resource.Stream;
		// The tray wants a small frame; System.Drawing picks the best match for 32x32 from the
		// multi-size .ico and scales to the shell's current icon size.
		return new DrawingIcon(stream, 32, 32);
	}

	private static DrawingIcon CreateTrayIconImage()
	{
		try
		{
			string? executablePath = Environment.ProcessPath;
			if (!string.IsNullOrWhiteSpace(executablePath))
			{
				DrawingIcon? icon = DrawingIcon.ExtractAssociatedIcon(executablePath);
				if (icon != null)
				{
					return icon;
				}
			}
		}
		catch (Exception ex)
		{
			AppLog.Write("App icon extraction failed: " + ex.Message);
		}

		return (DrawingIcon)System.Drawing.SystemIcons.Application.Clone();
	}
}
