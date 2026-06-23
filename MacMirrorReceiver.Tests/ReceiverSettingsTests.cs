using MacMirrorReceiver;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class ReceiverSettingsTests
{
	[Theory]
	[InlineData(" Living Room ", "Living Room")]
	[InlineData("", ReceiverSettings.DefaultReceiverName)]
	[InlineData("   ", ReceiverSettings.DefaultReceiverName)]
	[InlineData(null, ReceiverSettings.DefaultReceiverName)]
	public void NormalizeReceiverName_TrimsAndDefaultsEmptyValues(string? input, string expected)
	{
		Assert.Equal(expected, ReceiverSettings.NormalizeReceiverName(input));
	}

	[Fact]
	public void NormalizeReceiverName_TruncatesToMaxLength()
	{
		string input = new string('A', ReceiverSettings.MaxReceiverNameLength + 8);
		string normalized = ReceiverSettings.NormalizeReceiverName(input);

		Assert.Equal(ReceiverSettings.MaxReceiverNameLength, normalized.Length);
		Assert.Equal(new string('A', ReceiverSettings.MaxReceiverNameLength), normalized);
	}

	[Theory]
	[InlineData(ReceiverSettings.MinAudioSyncOffsetMs, ReceiverSettings.MinAudioSyncOffsetMs)]
	[InlineData(ReceiverSettings.MaxAudioSyncOffsetMs, ReceiverSettings.MaxAudioSyncOffsetMs)]
	[InlineData(ReceiverSettings.MinAudioSyncOffsetMs - 1, ReceiverSettings.MinAudioSyncOffsetMs)]
	[InlineData(ReceiverSettings.MaxAudioSyncOffsetMs + 1, ReceiverSettings.MaxAudioSyncOffsetMs)]
	[InlineData(120, 120)]
	public void ClampAudioOffset_ClampsToSupportedRange(int input, int expected)
	{
		Assert.Equal(expected, ReceiverSettings.ClampAudioOffset(input));
	}
}
