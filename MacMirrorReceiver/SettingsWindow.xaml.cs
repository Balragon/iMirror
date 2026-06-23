using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;

namespace MacMirrorReceiver;

internal interface ISettingsHost
{
	ReceiverSettingsSnapshot StartupReceiverSettings { get; }
	RenderModeSettingsSnapshot StartupRenderModeSettings { get; }
	bool GpuQualityRequested { get; }
	bool QualityPathAvailable { get; }
	int LiveAudioSyncOffsetMilliseconds { get; }
	void SetLiveAudioSyncOffsetMilliseconds(int value);
	Task RestartApplicationAsync();
	void SetStatusMessage(string message);
}

public partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow
{
	private readonly ISettingsHost _host;

	private ReceiverRenderModeSetting _pendingRenderModeSetting;
	private bool _renderModeSettingsUiReady;
	private bool _receiverSettingsUiReady;
	private string _pendingReceiverName;
	private ReceiverVideoEngineSetting _pendingVideoEngine;
	private bool _pendingAudioEnabled;
	private bool _pendingWriteDiagnostics;
	private bool _pendingDumpH264;
	private bool _pendingDumpAudio;

	internal SettingsWindow(ISettingsHost host)
	{
		_host = host ?? throw new ArgumentNullException(nameof(host));
		_pendingRenderModeSetting = _host.StartupRenderModeSettings.PersistedMode;
		_pendingReceiverName = _host.StartupReceiverSettings.Persisted.ReceiverName;
		_pendingVideoEngine = _host.StartupReceiverSettings.Persisted.VideoEngine;
		_pendingAudioEnabled = _host.StartupReceiverSettings.Persisted.AudioEnabled;
		_pendingWriteDiagnostics = _host.StartupReceiverSettings.Persisted.WriteDiagnostics;
		_pendingDumpH264 = _host.StartupReceiverSettings.Persisted.DumpH264;
		_pendingDumpAudio = _host.StartupReceiverSettings.Persisted.DumpAudio;

		InitializeComponent();
		Wpf.Ui.Appearance.ApplicationThemeManager.Apply(this);
		VersionTextBlock.Text = AppVersionInfo.DisplayText;
		InitializeRenderModeSettingsUi();
		InitializeReceiverSettingsUi();
	}

	protected override void OnContentRendered(EventArgs e)
	{
		base.OnContentRendered(e);
		ReceiverNameTextBox.Focus();
		ReceiverNameTextBox.SelectAll();
	}

	private void SettingsWindow_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Escape)
		{
			e.Handled = true;
			Close();
		}
	}

	private void SettingsWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
	{
		PersistLiveAudioSyncOffset();
	}

	private void InitializeRenderModeSettingsUi()
	{
		_renderModeSettingsUiReady = false;
		RenderModeSettingsSnapshot renderModeSettings = _host.StartupRenderModeSettings;
		ReceiverRenderModeSetting displayedMode = renderModeSettings.HasEnvironmentOverride
			? renderModeSettings.EffectiveMode
			: renderModeSettings.PersistedMode;
		if (!renderModeSettings.HasEnvironmentOverride
			&& !_host.QualityPathAvailable
			&& displayedMode == ReceiverRenderModeSetting.Quality)
		{
			displayedMode = ReceiverRenderModeSetting.Stable;
		}

		_pendingRenderModeSetting = displayedMode;
		StableRenderModeRadioButton.IsChecked = displayedMode == ReceiverRenderModeSetting.Stable;
		QualityRenderModeRadioButton.IsChecked = displayedMode == ReceiverRenderModeSetting.Quality;
		bool canEditSetting = !renderModeSettings.HasEnvironmentOverride;
		StableRenderModeRadioButton.IsEnabled = canEditSetting;
		QualityRenderModeRadioButton.IsEnabled = canEditSetting && _host.QualityPathAvailable;
		RenderModeOverrideTextBlock.Visibility = renderModeSettings.HasEnvironmentOverride
			? Visibility.Visible
			: Visibility.Collapsed;
		if (renderModeSettings.HasEnvironmentOverride)
		{
			RenderModeOverrideTextBlock.Text = $"{RenderModeSettings.EnvironmentVariableName}={renderModeSettings.EnvironmentOverrideValue} is overriding this setting.";
		}
		_renderModeSettingsUiReady = true;
		UpdateRenderModeSettingsUi();
	}

	private void RenderModeSettingRadioButton_Checked(object sender, RoutedEventArgs e)
	{
		if (!_renderModeSettingsUiReady || _host.StartupRenderModeSettings.HasEnvironmentOverride)
		{
			return;
		}

		_pendingRenderModeSetting = QualityRenderModeRadioButton.IsChecked == true
			? ReceiverRenderModeSetting.Quality
			: ReceiverRenderModeSetting.Stable;
		UpdateRenderModeSettingsUi();
	}

	private void UpdateRenderModeSettingsUi()
	{
		bool qualitySelected = _pendingRenderModeSetting == ReceiverRenderModeSetting.Quality;
		if (!qualitySelected)
		{
			RenderModeSettingDetailTextBlock.Text = "Compatibility mode advertises a 1080p AirPlay display.";
			RenderModeNoteTextBlock.Visibility = Visibility.Collapsed;
		}
		else if (_host.GpuQualityRequested)
		{
#if HIGH_RESOLUTION_D3D
			RenderModeSettingDetailTextBlock.Text = "Native GPU mode uses Media Foundation/D3D11 decode and present.";
			RenderModeNoteTextBlock.Text = "Falls back to the software decoder if GPU startup fails.";
#else
			RenderModeSettingDetailTextBlock.Text = "Native GPU mode requires a HIGH_RESOLUTION_D3D build.";
			RenderModeNoteTextBlock.Text = "This build will use the 1080p compatibility path.";
#endif
			RenderModeNoteTextBlock.Visibility = Visibility.Visible;
		}
		else
		{
			RenderModeSettingDetailTextBlock.Text = "GPU native mode is unavailable; iMirror will advertise 1080p.";
#if HIGH_RESOLUTION_D3D
			RenderModeNoteTextBlock.Text = "Set IMIRROR_EXPERIMENTAL_QUALITY=1 to force the GPU path for local validation.";
#else
			RenderModeNoteTextBlock.Text = "Build with HIGH_RESOLUTION_D3D to enable the GPU path.";
#endif
			RenderModeNoteTextBlock.Visibility = Visibility.Visible;
		}
		UpdateSettingsDirtyState();
	}

	private void InitializeReceiverSettingsUi()
	{
		_receiverSettingsUiReady = false;

		ReceiverSettingsValues persisted = _host.StartupReceiverSettings.Persisted;
		ReceiverSettingsValues effective = _host.StartupReceiverSettings.Effective;
		ReceiverSettingsOverrides overrides = _host.StartupReceiverSettings.Overrides;

		_pendingReceiverName = persisted.ReceiverName;
		ReceiverNameTextBox.Text = persisted.ReceiverName;
		ReceiverNameTextBox.MaxLength = ReceiverSettings.MaxReceiverNameLength;

		_pendingVideoEngine = overrides.VideoEngine ? effective.VideoEngine : persisted.VideoEngine;
		AutoVideoEngineRadioButton.IsChecked = _pendingVideoEngine == ReceiverVideoEngineSetting.Auto;
		SoftwareVideoEngineRadioButton.IsChecked = _pendingVideoEngine == ReceiverVideoEngineSetting.Software;
		AutoVideoEngineRadioButton.IsEnabled = !overrides.VideoEngine;
		SoftwareVideoEngineRadioButton.IsEnabled = !overrides.VideoEngine;
		SetOverrideNote(VideoEngineOverrideTextBlock, overrides.VideoEngine, ReceiverSettings.VideoEngineEnvironmentVariableName);

		_pendingAudioEnabled = overrides.AudioEnabled ? effective.AudioEnabled : persisted.AudioEnabled;
		AudioEnabledCheckBox.IsChecked = _pendingAudioEnabled;
		AudioEnabledCheckBox.IsEnabled = !overrides.AudioEnabled;
		SetOverrideNote(AudioEnabledOverrideTextBlock, overrides.AudioEnabled, ReceiverSettings.AudioEnabledEnvironmentVariableName);

		int offset = overrides.AudioSyncOffsetMs ? effective.AudioSyncOffsetMs : _host.LiveAudioSyncOffsetMilliseconds;
		_host.SetLiveAudioSyncOffsetMilliseconds(offset);
		AudioSyncOffsetSlider.Minimum = ReceiverSettings.MinAudioSyncOffsetMs;
		AudioSyncOffsetSlider.Maximum = ReceiverSettings.MaxAudioSyncOffsetMs;
		AudioSyncOffsetSlider.Value = offset;
		AudioSyncOffsetSlider.IsEnabled = !overrides.AudioSyncOffsetMs;
		AudioSyncOffsetValueText.Text = $"{offset} ms";
		SetOverrideNote(AudioSyncOffsetOverrideTextBlock, overrides.AudioSyncOffsetMs, ReceiverSettings.AudioSyncOffsetEnvironmentVariableName);

		_pendingWriteDiagnostics = overrides.WriteDiagnostics ? effective.WriteDiagnostics : persisted.WriteDiagnostics;
		_pendingDumpH264 = overrides.DumpH264 ? effective.DumpH264 : persisted.DumpH264;
		_pendingDumpAudio = overrides.DumpAudio ? effective.DumpAudio : persisted.DumpAudio;
		WriteDiagnosticsCheckBox.IsChecked = _pendingWriteDiagnostics;
		DumpH264CheckBox.IsChecked = _pendingDumpH264;
		DumpAudioCheckBox.IsChecked = _pendingDumpAudio;
		WriteDiagnosticsCheckBox.IsEnabled = !overrides.WriteDiagnostics;
		DumpH264CheckBox.IsEnabled = !overrides.DumpH264;
		DumpAudioCheckBox.IsEnabled = !overrides.DumpAudio;
		DiagnosticsOverrideTextBlock.Visibility = (overrides.WriteDiagnostics || overrides.DumpH264 || overrides.DumpAudio)
			? Visibility.Visible
			: Visibility.Collapsed;

		_receiverSettingsUiReady = true;
		UpdateSettingsDirtyState();
	}

	private static void SetOverrideNote(TextBlock textBlock, bool overridden, string environmentVariableName)
	{
		if (overridden)
		{
			textBlock.Text = $"{environmentVariableName} is overriding this setting.";
			textBlock.Visibility = Visibility.Visible;
		}
		else
		{
			textBlock.Visibility = Visibility.Collapsed;
		}
	}

	private void ReceiverNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		if (!_receiverSettingsUiReady)
		{
			return;
		}

		_pendingReceiverName = ReceiverSettings.NormalizeReceiverName(ReceiverNameTextBox.Text);
		UpdateSettingsDirtyState();
	}

	private void VideoEngineRadioButton_Checked(object sender, RoutedEventArgs e)
	{
		if (!_receiverSettingsUiReady || _host.StartupReceiverSettings.Overrides.VideoEngine)
		{
			return;
		}

		_pendingVideoEngine = SoftwareVideoEngineRadioButton.IsChecked == true
			? ReceiverVideoEngineSetting.Software
			: ReceiverVideoEngineSetting.Auto;
		UpdateSettingsDirtyState();
	}

	private void AudioEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
	{
		if (!_receiverSettingsUiReady || _host.StartupReceiverSettings.Overrides.AudioEnabled)
		{
			return;
		}

		_pendingAudioEnabled = AudioEnabledCheckBox.IsChecked == true;
		UpdateSettingsDirtyState();
	}

	private void AudioSyncOffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		int value = ReceiverSettings.ClampAudioOffset((int)Math.Round(e.NewValue));
		if (AudioSyncOffsetValueText != null)
		{
			AudioSyncOffsetValueText.Text = $"{value} ms";
		}

		if (!_receiverSettingsUiReady || _host.StartupReceiverSettings.Overrides.AudioSyncOffsetMs)
		{
			return;
		}

		_host.SetLiveAudioSyncOffsetMilliseconds(value);
	}

	private void DiagnosticsCheckBox_Changed(object sender, RoutedEventArgs e)
	{
		if (!_receiverSettingsUiReady)
		{
			return;
		}

		ReceiverSettingsOverrides overrides = _host.StartupReceiverSettings.Overrides;
		if (!overrides.WriteDiagnostics)
		{
			_pendingWriteDiagnostics = WriteDiagnosticsCheckBox.IsChecked == true;
		}
		if (!overrides.DumpH264)
		{
			_pendingDumpH264 = DumpH264CheckBox.IsChecked == true;
		}
		if (!overrides.DumpAudio)
		{
			_pendingDumpAudio = DumpAudioCheckBox.IsChecked == true;
		}
		UpdateSettingsDirtyState();
	}

	private void UpdateSettingsDirtyState()
	{
		if (!_receiverSettingsUiReady)
		{
			return;
		}

		ReceiverSettingsValues persisted = _host.StartupReceiverSettings.Persisted;
		ReceiverSettingsOverrides overrides = _host.StartupReceiverSettings.Overrides;
		RenderModeSettingsSnapshot renderModeSettings = _host.StartupRenderModeSettings;

		bool restartRequired =
			(!renderModeSettings.HasEnvironmentOverride && _pendingRenderModeSetting != renderModeSettings.PersistedMode)
			|| !string.Equals(_pendingReceiverName, persisted.ReceiverName, StringComparison.Ordinal)
			|| (!overrides.VideoEngine && _pendingVideoEngine != persisted.VideoEngine)
			|| (!overrides.AudioEnabled && _pendingAudioEnabled != persisted.AudioEnabled)
			|| (!overrides.WriteDiagnostics && _pendingWriteDiagnostics != persisted.WriteDiagnostics)
			|| (!overrides.DumpH264 && _pendingDumpH264 != persisted.DumpH264)
			|| (!overrides.DumpAudio && _pendingDumpAudio != persisted.DumpAudio);

		SettingsRestartPanel.Visibility = restartRequired ? Visibility.Visible : Visibility.Collapsed;
	}

	private void SettingsCloseButton_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private void UpdatesHyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
	{
		e.Handled = true;
		try
		{
			AppVersionInfo.OpenReleasesPage();
		}
		catch (Exception ex)
		{
			AppLog.Write("Could not open releases page: " + ex.Message);
			_host.SetStatusMessage("Could not open updates page: " + ex.Message);
		}
	}

	private void PersistLiveAudioSyncOffset()
	{
		if (_host.StartupReceiverSettings.Overrides.AudioSyncOffsetMs)
		{
			return;
		}

		int offset = _host.LiveAudioSyncOffsetMilliseconds;
		if (offset == ReceiverSettings.Load().Persisted.AudioSyncOffsetMs)
		{
			return;
		}

		try
		{
			ReceiverSettings.UpdateDto(dto => dto.AudioSyncOffsetMs = offset);
		}
		catch (Exception ex)
		{
			AppLog.Write("Audio sync offset could not be saved: " + ex.Message);
		}
	}

	private async void SettingsRestartButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			ReceiverSettingsOverrides overrides = _host.StartupReceiverSettings.Overrides;
			ReceiverSettings.UpdateDto(dto =>
			{
				dto.ReceiverName = _pendingReceiverName;

				if (!_host.StartupRenderModeSettings.HasEnvironmentOverride)
				{
					dto.RenderMode = RenderModeSettings.ToSettingValue(_pendingRenderModeSetting);
				}
				if (!overrides.VideoEngine)
				{
					dto.VideoEngine = ReceiverSettings.ToVideoEngineValue(_pendingVideoEngine);
				}
				if (!overrides.AudioEnabled)
				{
					dto.AudioEnabled = _pendingAudioEnabled;
				}

				dto.AudioSyncOffsetMs = _host.LiveAudioSyncOffsetMilliseconds;

				dto.Diagnostics ??= new ReceiverDiagnosticsDto();
				if (!overrides.WriteDiagnostics)
				{
					dto.Diagnostics.WriteDiagnostics = _pendingWriteDiagnostics;
				}
				if (!overrides.DumpH264)
				{
					dto.Diagnostics.DumpH264 = _pendingDumpH264;
				}
				if (!overrides.DumpAudio)
				{
					dto.Diagnostics.DumpAudio = _pendingDumpAudio;
				}
			});

			AppLog.Write("Settings saved; restarting.");
			await _host.RestartApplicationAsync();
		}
		catch (Exception ex)
		{
			AppLog.Write("Settings restart failed: " + ex);
			_host.SetStatusMessage("Could not restart iMirror: " + ex.Message);
		}
	}
}
