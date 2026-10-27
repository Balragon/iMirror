using System;
using MacMirrorReceiver.Networking;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class AirPlayPlayFairTablesTests
{
	[Fact]
	public void TablesLoadFromTheEmbeddedSources()
	{
		// Parses omg_hax.h / omg_hax.c / sap_hash.c from the assembly's embedded resources.
		// Fails if a resource is missing from the build or a table name/shape no longer matches.
		AirPlayPlayFair.EnsureTablesLoaded();
	}
}
