using MacMirrorReceiver;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class AppIconThemeTests
{
	[Theory]
	[InlineData(1, true)]
	[InlineData(0, false)]
	[InlineData(2, true)]
	public void ParseSystemUsesLightTheme_ReadsDwordValue(int value, bool expected)
	{
		Assert.Equal(expected, AppIconTheme.ParseSystemUsesLightTheme(value));
	}

	[Fact]
	public void ParseSystemUsesLightTheme_DefaultsToDarkWhenValueIsMissingOrOdd()
	{
		Assert.False(AppIconTheme.ParseSystemUsesLightTheme(null));
		Assert.False(AppIconTheme.ParseSystemUsesLightTheme("1"));
		Assert.False(AppIconTheme.ParseSystemUsesLightTheme(new byte[] { 1 }));
	}

	[Fact]
	public void IconUriFor_MapsThemeToPackedResource()
	{
		Assert.Equal(AppIconTheme.DarkIconUri, AppIconTheme.IconUriFor(lightTheme: false));
		Assert.Equal(AppIconTheme.LightIconUri, AppIconTheme.IconUriFor(lightTheme: true));
		Assert.EndsWith("/Assets/iMirror-light.ico", AppIconTheme.LightIconUri.AbsoluteUri);
		Assert.EndsWith("/Assets/iMirror.ico", AppIconTheme.DarkIconUri.AbsoluteUri);
	}
}
