using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.Orphans;

/// <summary>
/// Deterministic tests for the identifier-format check. No BigQuery credentials needed.
/// </summary>
public class IdPatternCheckTests
{
    #region Pattern

    [Fact]
    public void Pattern_ReplacesDigitRuns()
    {
        Assert.Equal(
            "https://example.com/session-series/{n}",
            IdPatternCheck.Pattern("https://example.com/session-series/12345"));
    }

    [Fact]
    public void Pattern_ReplacesUuidsWhole()
    {
        Assert.Equal(
            "https://example.com/series/{uuid}",
            IdPatternCheck.Pattern("https://example.com/series/3f2a9c1e-4b7d-4e2a-9f10-0c8d2b6e7a11"));
    }

    [Fact]
    public void Pattern_IsCaseInsensitive()
    {
        Assert.Equal(
            IdPatternCheck.Pattern("https://example.com/series/3F2A9C1E-4B7D-4E2A-9F10-0C8D2B6E7A11"),
            IdPatternCheck.Pattern("HTTPS://Example.com/Series/3f2a9c1e-4b7d-4e2a-9f10-0c8d2b6e7a11"));
    }

    [Fact]
    public void Pattern_TrimsWhitespace()
    {
        Assert.Equal(
            IdPatternCheck.Pattern("https://example.com/series/1"),
            IdPatternCheck.Pattern("  https://example.com/series/1  "));
    }

    #endregion

    #region Compare

    [Fact]
    public void SameShapeDifferentValues_IsNotAMismatch()
    {
        // An ordinary deleted parent: the id scheme is still the one the dataset uses.
        var result = IdPatternCheck.Compare(
            ["https://example.com/session-series/999"],
            ["https://example.com/session-series/1", "https://example.com/session-series/2"]);

        Assert.Null(result);
    }

    [Fact]
    public void NumericToUuid_IsAMismatch()
    {
        var result = IdPatternCheck.Compare(
            ["https://example.com/session-series/999"],
            ["https://example.com/session-series/3f2a9c1e-4b7d-4e2a-9f10-0c8d2b6e7a11"]);

        Assert.NotNull(result);
        Assert.Equal(["https://example.com/session-series/{n}"], result.ReferencedPatterns);
        Assert.Equal(["https://example.com/session-series/{uuid}"], result.ParentPatterns);
        Assert.Equal(["https://example.com/session-series/999"], result.ExampleReferencedIds);
    }

    [Fact]
    public void NewHostname_IsAMismatch()
    {
        var result = IdPatternCheck.Compare(
            ["https://old.example.com/series/1"],
            ["https://new.example.com/series/1"]);

        Assert.NotNull(result);
    }

    [Fact]
    public void AnyOverlap_IsNotAMismatch()
    {
        // One referenced shape is still in use, so the format has not changed wholesale.
        var result = IdPatternCheck.Compare(
            ["https://old.example.com/series/1", "https://example.com/series/2"],
            ["https://example.com/series/3"]);

        Assert.Null(result);
    }

    [Fact]
    public void NoReferencedIds_IsNotAMismatch()
    {
        Assert.Null(IdPatternCheck.Compare([], ["https://example.com/series/1"]));
    }

    [Fact]
    public void NoParentIds_IsNotAMismatch()
    {
        Assert.Null(IdPatternCheck.Compare(["https://example.com/series/1"], []));
    }

    [Fact]
    public void BlankIds_AreIgnored()
    {
        Assert.Null(IdPatternCheck.Compare(["", "   "], ["https://example.com/series/1"]));
    }

    [Fact]
    public void Patterns_AreDistinctAndSorted()
    {
        var result = IdPatternCheck.Compare(
            ["https://b.example.com/x/1", "https://a.example.com/x/2", "https://b.example.com/x/3"],
            ["https://c.example.com/x/1"]);

        Assert.NotNull(result);
        Assert.Equal(
            ["https://a.example.com/x/{n}", "https://b.example.com/x/{n}"],
            result.ReferencedPatterns);
    }

    #endregion


	#region Parent kinds

	[Theory]
	[InlineData("Slot", "FacilityUse")]
	[InlineData("ScheduledSession", "SessionSeries")]
	[InlineData("scheduledsession", "SessionSeries")]
	public void ParentKindFor_MapsEachMonitoredChildKind(string childKind, string expected)
	{
		Assert.Equal(expected, IdPatternCheck.ParentKindFor(childKind));
	}

	[Fact]
	public void ParentKindFor_IsNullForAKindThatIsNotAChild()
	{
		// A parent kind is not itself a monitored child, so it maps to nothing.
		Assert.Null(IdPatternCheck.ParentKindFor("FacilityUse"));
	}

	#endregion
}