using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MacMirrorReceiver.Video;

namespace MacMirrorReceiver;

public class App : Application
{
	public App()
	{
		InstallSharpDxAssemblyResolver();
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
		};
	}

	private static void InstallSharpDxAssemblyResolver()
	{
#if HIGH_RESOLUTION_D3D
		AssemblyLoadContext.Default.Resolving += delegate(AssemblyLoadContext context, AssemblyName assemblyName)
		{
			if (assemblyName.Name?.StartsWith("SharpDX", StringComparison.OrdinalIgnoreCase) != true)
			{
				return null;
			}

			string assemblyPath = Path.Combine(AppContext.BaseDirectory, assemblyName.Name + ".dll");
			if (!File.Exists(assemblyPath))
			{
				AppLog.Write("SharpDX assembly resolve failed; file not found: " + assemblyPath);
				return null;
			}

			AppLog.Write("SharpDX assembly resolve loading: " + assemblyPath);
			return context.LoadFromAssemblyPath(assemblyPath);
		};
#endif
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
