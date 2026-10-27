using System.Net;

namespace MacMirrorReceiver.Networking;

/// <summary>
/// Compares peer addresses for the "same host that performed SETUP" checks on the data paths.
/// </summary>
internal static class PeerAddress
{
	/// <summary>
	/// True when <paramref name="actual"/> is the same host as <paramref name="expected"/>.
	/// A missing expected address never matches: a data connection or RTP packet that arrives
	/// before any SETUP has named a sender is not accepted.
	/// </summary>
	public static bool Matches(IPAddress? expected, IPAddress? actual)
	{
		if (expected == null || actual == null)
		{
			return false;
		}

		return Normalize(expected).Equals(Normalize(actual));
	}

	private static IPAddress Normalize(IPAddress address)
	{
		return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
	}
}
