using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

if (args.Length < 1)
{
	Console.Error.WriteLine("Usage: HighResolutionProbeReport <capture.submitted.h264> [max-access-units=600] [geometry=WxH[@fps]]");
	return 2;
}

string capturePath = Path.GetFullPath(args[0]);
if (!File.Exists(capturePath))
{
	Console.Error.WriteLine("H.264 capture not found: " + capturePath);
	return 2;
}

int maxAccessUnits = args.Length >= 2 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedMax)
	? parsedMax
	: 600;
string? geometryOverride = args.Length >= 3 ? args[2] : null;

string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
string mfProject = Path.Combine(repoRoot, "tools", "MediaFoundationH264Probe", "MediaFoundationH264Probe.csproj");
string sharedProject = Path.Combine(repoRoot, "tools", "D3DSharedHandleProbe", "D3DSharedHandleProbe.csproj");
string videoProcessorProject = Path.Combine(repoRoot, "tools", "D3DVideoProcessorProbe", "D3DVideoProcessorProbe.csproj");
string productReplayProject = Path.Combine(repoRoot, "tools", "HighResolutionD3DReplayProbe", "HighResolutionD3DReplayProbe.csproj");

Console.WriteLine("iMirror high-resolution probe report");
Console.WriteLine("capture=" + capturePath);
Console.WriteLine("maxAccessUnits=" + maxAccessUnits.ToString(CultureInfo.InvariantCulture));
if (!string.IsNullOrWhiteSpace(geometryOverride))
{
	Console.WriteLine("geometryOverride=" + geometryOverride);
}
Console.WriteLine();

CommandResult mf = string.IsNullOrWhiteSpace(geometryOverride)
	? RunDotnet(repoRoot, "run", "--project", mfProject, "-c", "Release", "--", capturePath, maxAccessUnits.ToString(CultureInfo.InvariantCulture))
	: RunDotnet(repoRoot, "run", "--project", mfProject, "-c", "Release", "--", capturePath, maxAccessUnits.ToString(CultureInfo.InvariantCulture), geometryOverride);
Console.WriteLine("== MediaFoundationH264Probe ==");
Console.Write(mf.Output);
if (!mf.Output.EndsWith(Environment.NewLine, StringComparison.Ordinal))
{
	Console.WriteLine();
}

ProbeSignals signals = ParseProbeSignals(mf.Output);
bool mfProcessPass = mf.ExitCode == 0;
bool cpuDecodePass = signals.CpuDecodedOutputs > 0;
bool d3d11DecodePass = signals.D3D11DecodedOutputs > 0;
bool d3d11TexturePass = signals.D3D11Textures > 0;
ProbeGeometry captureGeometry = ParseProbeGeometry(mf.Output);
int matchingD3D11TextureDescriptions = CountMatchingD3D11TextureDescriptions(mf.Output, captureGeometry);
bool d3d11TextureDescriptionPass = matchingD3D11TextureDescriptions > 0;
bool mfPass = mfProcessPass && cpuDecodePass && d3d11DecodePass && d3d11TexturePass && d3d11TextureDescriptionPass;
Console.WriteLine($"captureProbeGeometry={captureGeometry.Width}x{captureGeometry.Height}@{captureGeometry.Fps}, source={captureGeometry.Source}");

ProbeResult sharedCapture = RunSharedProbe(repoRoot, sharedProject, captureGeometry, "capture");
ProbeResult videoProcessorCapture = RunVideoProcessorProbe(repoRoot, videoProcessorProject, captureGeometry, "capture");
ProbeResult productReplayCapture = RunProductReplayProbe(repoRoot, productReplayProject, capturePath, maxAccessUnits, captureGeometry);
ProbeResult shared2048 = SameSize(captureGeometry, 2048, 1152) ? sharedCapture : RunSharedProbe(repoRoot, sharedProject, new ProbeGeometry(2048, 1152, 30, "reference"), "reference");
ProbeResult videoProcessor2048 = SameSize(captureGeometry, 2048, 1152) ? videoProcessorCapture : RunVideoProcessorProbe(repoRoot, videoProcessorProject, new ProbeGeometry(2048, 1152, 30, "reference"), "reference");
ProbeResult shared2560 = SameSize(captureGeometry, 2560, 1440) ? sharedCapture : RunSharedProbe(repoRoot, sharedProject, new ProbeGeometry(2560, 1440, 30, "reference"), "reference");
ProbeResult videoProcessor2560 = SameSize(captureGeometry, 2560, 1440) ? videoProcessorCapture : RunVideoProcessorProbe(repoRoot, videoProcessorProject, new ProbeGeometry(2560, 1440, 30, "reference"), "reference");
bool pass = mfPass && sharedCapture.Pass && videoProcessorCapture.Pass && productReplayCapture.Pass;

Console.WriteLine("== Summary ==");
Console.WriteLine((pass ? "PASS" : "FAIL") + ": high-resolution probe report");
Console.WriteLine($"mediaFoundationProcess={(mfProcessPass ? "pass" : "fail")} exitCode={mf.ExitCode}");
Console.WriteLine($"cpuDecodedOutputs={signals.CpuDecodedOutputs:N0} ({(cpuDecodePass ? "pass" : "fail")})");
Console.WriteLine($"d3d11DecodedOutputs={signals.D3D11DecodedOutputs:N0} ({(d3d11DecodePass ? "pass" : "fail")})");
Console.WriteLine($"d3d11Textures={signals.D3D11Textures:N0} ({(d3d11TexturePass ? "pass" : "fail")})");
Console.WriteLine($"d3d11TextureDescriptions={signals.D3D11TextureDescriptions:N0}, matchingNv12CaptureGeometry={matchingD3D11TextureDescriptions:N0} ({(d3d11TextureDescriptionPass ? "pass" : "fail")})");
Console.WriteLine($"sharedCapture={FormatProbeSummary(sharedCapture)}");
Console.WriteLine($"videoProcessorCapture={FormatProbeSummary(videoProcessorCapture)}");
Console.WriteLine($"productReplayCapture={FormatProbeSummary(productReplayCapture)}");
Console.WriteLine($"shared2048={FormatProbeSummary(shared2048)}");
Console.WriteLine($"videoProcessor2048={FormatProbeSummary(videoProcessor2048)}");
Console.WriteLine($"shared2560={FormatProbeSummary(shared2560)}");
Console.WriteLine($"videoProcessor2560={FormatProbeSummary(videoProcessor2560)}");
Console.WriteLine("Note: this report proves offline decode/surface/render-bridge capability and product-class replay only. Product readiness still requires the 30-minute real-device latency and reconnect acceptance gates.");

return pass ? 0 : 1;

static CommandResult RunDotnet(string workingDirectory, params string[] arguments)
{
	var startInfo = new ProcessStartInfo
	{
		FileName = "dotnet",
		WorkingDirectory = workingDirectory,
		UseShellExecute = false,
		RedirectStandardOutput = true,
		RedirectStandardError = true
	};
	foreach (string argument in arguments)
	{
		startInfo.ArgumentList.Add(argument);
	}

	using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start dotnet.");
	string stdout = process.StandardOutput.ReadToEnd();
	string stderr = process.StandardError.ReadToEnd();
	process.WaitForExit();
	return new CommandResult(process.ExitCode, stdout + stderr);
}

static ProbeResult RunSharedProbe(string repoRoot, string project, ProbeGeometry geometry, string label)
{
	Console.WriteLine($"== D3DSharedHandleProbe {label}: {geometry.Width}x{geometry.Height} ==");
	CommandResult result = RunDotnet(
		repoRoot,
		"run",
		"--project",
		project,
		"-c",
		"Release",
		"--",
		geometry.Width.ToString(CultureInfo.InvariantCulture),
		geometry.Height.ToString(CultureInfo.InvariantCulture));
	Console.Write(result.Output);
	if (!result.Output.EndsWith(Environment.NewLine, StringComparison.Ordinal))
	{
		Console.WriteLine();
	}
	bool pass = result.ExitCode == 0 && result.Output.Contains("result=d3d11-d3d9-d3dimage-shared-handle-opened", StringComparison.Ordinal);
	return new ProbeResult(geometry.Width, geometry.Height, result.ExitCode, pass);
}

static ProbeResult RunVideoProcessorProbe(string repoRoot, string project, ProbeGeometry geometry, string label)
{
	Console.WriteLine($"== D3DVideoProcessorProbe {label}: {geometry.Width}x{geometry.Height} ==");
	CommandResult result = RunDotnet(
		repoRoot,
		"run",
		"--project",
		project,
		"-c",
		"Release",
		"--",
		geometry.Width.ToString(CultureInfo.InvariantCulture),
		geometry.Height.ToString(CultureInfo.InvariantCulture));
	Console.Write(result.Output);
	if (!result.Output.EndsWith(Environment.NewLine, StringComparison.Ordinal))
	{
		Console.WriteLine();
	}
	bool pass = result.ExitCode == 0 && result.Output.Contains("result=nv12-to-bgra-video-processor-d3dimage-completed", StringComparison.Ordinal);
	return new ProbeResult(geometry.Width, geometry.Height, result.ExitCode, pass);
}

static ProbeResult RunProductReplayProbe(string repoRoot, string project, string capturePath, int maxAccessUnits, ProbeGeometry geometry)
{
	Console.WriteLine($"== HighResolutionD3DReplayProbe capture: {geometry.Width}x{geometry.Height}@{geometry.Fps} ==");
	CommandResult result = RunDotnet(
		repoRoot,
		"run",
		"--project",
		project,
		"-c",
		"Release",
		"--",
		capturePath,
		maxAccessUnits.ToString(CultureInfo.InvariantCulture),
		$"{geometry.Width.ToString(CultureInfo.InvariantCulture)}x{geometry.Height.ToString(CultureInfo.InvariantCulture)}@{geometry.Fps.ToString(CultureInfo.InvariantCulture)}");
	Console.Write(result.Output);
	if (!result.Output.EndsWith(Environment.NewLine, StringComparison.Ordinal))
	{
		Console.WriteLine();
	}
	bool pass = result.ExitCode == 0 && result.Output.Contains("PASS: product MF/D3D11 decode-to-D3DImage replay", StringComparison.Ordinal);
	return new ProbeResult(geometry.Width, geometry.Height, result.ExitCode, pass);
}

static string FormatProbeSummary(ProbeResult result)
{
	return $"{(result.Pass ? "pass" : "fail")} {result.Width}x{result.Height} exitCode={result.ExitCode}";
}

static bool SameSize(ProbeGeometry geometry, int width, int height)
{
	return geometry.Width == width && geometry.Height == height;
}

static ProbeSignals ParseProbeSignals(string output)
{
	var cpuPattern = new Regex(@"^decodeProbe: .*decodedOutputs=(?<decoded>[0-9,]+).*d3d11Textures=(?<textures>[0-9,]+)", RegexOptions.Multiline);
	var d3dPattern = new Regex(@"^D3D11Probe\.decodeProbe: .*decodedOutputs=(?<decoded>[0-9,]+).*d3d11Textures=(?<textures>[0-9,]+)", RegexOptions.Multiline);
	Match cpu = cpuPattern.Match(output);
	Match d3d = d3dPattern.Match(output);
	int textureDescriptions = Regex.Matches(output, "^Decoded D3D11 texture:", RegexOptions.Multiline).Count;
	return new ProbeSignals(
		ParseCount(cpu, "decoded"),
		ParseCount(d3d, "decoded"),
		ParseCount(d3d, "textures"),
		textureDescriptions);
}

static ProbeGeometry ParseProbeGeometry(string output)
{
	var pattern = new Regex(@"^probeGeometry=(?<width>[0-9]+)x(?<height>[0-9]+)@(?<fps>[0-9]+), source=(?<source>\w+)", RegexOptions.Multiline);
	Match match = pattern.Match(output);
	if (!match.Success)
	{
		return new ProbeGeometry(2048, 1152, 30, "missing");
	}
	return new ProbeGeometry(
		ParseRequiredInt(match, "width", 2048),
		ParseRequiredInt(match, "height", 1152),
		ParseRequiredInt(match, "fps", 30),
		match.Groups["source"].Value);
}

static int CountMatchingD3D11TextureDescriptions(string output, ProbeGeometry geometry)
{
	var pattern = new Regex(@"^Decoded D3D11 texture: format=NV12\([0-9]+\) size=(?<width>[0-9]+)x(?<height>[0-9]+)", RegexOptions.Multiline);
	int count = 0;
	foreach (Match match in pattern.Matches(output))
	{
		if (ParseRequiredInt(match, "width", 0) == geometry.Width &&
			ParseRequiredInt(match, "height", 0) == geometry.Height)
		{
			count++;
		}
	}
	return count;
}

static int ParseRequiredInt(Match match, string groupName, int fallback)
{
	return int.TryParse(match.Groups[groupName].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
		? value
		: fallback;
}

static int ParseCount(Match match, string groupName)
{
	if (!match.Success)
	{
		return 0;
	}

	string text = match.Groups[groupName].Value.Replace(",", string.Empty, StringComparison.Ordinal);
	return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;
}

static string FindRepoRoot(string startDirectory)
{
	DirectoryInfo? directory = new DirectoryInfo(startDirectory);
	while (directory != null)
	{
		if (File.Exists(Path.Combine(directory.FullName, "MacMirrorReceiver.csproj")))
		{
			return directory.FullName;
		}
		directory = directory.Parent;
	}

	string current = Directory.GetCurrentDirectory();
	if (File.Exists(Path.Combine(current, "MacMirrorReceiver.csproj")))
	{
		return current;
	}

	throw new InvalidOperationException("Could not locate iMirror repository root.");
}

readonly record struct CommandResult(int ExitCode, string Output);

readonly record struct ProbeSignals(int CpuDecodedOutputs, int D3D11DecodedOutputs, int D3D11Textures, int D3D11TextureDescriptions);

readonly record struct ProbeGeometry(int Width, int Height, int Fps, string Source);

readonly record struct ProbeResult(int Width, int Height, int ExitCode, bool Pass);
