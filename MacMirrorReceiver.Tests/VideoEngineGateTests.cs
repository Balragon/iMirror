using System;
using System.IO;
using MacMirrorReceiver;
using MacMirrorReceiver.Protocol;
using MacMirrorReceiver.Video;
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
	public void ShouldUseHighResolutionD3DPath_ReturnsTrue_ForPortraitStreamAboveResponsiveHeight()
	{
		var config = new StreamConfig { Width = 666, Height = 1440, Fps = 60 };

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

	[Fact]
	public void IsCompatibleNv12OutputSize_AcceptsExactPortraitSize()
	{
		bool result = VorticeMediaFoundationD3D11Decoder.IsCompatibleNv12OutputSize(
			expectedWidth: 666,
			expectedHeight: 1440,
			actualWidth: 666,
			actualHeight: 1440);

		Assert.True(result);
	}

	[Fact]
	public void IsCompatibleNv12OutputSize_AcceptsMacroblockPaddedPortraitWidth()
	{
		bool result = VorticeMediaFoundationD3D11Decoder.IsCompatibleNv12OutputSize(
			expectedWidth: 666,
			expectedHeight: 1440,
			actualWidth: 672,
			actualHeight: 1440);

		Assert.True(result);
	}

	[Fact]
	public void IsCompatibleNv12OutputSize_RejectsUnexpectedGeometry()
	{
		bool result = VorticeMediaFoundationD3D11Decoder.IsCompatibleNv12OutputSize(
			expectedWidth: 666,
			expectedHeight: 1440,
			actualWidth: 704,
			actualHeight: 1440);

		Assert.False(result);
	}

	[Fact]
	public void ResolveDecoderThreadCount_UsesTwoThreads_ForSmallPhoneFallback()
	{
		int result = FfmpegDecoder.ResolveDecoderThreadCount(
			outputWidth: 500,
			outputHeight: 1080,
			targetOutputFps: 60);

		Assert.Equal(2, result);
	}

	[Fact]
	public void ResolveDecoderThreadCount_UsesFourThreads_For1080pFallback()
	{
		int result = FfmpegDecoder.ResolveDecoderThreadCount(
			outputWidth: 1920,
			outputHeight: 1080,
			targetOutputFps: 60);

		Assert.Equal(4, result);
	}

	[Fact]
	public void FindFfmpegInAncestorTools_FindsRepoToolFromRidBuildOutput()
	{
		string root = Path.Combine(Path.GetTempPath(), "imirror-ffmpeg-test-" + Guid.NewGuid().ToString("N"));
		try
		{
			string startDirectory = Path.Combine(root, "bin", "x64", "Release", "net10.0-windows", "win-x64");
			string ffmpegPath = Path.Combine(root, "tools", "ffmpeg", "bin", "ffmpeg.exe");
			Directory.CreateDirectory(startDirectory);
			Directory.CreateDirectory(Path.GetDirectoryName(ffmpegPath)!);
			File.WriteAllBytes(ffmpegPath, Array.Empty<byte>());

			string? result = FfmpegDecoder.FindFfmpegInAncestorTools(startDirectory);

			Assert.Equal(ffmpegPath, result);
		}
		finally
		{
			if (Directory.Exists(root))
			{
				Directory.Delete(root, recursive: true);
			}
		}
	}
}
