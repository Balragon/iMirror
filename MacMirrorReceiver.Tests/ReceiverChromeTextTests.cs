using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class ReceiverChromeTextTests
{
	[Fact]
	public void IdleWithNoSenders()
	{
		var state = new ReceiverChromeState(Connected: false, Connecting: false, DeviceCount: 0, AudioFirewallWarning: false);
		Assert.Equal("Ready to receive", ReceiverChromeText.CardTitle(state));
		Assert.Equal("READY", ReceiverChromeText.BadgeText(state));
		Assert.Equal("Ready for AirPlay", ReceiverChromeText.Subtitle(state));
		Assert.Equal("Ready to mirror", ReceiverChromeText.EmptyStateTitle(state));
		Assert.Equal("Open Control Center, tap Screen Mirroring, then choose iMirror.", ReceiverChromeText.EmptyStateDetail(state));
		Assert.Equal(ReceiverStatusTone.Muted, ReceiverChromeText.Tone(state));
	}

	[Fact]
	public void IdleWithLegacySenders()
	{
		var state = new ReceiverChromeState(false, false, 2, false);
		Assert.Equal("LEGACY", ReceiverChromeText.BadgeText(state));
		Assert.Equal("Legacy sender available", ReceiverChromeText.Subtitle(state));
		Assert.Equal("Legacy sender found", ReceiverChromeText.EmptyStateTitle(state));
		Assert.Equal("Enter the sender PIN, then press Connect Sender.", ReceiverChromeText.EmptyStateDetail(state));
		Assert.Equal(ReceiverStatusTone.LegacySenderAvailable, ReceiverChromeText.Tone(state));
	}

	[Fact]
	public void Connecting()
	{
		var state = new ReceiverChromeState(false, true, 1, false);
		Assert.Equal("Connecting", ReceiverChromeText.CardTitle(state));
		Assert.Equal("LINKING", ReceiverChromeText.BadgeText(state));
		Assert.Equal("Connecting", ReceiverChromeText.Subtitle(state));
		Assert.Equal("Connecting", ReceiverChromeText.EmptyStateTitle(state));
		Assert.Equal(ReceiverStatusTone.Connecting, ReceiverChromeText.Tone(state));
	}

	[Fact]
	public void ConnectedBeatsConnectingAndSenders()
	{
		var state = new ReceiverChromeState(true, true, 3, false);
		Assert.Equal("Mirroring", ReceiverChromeText.CardTitle(state));
		Assert.Equal("LIVE", ReceiverChromeText.BadgeText(state));
		Assert.Equal("Mirroring", ReceiverChromeText.Subtitle(state));
		Assert.Equal("Preparing the stream", ReceiverChromeText.EmptyStateTitle(state));
		Assert.Equal("The mirrored screen will appear here as soon as video frames arrive.", ReceiverChromeText.EmptyStateDetail(state));
		Assert.Equal(ReceiverStatusTone.Live, ReceiverChromeText.Tone(state));
	}

	[Fact]
	public void FirewallWarningOverridesSubtitleAndToneOnly()
	{
		var state = new ReceiverChromeState(true, false, 0, true);
		Assert.Equal("Audio blocked by Windows Firewall", ReceiverChromeText.Subtitle(state));
		Assert.Equal(ReceiverStatusTone.Warning, ReceiverChromeText.Tone(state));
		Assert.Equal("Mirroring", ReceiverChromeText.CardTitle(state));
		Assert.Equal("LIVE", ReceiverChromeText.BadgeText(state));
	}

	[Theory]
	[InlineData(0, "No legacy sender found yet. iPhone mirroring does not need this field.")]
	[InlineData(1, "1 legacy sender is ready. Enter its PIN to use the old path.")]
	[InlineData(4, "4 senders are available. Choose one, then enter its PIN.")]
	public void DeviceSummaryCountsSenders(int count, string expected)
	{
		Assert.Equal(expected, ReceiverChromeText.DeviceSummary(count));
	}
}
