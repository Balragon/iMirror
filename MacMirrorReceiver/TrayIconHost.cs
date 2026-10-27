using System;
using System.Threading.Tasks;
using System.Windows;
using DrawingIcon = System.Drawing.Icon;
using Forms = System.Windows.Forms;

namespace MacMirrorReceiver;

/// <summary>
/// Owns the notification-area icon and its menu (Show, Settings..., Exit). The window supplies
/// the three actions; this class owns the NotifyIcon, the menu and the icon image, swaps the
/// image when the theme changes, and disposes all three together.
/// </summary>
internal sealed class TrayIconHost : IDisposable
{
	private Forms.NotifyIcon? _trayIcon;
	private Forms.ContextMenuStrip? _trayMenu;
	private DrawingIcon? _trayIconImage;

	/// <summary>
	/// Creates the icon. A failure (no shell, broken icon resource) is logged and leaves the host
	/// inactive rather than throwing, so the window still opens.
	/// </summary>
	public TrayIconHost(Action showWindow, Action showSettings, Func<Task> exit)
	{
		try
		{
			_trayIconImage = CreateTrayIconImage();
			_trayMenu = new Forms.ContextMenuStrip();
			var showItem = new Forms.ToolStripMenuItem("Show iMirror");
			showItem.Click += delegate
			{
				showWindow();
			};
			var settingsItem = new Forms.ToolStripMenuItem("Settings...");
			settingsItem.Click += delegate
			{
				showSettings();
			};
			var exitItem = new Forms.ToolStripMenuItem("Exit");
			exitItem.Click += async delegate
			{
				await exit();
			};
			_trayMenu.Items.Add(showItem);
			_trayMenu.Items.Add(settingsItem);
			_trayMenu.Items.Add(new Forms.ToolStripSeparator());
			_trayMenu.Items.Add(exitItem);

			_trayIcon = new Forms.NotifyIcon
			{
				ContextMenuStrip = _trayMenu,
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

		Forms.ContextMenuStrip? trayMenu = _trayMenu;
		_trayMenu = null;
		trayMenu?.Dispose();

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
