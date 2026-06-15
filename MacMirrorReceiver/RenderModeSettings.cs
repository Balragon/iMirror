using System;
using System.IO;
using System.Text.Json;

namespace MacMirrorReceiver;

internal enum ReceiverRenderModeSetting
{
	Stable = 0,
	Quality = 1
}

internal sealed record RenderModeSettingsSnapshot(
	ReceiverRenderModeSetting PersistedMode,
	ReceiverRenderModeSetting EffectiveMode,
	bool HasEnvironmentOverride,
	string? EnvironmentOverrideValue,
	string SettingsPath);

internal static class RenderModeSettings
{
	public const string EnvironmentVariableName = "IMIRROR_RENDER_MODE";

	public const string ExperimentalMpvEnvironmentVariableName = "IMIRROR_EXPERIMENTAL_MPV";

	public const string ExperimentalQualityEnvironmentVariableName = "IMIRROR_EXPERIMENTAL_QUALITY";

	private const string SettingsDirectoryName = "iMirror";
	private const string SettingsFileName = "settings.json";
	private const string QualityValue = "quality";
	private const string StableValue = "stable";

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true
	};

	public static string SettingsPath
	{
		get
		{
			string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
			if (string.IsNullOrWhiteSpace(appData))
			{
				appData = AppContext.BaseDirectory;
			}

			return Path.Combine(appData, SettingsDirectoryName, SettingsFileName);
		}
	}

	public static RenderModeSettingsSnapshot Load()
	{
		ReceiverRenderModeSetting persistedMode = LoadPersistedMode();
		string? environmentValue = Environment.GetEnvironmentVariable(EnvironmentVariableName);
		bool hasEnvironmentOverride = TryParseMode(environmentValue, out ReceiverRenderModeSetting environmentMode);
		return new RenderModeSettingsSnapshot(
			persistedMode,
			hasEnvironmentOverride ? environmentMode : persistedMode,
			hasEnvironmentOverride,
			hasEnvironmentOverride ? environmentValue : null,
			SettingsPath);
	}

	public static bool ExperimentalMpvQualityEnabled => string.Equals(
		Environment.GetEnvironmentVariable(ExperimentalMpvEnvironmentVariableName),
		"1",
		StringComparison.OrdinalIgnoreCase);

	public static bool ExperimentalWpfQualityEnabled => string.Equals(
		Environment.GetEnvironmentVariable(ExperimentalQualityEnvironmentVariableName),
		"1",
		StringComparison.OrdinalIgnoreCase);

	public const string ForceSoftwareVideoEnvironmentVariableName = "IMIRROR_FORCE_SOFTWARE_VIDEO";

	// Explicit opt-out: force the software (FFmpeg) engine even when GPU hardware decode is available.
	public static bool ForceSoftwareVideoRequested => string.Equals(
		Environment.GetEnvironmentVariable(ForceSoftwareVideoEnvironmentVariableName),
		"1",
		StringComparison.OrdinalIgnoreCase);

	// The GPU (MediaFoundation/D3D11 decode + swap-chain present) engine is the default whenever the
	// hardware supports it, unless the software engine is explicitly forced. Native resolution is then
	// advertised and the GPU path is used, with the FFmpeg software path as the runtime fallback.
	public static bool GpuVideoEngineEnabled =>
		Video.HighResolutionPipelineProbe.IsHardwareDecodeAvailable && !ForceSoftwareVideoRequested;

	public static void SavePersistedMode(ReceiverRenderModeSetting mode)
	{
		string path = SettingsPath;
		string? directory = Path.GetDirectoryName(path);
		if (!string.IsNullOrWhiteSpace(directory))
		{
			Directory.CreateDirectory(directory);
		}

		var dto = new SettingsDto
		{
			RenderMode = ToSettingValue(mode)
		};
		File.WriteAllText(path, JsonSerializer.Serialize(dto, JsonOptions));
	}

	public static string ToSettingValue(ReceiverRenderModeSetting mode)
	{
		return mode == ReceiverRenderModeSetting.Quality ? QualityValue : StableValue;
	}

	private static ReceiverRenderModeSetting LoadPersistedMode()
	{
		string path = SettingsPath;
		if (!File.Exists(path))
		{
			return ReceiverRenderModeSetting.Stable;
		}

		try
		{
			using FileStream stream = File.OpenRead(path);
			SettingsDto? dto = JsonSerializer.Deserialize<SettingsDto>(stream);
			if (TryParseMode(dto?.RenderMode, out ReceiverRenderModeSetting mode))
			{
				return mode;
			}
		}
		catch (Exception ex)
		{
			AppLog.Write("Render mode settings could not be loaded: " + ex.Message);
		}

		return ReceiverRenderModeSetting.Stable;
	}

	private static bool TryParseMode(string? value, out ReceiverRenderModeSetting mode)
	{
		mode = ReceiverRenderModeSetting.Stable;
		if (string.IsNullOrWhiteSpace(value))
		{
			return false;
		}

		switch (value.Trim().ToLowerInvariant())
		{
		case QualityValue:
			mode = ReceiverRenderModeSetting.Quality;
			return true;
		case StableValue:
		case "default":
		case "auto":
		case "off":
		case "0":
			mode = ReceiverRenderModeSetting.Stable;
			return true;
		default:
			return false;
		}
	}

	private sealed class SettingsDto
	{
		public string? RenderMode { get; set; }
	}
}
