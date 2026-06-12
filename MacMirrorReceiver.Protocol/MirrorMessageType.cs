namespace MacMirrorReceiver.Protocol;

public enum MirrorMessageType : ushort
{
	Auth = 1,
	AuthResult = 2,
	StreamConfig = 3,
	Video = 10,
	Error = 11,
	Cursor = 20
}
