using MonitorApi.Models.Admin;

namespace MonitorApi.Admin.Tests;

/// <summary>
/// Pins the slug used in <c>publisher_id</c>. It had no test while it was private to
/// <see cref="FeedMetadata"/>; now that two monitors' identity fields share it, a change here would
/// silently split one publisher into two on the dashboard, so the exact output is fixed.
/// </summary>
public class AdminSlugTests
{
	[Theory]
	[InlineData("Freedom Leisure", "freedom-leisure")]
	[InlineData("Actihire", "actihire")]
	// Punctuation and runs of whitespace collapse to one hyphen, never several.
	[InlineData("St. Albans  City & District Council", "st-albans-city-district-council")]
	[InlineData("O'Neill's 24/7", "o-neill-s-24-7")]
	// Leading and trailing separators never produce an edge hyphen.
	[InlineData("  padded  ", "padded")]
	[InlineData("-already-hyphenated-", "already-hyphenated")]
	[InlineData("MiXeD CaSe", "mixed-case")]
	public void Of_KeepsAlphanumericsAndCollapsesEverythingElseToSingleHyphens(string value, string expected) =>
		Assert.Equal(expected, AdminSlug.Of(value));

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("!!!")]
	[InlineData("- / -")]
	public void Of_FallsBackToUnknownWhenThereIsNothingToSlugify(string? value)
	{
		Assert.Equal("unknown", AdminSlug.Of(value));
		Assert.Equal(AdminSlug.Unknown, AdminSlug.Of(value));
	}

	[Fact]
	public void FeedPublisherId_IsThePrefixedSlug()
	{
		// The value two live monitors already serve; extracting the slug must not have changed it.
		var feed = new FeedMetadata("f1", null, null, "Freedom Leisure", null);

		Assert.Equal("pub_freedom-leisure", feed.PublisherId);
	}

	[Fact]
	public void FeedPublisherId_IsPubUnknownForAnUnnamedPublisher()
	{
		var feed = new FeedMetadata("f1", null, null, null, null);

		Assert.Equal("pub_unknown", feed.PublisherId);
	}
}
