using System;
using System.Threading.Tasks;

namespace MacMirrorReceiver;

internal static class CleanupGuards
{
	public static void RunStep(string name, Action step)
	{
		RunStep(name, step, AppLog.Write);
	}

	internal static void RunStep(string name, Action step, Action<string> log)
	{
		try
		{
			step();
		}
		catch (Exception ex)
		{
			log($"Cleanup step failed ({name}): {ex}");
		}
	}

	public static Task RunStepAsync(string name, Func<Task> step)
	{
		return RunStepAsync(name, step, AppLog.Write);
	}

	internal static async Task RunStepAsync(string name, Func<Task> step, Action<string> log)
	{
		try
		{
			await step();
		}
		catch (Exception ex)
		{
			log($"Cleanup step failed ({name}): {ex}");
		}
	}
}
