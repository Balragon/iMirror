using MacMirrorReceiver;
using MacMirrorReceiver.Protocol;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class VideoEngineGateTests
{
	[Fact]
	public void ShouldUseHighResolutionD3DPath_ReturnsFalse_WhenSoftwareEngineSelected()
	{
		var config = new StreamConfig { Width = 2560, Height = 1440, Fps = 30 };

		bool result = MainWindow.ShouldUseHighResolutionD3DPath(
			config,
			ReceiverVideoEngineSetting.Software,
			qualityRenderModeEnabled: true,
			gpuQualityRequested: true);

		Assert.False(result);
	}

	[Fact]
	public void ShouldUseHighResolutionD3DPath_ReturnsTrue_ForAutoHighResolutionGpuSession()
	{
		var config = new StreamConfig { Width = 2560, Height = 1440, Fps = 30 };

		bool result = MainWindow.ShouldUseHighResolutionD3DPath(
			config,
			ReceiverVideoEngineSetting.Auto,
			qualityRenderModeEnabled: true,
			gpuQualityRequested: true);

		Assert.True(result);
	}

	[Fact]
	public void ShouldUseHighResolutionD3DPath_ReturnsFalse_ForResponsiveResolution()
	{
		var config = new StreamConfig { Width = 1920, Height = 1080, Fps = 60 };

		bool result = MainWindow.ShouldUseHighResolutionD3DPath(
			config,
			ReceiverVideoEngineSetting.Auto,
			qualityRenderModeEnabled: true,
			gpuQualityRequested: true);

		Assert.False(result);
	}
}
