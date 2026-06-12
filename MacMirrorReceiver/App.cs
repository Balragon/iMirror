using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MacMirrorReceiver;

public class App : Application
{
	public App()
	{
		AppLog.Write("App constructed.");
		base.DispatcherUnhandledException += OnDispatcherUnhandledException;
		AppDomain.CurrentDomain.UnhandledException += delegate(object _, UnhandledExceptionEventArgs args)
		{
			AppLog.Write("Unhandled domain exception: " + args.ExceptionObject);
		};
	}

	protected override void OnStartup(StartupEventArgs e)
	{
		AppLog.Write("OnStartup entered.");
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

	private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
	{
		AppLog.Write("Dispatcher exception: " + e.Exception);
	}

	[STAThread]
	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.27.0")]
	public static void Main()
	{
		new App().Run();
	}
}
