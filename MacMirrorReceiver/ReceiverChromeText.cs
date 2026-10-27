namespace MacMirrorReceiver;

/// <summary>The inputs that decide what the receiver card, header and empty state say.</summary>
internal readonly record struct ReceiverChromeState(bool Connected, bool Connecting, int DeviceCount, bool AudioFirewallWarning);

/// <summary>Which status colour the chrome should use; MainWindow maps this to brushes.</summary>
internal enum ReceiverStatusTone
{
	Muted,
	LegacySenderAvailable,
	Connecting,
	Live,
	Warning
}

/// <summary>
/// Pure text and tone resolution for the main window chrome, kept free of WPF so it can be
/// snapshot-tested. The precedence is: firewall warning, connected, connecting, legacy senders
/// present, idle.
/// </summary>
internal static class ReceiverChromeText
{
	/// <summary>Shortens a status line for the compact control bar (46 characters at most).</summary>
	public static string CompactStatus(string message)
	{
		if (message.Length > 46)
		{
			return message.Substring(0, 43) + "...";
		}
		return message;
	}

	public static string CardTitle(ReceiverChromeState state)
	{
		if (state.Connected)
		{
			return "Mirroring";
		}
		if (state.Connecting)
		{
			return "Connecting";
		}
		return "Ready to receive";
	}

	public static string BadgeText(ReceiverChromeState state)
	{
		if (state.Connected)
		{
			return "LIVE";
		}
		if (state.Connecting)
		{
			return "LINKING";
		}
		return state.DeviceCount > 0 ? "LEGACY" : "READY";
	}

	public static string Subtitle(ReceiverChromeState state)
	{
		if (state.AudioFirewallWarning)
		{
			return "Audio blocked by Windows Firewall";
		}
		if (state.Connected)
		{
			return "Mirroring";
		}
		if (state.Connecting)
		{
			return "Connecting";
		}
		return state.DeviceCount > 0 ? "Legacy sender available" : "Ready for AirPlay";
	}

	public static string DeviceSummary(int deviceCount)
	{
		return deviceCount switch
		{
			0 => "No legacy sender found yet. iPhone mirroring does not need this field.",
			1 => "1 legacy sender is ready. Enter its PIN to use the old path.",
			_ => $"{deviceCount} senders are available. Choose one, then enter its PIN."
		};
	}

	public static string EmptyStateTitle(ReceiverChromeState state)
	{
		if (state.Connected)
		{
			return "Preparing the stream";
		}
		if (state.Connecting)
		{
			return "Connecting";
		}
		return state.DeviceCount > 0 ? "Legacy sender found" : "Ready to mirror";
	}

	public static string EmptyStateDetail(ReceiverChromeState state)
	{
		if (state.Connected)
		{
			return "The mirrored screen will appear here as soon as video frames arrive.";
		}
		if (state.Connecting)
		{
			return "Keep both devices on the same network while the secure session starts.";
		}
		return state.DeviceCount > 0
			? "Enter the sender PIN, then press Connect Sender."
			: "Open Control Center, tap Screen Mirroring, then choose iMirror.";
	}

	public static ReceiverStatusTone Tone(ReceiverChromeState state)
	{
		if (state.AudioFirewallWarning)
		{
			return ReceiverStatusTone.Warning;
		}
		if (state.Connected)
		{
			return ReceiverStatusTone.Live;
		}
		if (state.Connecting)
		{
			return ReceiverStatusTone.Connecting;
		}
		return state.DeviceCount > 0 ? ReceiverStatusTone.LegacySenderAvailable : ReceiverStatusTone.Muted;
	}
}
