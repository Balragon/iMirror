using System.Text.Json.Serialization;

namespace MacMirrorReceiver.Protocol;

public sealed class CursorState
{
	[JsonPropertyName("x")]
	public double X { get; init; }

	[JsonPropertyName("y")]
	public double Y { get; init; }

	[JsonPropertyName("visible")]
	public bool Visible { get; init; }
}
