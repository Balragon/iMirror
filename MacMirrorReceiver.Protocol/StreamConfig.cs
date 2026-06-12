using System.Text.Json.Serialization;

namespace MacMirrorReceiver.Protocol;

public sealed class StreamConfig
{
	[JsonPropertyName("width")]
	public int Width { get; init; }

	[JsonPropertyName("height")]
	public int Height { get; init; }

	[JsonPropertyName("fps")]
	public int Fps { get; init; }

	[JsonPropertyName("codec")]
	public string Codec { get; init; } = "h264-annexb";

}
