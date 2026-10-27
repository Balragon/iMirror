using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MacMirrorReceiver.Video;

namespace MacMirrorReceiver;

public class App : Application
{
	private readonly Mutex? _applicationMutex;

	public App()
		: this(null)
	{
	}

	private App(Mutex? applicationMutex)
	{
		_applicationMutex = applicationMutex;
		AppLog.Write("App constructed.");
		Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary
		{
			Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark,
		});
		Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
		base.DispatcherUnhandledException += OnDispatcherUnhandledException;
		AppDomain.CurrentDomain.UnhandledException += delegate(object _, UnhandledExceptionEventArgs args)
		{
			AppLog.Write("Unhandled domain exception: " + args.ExceptionObject);
			// The process is about to die and ProcessExit will not run; get the queued lines out now.
			AppLog.Flush();
		};
	}

	protected override void OnStartup(StartupEventArgs e)
	{
		AppLog.Write("OnStartup entered.");
		ShutdownMode = ShutdownMode.OnExplicitShutdown;
		AppLog.Write($"WPF render tier: {System.Windows.Media.RenderCapability.Tier >> 16} (2=full hardware, 1=partial, 0=software).");
		HighResolutionPipelineProbe.RunIfEnabled();
		base.OnStartup(e);
		MainWindow mainWindow = (MainWindow)(base.MainWindow = new MainWindow());
		mainWindow.ShowInTaskbar = true;
		mainWindow.WindowState = WindowState.Normal;
		mainWindow.Show();
		IntPtr handle = new WindowInteropHelper(mainWindow).EnsureHandle();
		mainWindow.Activate();
		mainWindow.Topmost = true;
		mainWindow.Topmost = false;
		AppLog.Write($"MainWindow handle ensured: 0x{handle.ToInt64():X}.");
		AppLog.Write("MainWindow.Show returned.");
	}

	protected override void OnExit(ExitEventArgs e)
	{
		_applicationMutex?.Dispose();
		base.OnExit(e);
	}

	/// <summary>
	/// Releases and closes the single-instance mutex ahead of normal exit. The silent update
	/// installer checks this mutex (installer AppMutex) when it starts, which is before this
	/// process has finished shutting down; while the mutex still exists the installer aborts
	/// with /SUPPRESSMSGBOXES and the user is left with no app and no update. Must run on the
	/// thread that acquired the mutex (the UI thread, which is where the update flow runs).
	/// </summary>
	internal static void ReleaseSingleInstanceMutex()
	{
		Mutex? mutex = (Current as App)?._applicationMutex;
		if (mutex == null)
		{
			return;
		}

		try
		{
			mutex.ReleaseMutex();
		}
		catch (ApplicationException)
		{
			// Not owned by this thread; closing the handle below still removes our reference.
		}
		catch (ObjectDisposedException)
		{
			return;
		}

		mutex.Dispose();
		AppLog.Write("Single-instance mutex released for update installer handoff.");
	}

	private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
	{
		AppLog.Write("Dispatcher exception: " + e.Exception);
		// Unhandled, so the app is going down; the background writer would die with the queued lines.
		AppLog.Flush();
	}

	// Long enough to cover a restart handoff (settings "Save & Restart" spawns the new
	// process before the old one finishes disconnecting and shutting down), short enough
	// that a genuine duplicate launch still exits promptly.
	private static readonly TimeSpan PreviousInstanceExitTimeout = TimeSpan.FromSeconds(5);

	[STAThread]
	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.27.0")]
	public static void Main()
	{
		using Mutex applicationMutex = new Mutex(initiallyOwned: true, AppUpdateConstants.ApplicationMutexName, out bool createdNew);
		if (!createdNew)
		{
			AppLog.Write(
				$"Another iMirror instance holds the single-instance mutex; waiting up to {PreviousInstanceExitTimeout.TotalSeconds:N0}s " +
				"for it to exit (restart handoff).");
			if (!SingleInstanceGate.TryAcquireExistingMutex(applicationMutex, PreviousInstanceExitTimeout))
			{
				AppLog.Write("Another iMirror instance is already running; exiting duplicate instance.");
				return;
			}

			AppLog.Write("Previous iMirror instance released the single-instance mutex; continuing startup.");
		}

		new App(applicationMutex).Run();
	}
}
