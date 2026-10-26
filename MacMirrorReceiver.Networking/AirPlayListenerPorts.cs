namespace MacMirrorReceiver.Networking;

/// <summary>
/// TCP ports the AirPlay receiver listens on. The defaults are the ports senders expect;
/// <see cref="Ephemeral"/> lets tests bind whatever is free instead of competing with a
/// running iMirror or Bonjour on the developer machine.
/// </summary>
public sealed record AirPlayListenerPorts(int AirPlay, int Raop, int Data, int Event, int Timing)
{
	public static AirPlayListenerPorts Default { get; } = new AirPlayListenerPorts(7000, 5000, 7100, 7101, 7102);

	public static AirPlayListenerPorts Ephemeral { get; } = new AirPlayListenerPorts(0, 0, 0, 0, 0);
}
