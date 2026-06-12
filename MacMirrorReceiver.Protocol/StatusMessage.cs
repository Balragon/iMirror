using System.Text.Json.Serialization;

namespace MacMirrorReceiver.Protocol;

public sealed class StatusMessage
{
	[JsonPropertyName("message")]
	public string Message { get; init; } = "";

}
