using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using MacMirrorReceiver.Networking;
using MacMirrorReceiver.Video;

namespace MacMirrorReceiver;

internal enum PreflightStatus { Ok, Warning, Blocked }

internal sealed record PreflightCheck(
	string Id,
	string Title,
	PreflightStatus Status,
	string Message,
	string? Detail = null);

internal sealed record PreflightReport(
	IReadOnlyList<PreflightCheck> Checks,
	PreflightStatus Worst);

internal sealed record StartupListenerState(
	bool IsMdnsBound,
	bool IsAirPlayListenerBound,
	bool IsRaopListenerBound,
	int AirPlayListenerPort,
	int RaopListenerPort,
	string? AirPlayListenerBindError,
	string? RaopListenerBindError)
{
	public static StartupListenerState FromProbe(AirPlayProbeService probe)
	{
		return new StartupListenerState(
			probe.IsMdnsBound,
			probe.IsAirPlayListenerBound,
			probe.IsRaopListenerBound,
			probe.AirPlayListenerPort,
			probe.RaopListenerPort,
			probe.AirPlayListenerBindError,
			probe.RaopListenerBindError);
	}
}

internal static class StartupDiagnostics
{
	public static async Task<PreflightReport> RunAsync(AirPlayProbeService probe)
	{
		Task<PreflightCheck> ffmpegTask = Task.Run(CheckFfmpeg);
		Task<PreflightCheck> networkTask = Task.Run(CheckNetwork);
		PreflightCheck listeners = CheckListeners(StartupListenerState.FromProbe(probe));

		PreflightCheck ffmpeg = await ffmpegTask;
		PreflightCheck network = await networkTask;

		var checks = new[] { ffmpeg, listeners, network };
		PreflightStatus worst = PreflightStatus.Ok;
		foreach (PreflightCheck check in checks)
		{
			if (check.Status > worst)
			{
				worst = check.Status;
			}
		}

		return new PreflightReport(checks, worst);
	}

	private static PreflightCheck CheckFfmpeg()
	{
		string? path = FfmpegDecoder.FindFfmpeg();
		if (path != null)
		{
			return new PreflightCheck("ffmpeg", "FFmpeg", PreflightStatus.Ok, "Found.", path);
		}

		return new PreflightCheck(
			"ffmpeg",
			"FFmpeg",
			PreflightStatus.Blocked,
			"FFmpeg not found. Audio and software video are disabled.",
			"Place ffmpeg.exe at tools\\ffmpeg\\bin\\ffmpeg.exe or add it to PATH.");
	}

	internal static PreflightCheck CheckListeners(StartupListenerState listeners)
	{
		bool mdnsBound = listeners.IsMdnsBound;
		bool airPlayBound = listeners.IsAirPlayListenerBound;
		bool raopBound = listeners.IsRaopListenerBound;

		if (!mdnsBound || !airPlayBound)
		{
			var missing = new List<string>();
			if (!mdnsBound)
			{
				missing.Add("mDNS UDP 5353");
			}
			if (!airPlayBound)
			{
				missing.Add($"AirPlay TCP {listeners.AirPlayListenerPort}");
			}

			if (!airPlayBound && listeners.AirPlayListenerBindError != null)
			{
				return new PreflightCheck(
					"listeners",
					"Firewall / discovery",
					PreflightStatus.Blocked,
					$"Port {listeners.AirPlayListenerPort} is unavailable. iMirror is not discoverable.",
					$"AirPlay TCP {listeners.AirPlayListenerPort} could not bind: {listeners.AirPlayListenerBindError}. " +
					"Close other AirPlay receivers, then press Re-check.");
			}

			return new PreflightCheck(
				"listeners",
				"Firewall / discovery",
				PreflightStatus.Blocked,
				"Windows Firewall is blocking AirPlay discovery. Allow iMirror through Windows Firewall.",
				$"Blocked: {string.Join(", ", missing)}. The receiver cannot appear in Screen Mirroring until Windows Firewall allows iMirror on both Private and Public networks. " +
				"Use Allow in Firewall, or open Windows Security > Firewall & network protection > Allow an app through firewall. " +
				"Audio uses dynamic UDP ports, so allow the iMirror app itself rather than only fixed ports.");
		}

		if (!raopBound)
		{
			return new PreflightCheck(
				"listeners",
				"Firewall / discovery",
				PreflightStatus.Warning,
				$"Legacy audio port unavailable (TCP {listeners.RaopListenerPort}). Mirroring still works.",
				listeners.RaopListenerBindError != null
					? $"RAOP TCP {listeners.RaopListenerPort} could not bind: {listeners.RaopListenerBindError}. Close other receivers, then press Re-check."
					: null);
		}

		return new PreflightCheck(
			"listeners",
			"Firewall / discovery",
			PreflightStatus.Ok,
			"All listeners bound.",
			null);
	}

	private static PreflightCheck CheckNetwork()
	{
		string? localIp = null;
		bool hasUsable = false;
		bool hasVirtualOnly = true;

		foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
		{
			if (nic.OperationalStatus != OperationalStatus.Up)
			{
				continue;
			}
			if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
			{
				continue;
			}
			if (!nic.SupportsMulticast)
			{
				continue;
			}

			foreach (UnicastIPAddressInformation addr in nic.GetIPProperties().UnicastAddresses)
			{
				if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
				{
					continue;
				}

				string addrStr = addr.Address.ToString();
				if (addrStr.StartsWith("169.254"))
				{
					continue;
				}

				bool isVirtual =
					nic.Description.Contains("Virtual", System.StringComparison.OrdinalIgnoreCase) ||
					nic.Description.Contains("VPN", System.StringComparison.OrdinalIgnoreCase) ||
					nic.Description.Contains("Tunnel", System.StringComparison.OrdinalIgnoreCase) ||
					nic.Name.Contains("tun", System.StringComparison.OrdinalIgnoreCase) ||
					nic.Name.Contains("tap", System.StringComparison.OrdinalIgnoreCase);

				hasUsable = true;
				if (!isVirtual)
				{
					hasVirtualOnly = false;
					localIp ??= $"{addrStr} ({nic.Name})";
				}
				else
				{
					localIp ??= $"{addrStr} ({nic.Name} virtual)";
				}
			}
		}

		if (!hasUsable)
		{
			return new PreflightCheck(
				"network",
				"Network",
				PreflightStatus.Blocked,
				"No network connection. Connect to the same Wi-Fi as your Mac/iPhone.",
				null);
		}

		if (hasVirtualOnly)
		{
			return new PreflightCheck(
				"network",
				"Network",
				PreflightStatus.Warning,
				"Connected via VPN or virtual adapter. Senders on your Wi-Fi may not see iMirror.",
				localIp);
		}

		return new PreflightCheck(
			"network",
			"Network",
			PreflightStatus.Ok,
			"Network OK.",
			localIp);
	}
}
