using System.Net;

namespace MacMirrorReceiver.Models;

public sealed class MirrorDevice
{
	public required string Name { get; init; }

	public required string Host { get; init; }

	public required IPAddress Address { get; init; }

	public required int Port { get; init; }

	public string EndpointKey => $"{Address}:{Port}";

	public string DisplayName => $"{Name} ({Address}:{Port})";
}
