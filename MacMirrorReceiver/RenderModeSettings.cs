using System;

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

	public const string QualityEnvironmentVariableName = "IMIRROR_EXPERIMENTAL_QUALITY";

	private const string QualityValue = "quality";
	private const string StableValue = "stable";

	// The settings file is owned by ReceiverSettings; render mode is one field in it.
	public static string SettingsPath => ReceiverSettings.SettingsPath;

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

	public static bool ExperimentalQualityEnabled => string.Equals(
		Environment.GetEnvironmentVariable(QualityEnvironmentVariableName),
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
		ReceiverSettings.UpdateDto(dto => dto.RenderMode = ToSettingValue(mode));
	}

	public static string ToSettingValue(ReceiverRenderModeSetting mode)
	{
		return mode == ReceiverRenderModeSetting.Quality ? QualityValue : StableValue;
	}

	private static ReceiverRenderModeSetting LoadPersistedMode()
	{
		if (TryParseMode(ReceiverSettings.LoadDto().RenderMode, out ReceiverRenderModeSetting mode))
		{
			return mode;
		}

		return ReceiverRenderModeSetting.Quality;
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
}
