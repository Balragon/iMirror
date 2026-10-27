using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class AppVersionInfoTests
{
	[Theory]
	[InlineData("0.8.0+25aa28455baeee7dae720be9677971c036526ca6", "0.8.0 (25aa284)")]
	[InlineData("0.8.0+b53acc6", "0.8.0 (b53acc6)")]
	[InlineData("0.8.0+abc", "0.8.0 (abc)")]
	[InlineData("0.8.0+", "0.8.0")]
	[InlineData("0.8.0", "0.8.0")]
	[InlineData("0.9.0-beta.1+25aa28455baeee7d", "0.9.0-beta.1 (25aa284)")]
	[InlineData("unknown", "unknown")]
	public void ShortenVersion_CutsBuildMetadataToSevenCharacters(string informationalVersion, string expected)
	{
		Assert.Equal(expected, AppVersionInfo.ShortenVersion(informationalVersion));
	}
}
