using System.Text.Json.Serialization;

namespace MacMirrorReceiver.Protocol;

public sealed class AuthResult
{
	[JsonPropertyName("accepted")]
	public bool Accepted { get; init; }

	[JsonPropertyName("message")]
	public string Message { get; init; } = "";

}
