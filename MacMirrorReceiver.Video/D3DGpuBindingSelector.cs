#if HIGH_RESOLUTION_D3D
using System;

namespace MacMirrorReceiver.Video;

public enum D3DGpuBinding
{
	SharpDx,
	Vortice
}

public static class D3DGpuBindingSelector
{
	public const string EnvironmentVariable = "IMIRROR_GPU_BINDING";

	public static D3DGpuBinding Current
	{
		get
		{
			string? value = Environment.GetEnvironmentVariable(EnvironmentVariable);
			return string.Equals(value, "vortice", StringComparison.OrdinalIgnoreCase)
				? D3DGpuBinding.Vortice
				: D3DGpuBinding.SharpDx;
		}
	}

	public static string CurrentName => Current == D3DGpuBinding.Vortice ? "Vortice" : "SharpDX";
}
#endif
