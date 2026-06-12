using System.Text.Json.Serialization;

namespace MacMirrorReceiver.Protocol;

public sealed class AuthRequest
{
	[JsonPropertyName("pin")]
	public required string Pin { get; init; }

	[JsonPropertyName("client")]
	public string Client { get; init; } = "iMirror";

}
