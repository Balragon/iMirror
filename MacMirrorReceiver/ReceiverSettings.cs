using System;
using System.IO;
using System.Text.Json;

namespace MacMirrorReceiver;

internal enum ReceiverVideoEngineSetting
{
	Auto = 0,
	Software = 1
}

// Resolved, non-null setting values. This is the contract the rest of the app
// reads: prefer ReceiverSettings.Load().Effective over reading IMIRROR_* env vars
// directly, so persisted user choices and environment overrides resolve in one place.
internal sealed record ReceiverSettingsValues(
	string ReceiverName,
	ReceiverRenderModeSetting RenderMode,
	ReceiverVideoEngineSetting VideoEngine,
	bool AudioEnabled,
	int AudioSyncOffsetMs,
	bool WriteDiagnostics,
	bool DumpH264,
	bool DumpAudio);

// Per-field flags indicating an environment variable is overriding the persisted
// value. Overridden controls are shown disabled with a note in the Settings UI.
internal sealed record ReceiverSettingsOverrides(
	bool RenderMode,
	bool VideoEngine,
	bool AudioEnabled,
	bool AudioSyncOffsetMs,
	bool WriteDiagnostics,
	bool DumpH264,
	bool DumpAudio);

internal sealed record ReceiverSettingsSnapshot(
	ReceiverSettingsValues Persisted,
	ReceiverSettingsValues Effective,
	ReceiverSettingsOverrides Overrides,
	string SettingsPath);

internal static class ReceiverSettings
{
	public const int CurrentSchemaVersion = 1;

	public const string DefaultReceiverName = "iMirror";
	public const int DefaultAudioSyncOffsetMs = 120;
	public const int MinAudioSyncOffsetMs = 60;
	public const int MaxAudioSyncOffsetMs = 220;
	public const int MaxReceiverNameLength = 32;

	public const string VideoEngineEnvironmentVariableName = RenderModeSettings.ForceSoftwareVideoEnvironmentVariableName;
	public const string AudioEnabledEnvironmentVariableName = "IMIRROR_AUDIO_ENABLED";
	public const string AudioSyncOffsetEnvironmentVariableName = "IMIRROR_AUDIO_SYNC_OFFSET_MS";
	public const string WriteDiagnosticsEnvironmentVariableName = "IMIRROR_WRITE_DIAGNOSTICS";
	public const string DumpH264EnvironmentVariableName = "IMIRROR_DUMP_H264";
	public const string DumpAudioEnvironmentVariableName = "IMIRROR_DUMP_AUDIO";

	private const string SettingsDirectoryName = "iMirror";
	private const string SettingsFileName = "settings.json";

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true
	};

	private static readonly object FileGate = new object();

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

	// Reads the raw persisted document. Missing fields stay null so legacy files
	// (render mode only) and forward-compatible additions both load cleanly.
	public static ReceiverSettingsDto LoadDto()
	{
		lock (FileGate)
		{
			string path = SettingsPath;
			if (!File.Exists(path))
			{
				return new ReceiverSettingsDto();
			}

			try
			{
				using FileStream stream = File.OpenRead(path);
				return JsonSerializer.Deserialize<ReceiverSettingsDto>(stream) ?? new ReceiverSettingsDto();
			}
			catch (Exception ex)
			{
				AppLog.Write("Receiver settings could not be loaded: " + ex.Message);
				return new ReceiverSettingsDto();
			}
		}
	}

	// Read-modify-write so a single field change never clobbers other fields in
	// the file. RenderModeSettings.SavePersistedMode and the Settings overlay both
	// go through this path.
	public static void UpdateDto(Action<ReceiverSettingsDto> mutate)
	{
		if (mutate == null)
		{
			throw new ArgumentNullException(nameof(mutate));
		}

		lock (FileGate)
		{
			ReceiverSettingsDto dto = LoadDtoNoLock();
			mutate(dto);
			dto.SchemaVersion = CurrentSchemaVersion;

			string path = SettingsPath;
			string? directory = Path.GetDirectoryName(path);
			if (!string.IsNullOrWhiteSpace(directory))
			{
				Directory.CreateDirectory(directory);
			}

			File.WriteAllText(path, JsonSerializer.Serialize(dto, JsonOptions));
		}
	}

	public static ReceiverSettingsSnapshot Load()
	{
		ReceiverSettingsDto dto = LoadDto();

		RenderModeSettingsSnapshot renderMode = RenderModeSettings.Load();

		string persistedName = NormalizeReceiverName(dto.ReceiverName);
		ReceiverVideoEngineSetting persistedEngine = ParseVideoEngine(dto.VideoEngine) ?? ReceiverVideoEngineSetting.Auto;
		bool persistedAudioEnabled = dto.AudioEnabled ?? true;
		int persistedAudioOffset = ClampAudioOffset(dto.AudioSyncOffsetMs ?? DefaultAudioSyncOffsetMs);
		bool persistedWriteDiagnostics = dto.Diagnostics?.WriteDiagnostics ?? false;
		bool persistedDumpH264 = dto.Diagnostics?.DumpH264 ?? false;
		bool persistedDumpAudio = dto.Diagnostics?.DumpAudio ?? false;

		var persisted = new ReceiverSettingsValues(
			persistedName,
			renderMode.PersistedMode,
			persistedEngine,
			persistedAudioEnabled,
			persistedAudioOffset,
			persistedWriteDiagnostics,
			persistedDumpH264,
			persistedDumpAudio);

		bool engineOverridden = TryGetVideoEngineOverride(out ReceiverVideoEngineSetting engineOverride);
		bool audioEnabledOverridden = TryGetBoolOverride(AudioEnabledEnvironmentVariableName, out bool audioEnabledOverride);
		bool audioOffsetOverridden = TryGetAudioOffsetOverride(out int audioOffsetOverride);
		bool writeDiagOverridden = TryGetBoolOverride(WriteDiagnosticsEnvironmentVariableName, out bool writeDiagOverride);
		bool dumpH264Overridden = TryGetBoolOverride(DumpH264EnvironmentVariableName, out bool dumpH264Override);
		bool dumpAudioOverridden = TryGetBoolOverride(DumpAudioEnvironmentVariableName, out bool dumpAudioOverride);

		var effective = new ReceiverSettingsValues(
			persistedName,
			renderMode.EffectiveMode,
			engineOverridden ? engineOverride : persistedEngine,
			audioEnabledOverridden ? audioEnabledOverride : persistedAudioEnabled,
			audioOffsetOverridden ? audioOffsetOverride : persistedAudioOffset,
			writeDiagOverridden ? writeDiagOverride : persistedWriteDiagnostics,
			dumpH264Overridden ? dumpH264Override : persistedDumpH264,
			dumpAudioOverridden ? dumpAudioOverride : persistedDumpAudio);

		var overrides = new ReceiverSettingsOverrides(
			renderMode.HasEnvironmentOverride,
			engineOverridden,
			audioEnabledOverridden,
			audioOffsetOverridden,
			writeDiagOverridden,
			dumpH264Overridden,
			dumpAudioOverridden);

		return new ReceiverSettingsSnapshot(persisted, effective, overrides, SettingsPath);
	}

	public static string NormalizeReceiverName(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return DefaultReceiverName;
		}

		string trimmed = value.Trim();
		if (trimmed.Length > MaxReceiverNameLength)
		{
			trimmed = trimmed.Substring(0, MaxReceiverNameLength).TrimEnd();
		}

		return trimmed.Length == 0 ? DefaultReceiverName : trimmed;
	}

	public static int ClampAudioOffset(int value)
	{
		return Math.Clamp(value, MinAudioSyncOffsetMs, MaxAudioSyncOffsetMs);
	}

	public static string ToVideoEngineValue(ReceiverVideoEngineSetting engine)
	{
		return engine == ReceiverVideoEngineSetting.Software ? "software" : "auto";
	}

	private static ReceiverSettingsDto LoadDtoNoLock()
	{
		string path = SettingsPath;
		if (!File.Exists(path))
		{
			return new ReceiverSettingsDto();
		}

		try
		{
			using FileStream stream = File.OpenRead(path);
			return JsonSerializer.Deserialize<ReceiverSettingsDto>(stream) ?? new ReceiverSettingsDto();
		}
		catch (Exception ex)
		{
			AppLog.Write("Receiver settings could not be loaded for update: " + ex.Message);
			return new ReceiverSettingsDto();
		}
	}

	private static ReceiverVideoEngineSetting? ParseVideoEngine(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}

		switch (value.Trim().ToLowerInvariant())
		{
		case "software":
		case "ffmpeg":
		case "cpu":
			return ReceiverVideoEngineSetting.Software;
		case "auto":
		case "gpu":
		case "hardware":
			return ReceiverVideoEngineSetting.Auto;
		default:
			return null;
		}
	}

	private static bool TryGetVideoEngineOverride(out ReceiverVideoEngineSetting engine)
	{
		// The engine selector env (IMIRROR_FORCE_SOFTWARE_VIDEO) only forces software
		// when truthy; otherwise it does not override the persisted choice.
		if (TryGetBoolOverride(VideoEngineEnvironmentVariableName, out bool forceSoftware) && forceSoftware)
		{
			engine = ReceiverVideoEngineSetting.Software;
			return true;
		}

		engine = ReceiverVideoEngineSetting.Auto;
		return false;
	}

	private static bool TryGetAudioOffsetOverride(out int value)
	{
		string? raw = Environment.GetEnvironmentVariable(AudioSyncOffsetEnvironmentVariableName);
		if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out int parsed))
		{
			value = ClampAudioOffset(parsed);
			return true;
		}

		value = DefaultAudioSyncOffsetMs;
		return false;
	}

	private static bool TryGetBoolOverride(string environmentVariableName, out bool value)
	{
		bool? parsed = ParseBoolish(Environment.GetEnvironmentVariable(environmentVariableName));
		value = parsed ?? false;
		return parsed.HasValue;
	}

	private static bool? ParseBoolish(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}

		switch (value.Trim().ToLowerInvariant())
		{
		case "1":
		case "true":
		case "yes":
		case "on":
			return true;
		case "0":
		case "false":
		case "no":
		case "off":
			return false;
		default:
			return null;
		}
	}
}

internal sealed class ReceiverSettingsDto
{
	public int SchemaVersion { get; set; } = ReceiverSettings.CurrentSchemaVersion;
	public string? ReceiverName { get; set; }
	public string? RenderMode { get; set; }
	public string? VideoEngine { get; set; }
	public bool? AudioEnabled { get; set; }
	public int? AudioSyncOffsetMs { get; set; }
	public ReceiverDiagnosticsDto? Diagnostics { get; set; }
}

internal sealed class ReceiverDiagnosticsDto
{
	public bool? WriteDiagnostics { get; set; }
	public bool? DumpH264 { get; set; }
	public bool? DumpAudio { get; set; }
}
