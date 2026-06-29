#if HIGH_RESOLUTION_D3D
using System;
using D3D11 = Vortice.Direct3D11;

namespace MacMirrorReceiver.Video;

internal enum D3D11VideoProcessorColorSpaceMode
{
	Bt709Full,
	Bt709Studio,
	Default
}

internal static class D3D11VideoProcessorColorSpace
{
	private const string EnvironmentVariable = "IMIRROR_D3D_COLOR_SPACE";

	public static void Apply(
		D3D11.ID3D11VideoContext context,
		D3D11.ID3D11VideoProcessor processor,
		Action<string>? statusChanged)
	{
		D3D11VideoProcessorColorSpaceMode mode = ResolveMode();
		if (mode == D3D11VideoProcessorColorSpaceMode.Default)
		{
			statusChanged?.Invoke("D3D11 video processor color space: mode=default (driver defaults).");
			return;
		}

		try
		{
			D3D11.VideoProcessorColorSpace streamColorSpace = CreateStreamColorSpace(mode);
			D3D11.VideoProcessorColorSpace outputColorSpace = CreateOutputColorSpace();

			context.VideoProcessorSetStreamColorSpace(processor, 0, streamColorSpace);
			context.VideoProcessorSetOutputColorSpace(processor, outputColorSpace);

			D3D11.VideoProcessorColorSpace actualStream = context.VideoProcessorGetStreamColorSpace(processor, 0);
			D3D11.VideoProcessorColorSpace actualOutput = context.VideoProcessorGetOutputColorSpace(processor);
			statusChanged?.Invoke(
				$"D3D11 video processor color space: mode={FormatMode(mode)}, stream={Format(actualStream)}, output={Format(actualOutput)}.");
		}
		catch (Exception ex)
		{
			statusChanged?.Invoke($"D3D11 video processor color space could not be set: {ex.Message}");
		}
	}

	private static D3D11VideoProcessorColorSpaceMode ResolveMode()
	{
		string? value = Environment.GetEnvironmentVariable(EnvironmentVariable);
		if (string.IsNullOrWhiteSpace(value))
		{
			return D3D11VideoProcessorColorSpaceMode.Bt709Full;
		}

		return value.Trim().ToLowerInvariant() switch
		{
			"default" or "off" or "0" or "false" => D3D11VideoProcessorColorSpaceMode.Default,
			"studio" or "limited" or "bt709-studio" or "bt709-limited" or "16-235" => D3D11VideoProcessorColorSpaceMode.Bt709Studio,
			"full" or "bt709" or "bt709-full" or "0-255" => D3D11VideoProcessorColorSpaceMode.Bt709Full,
			_ => D3D11VideoProcessorColorSpaceMode.Bt709Full
		};
	}

	private static D3D11.VideoProcessorColorSpace CreateStreamColorSpace(D3D11VideoProcessorColorSpaceMode mode)
	{
		return new D3D11.VideoProcessorColorSpace
		{
			Usage = 0u,
			RGB_Range = 0u,
			YCbCr_Matrix = 1u,
			YCbCr_xvYCC = 0u,
			Nominal_Range = (uint)(mode == D3D11VideoProcessorColorSpaceMode.Bt709Studio
				? D3D11.VideoProcessorNominalRange.Range_16_235
				: D3D11.VideoProcessorNominalRange.Range_0_255),
			Reserved = 0u
		};
	}

	private static D3D11.VideoProcessorColorSpace CreateOutputColorSpace()
	{
		return new D3D11.VideoProcessorColorSpace
		{
			Usage = 0u,
			RGB_Range = 0u,
			YCbCr_Matrix = 1u,
			YCbCr_xvYCC = 0u,
			Nominal_Range = (uint)D3D11.VideoProcessorNominalRange.Range_0_255,
			Reserved = 0u
		};
	}

	private static string FormatMode(D3D11VideoProcessorColorSpaceMode mode)
	{
		return mode switch
		{
			D3D11VideoProcessorColorSpaceMode.Bt709Studio => "bt709-studio",
			D3D11VideoProcessorColorSpaceMode.Bt709Full => "bt709-full",
			_ => "default"
		};
	}

	private static string Format(D3D11.VideoProcessorColorSpace colorSpace)
	{
		return "usage=" + colorSpace.Usage
			+ ", rgbRange=" + colorSpace.RGB_Range
			+ ", ycbcrMatrix=" + colorSpace.YCbCr_Matrix
			+ ", nominalRange=" + colorSpace.Nominal_Range;
	}
}
#endif
