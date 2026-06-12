namespace MacMirrorReceiver.Protocol;

public readonly record struct MirrorHeader(MirrorMessageType Type, ulong Timestamp, int PayloadLength);
