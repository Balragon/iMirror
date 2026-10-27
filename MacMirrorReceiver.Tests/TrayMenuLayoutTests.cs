using System.Linq;
using System.Windows.Media;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class TrayMenuLayoutTests
{
	// A 1920x1080 monitor with a 48px taskbar at the bottom.
	private const int WorkLeft = 0;
	private const int WorkTop = 0;
	private const int WorkRight = 1920;
	private const int WorkBottom = 1032;

	[Fact]
	public void Place_OpensUpAndLeftOfACursorOnABottomTaskbar()
	{
		(int x, int y) = TrayMenuPlacement.Place(1700, 1056, 240, 200, WorkLeft, WorkTop, WorkRight, WorkBottom);

		Assert.Equal(1460, x);
		Assert.Equal(832, y);
	}

	[Fact]
	public void Place_FlipsRightWhenTheMenuWouldLeaveTheLeftEdge()
	{
		(int x, _) = TrayMenuPlacement.Place(100, 1056, 240, 200, WorkLeft, WorkTop, WorkRight, WorkBottom);

		Assert.Equal(100, x);
	}

	[Fact]
	public void Place_OpensBelowAndInsideTheWorkAreaForATopTaskbar()
	{
		// Taskbar on top: the work area starts at y=48 and the cursor is above it.
		(int x, int y) = TrayMenuPlacement.Place(1700, 24, 240, 200, 0, 48, 1920, 1080);

		Assert.Equal(1460, x);
		Assert.Equal(48, y);
	}

	[Fact]
	public void Place_StaysInsideTheWorkAreaForALeftTaskbar()
	{
		// Taskbar on the left: the work area starts at x=48 and the cursor is left of it.
		(int x, int y) = TrayMenuPlacement.Place(24, 900, 240, 200, 48, 0, 1920, 1080);

		Assert.Equal(48, x);
		Assert.Equal(700, y);
	}

	[Fact]
	public void Place_ClampsAMenuNearTheRightEdgeOfASecondMonitor()
	{
		// Second monitor to the right of the first, cursor past its right edge.
		(int x, int y) = TrayMenuPlacement.Place(3900, 500, 240, 200, 1920, 0, 3840, 1032);

		Assert.Equal(3600, x);
		Assert.Equal(300, y);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Palette_TextIsOpaqueEnoughAndFillsAreSubtle(bool lightTheme)
	{
		TrayMenuPalette palette = TrayMenuPalette.For(lightTheme);

		Assert.True(palette.Foreground.A >= 0xE0);
		Assert.True(palette.MutedForeground.A >= 0x90);
		Assert.True(palette.HoverFill.A is > 0 and < 0x20);
		Assert.True(palette.PressedFill.A < palette.HoverFill.A);
		Assert.Equal(0xFF, palette.SolidBackground.A);
	}

	[Fact]
	public void Palette_LightAndDarkUseOppositeTextColours()
	{
		Assert.Equal(Colors.White.R, TrayMenuPalette.Dark.Foreground.R);
		Assert.Equal(0, TrayMenuPalette.Light.Foreground.R);
		Assert.True(TrayMenuPalette.Light.SolidBackground.R > TrayMenuPalette.Dark.SolidBackground.R);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void ToneColor_DistinguishesLiveWarningLegacyAndIdle(bool lightTheme)
	{
		Color[] colors = new[]
		{
			TrayMenuPalette.ToneColor(ReceiverStatusTone.Live, lightTheme),
			TrayMenuPalette.ToneColor(ReceiverStatusTone.Warning, lightTheme),
			TrayMenuPalette.ToneColor(ReceiverStatusTone.LegacySenderAvailable, lightTheme),
			TrayMenuPalette.ToneColor(ReceiverStatusTone.Muted, lightTheme)
		};

		Assert.Equal(colors.Length, colors.Distinct().Count());
		Assert.Equal(
			TrayMenuPalette.ToneColor(ReceiverStatusTone.Warning, lightTheme),
			TrayMenuPalette.ToneColor(ReceiverStatusTone.Connecting, lightTheme));
	}

	[Fact]
	public void ToneColor_IsDarkerOnLightBackgrounds()
	{
		Color dark = TrayMenuPalette.ToneColor(ReceiverStatusTone.Live, lightTheme: false);
		Color light = TrayMenuPalette.ToneColor(ReceiverStatusTone.Live, lightTheme: true);

		Assert.True(light.R + light.G + light.B < dark.R + dark.G + dark.B);
	}
}
