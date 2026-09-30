using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.Quality;

/// <summary>
/// Deterministic tests for which feeds the schema-drift endpoint reports and the summary arithmetic,
/// run against hand-written assessments. No BigQuery credentials and no fixture.
/// </summary>
public class CustomPropertiesSummariserTests
{
	/// <summary>
	/// An assessment with everything defaulted, so each test states only the fields it is about. The
	/// counts default to what <paramref name="properties"/> implies.
	/// </summary>
	private static FeedCustomProperties Feed(
		string feedId,
		string datasetUrl = "https://example.org/dataset",
		string? publisherName = "Example",
		long? numCustomProperties = -1,
		long? numUsages = -1,
		params CustomPropertyUsage[] properties) =>
		new(
			feedId,
			datasetUrl,
			DatasetName: null,
			publisherName,
			FeedUrl: null,
			FeedType: null,
			IsRegular: null,
			SampledItems: null,
			numCustomProperties == -1 ? properties.Select(p => p.Property).Distinct().Count() : numCustomProperties,
			numUsages == -1 ? properties.Length : numUsages,
			properties);

	private static CustomPropertyUsage Usage(string property, string entityType = "SessionSeries", double? presence = 100)
	{
		var colon = property.IndexOf(':');
		var ns = colon > 0 && !property.Contains("://") ? property[..colon] : null;
		return new CustomPropertyUsage(property, ns, entityType, presence);
	}

	#region Which feeds are reported

	[Fact]
	public void OnlyFeedsWithCustomPropertiesAreReported()
	{
		var feeds = new[]
		{
			Feed("a", properties: Usage("beta:x")),
			Feed("b"),
			Feed("c", numCustomProperties: null),
			Feed("d", properties: [Usage("beta:x"), Usage("beta:y")]),
		};

		Assert.Equal(["a", "d"], CustomPropertiesSummariser.Reported(feeds).Select(f => f.FeedId));
	}

	[Fact]
	public void ReportedKeepsTheOrderItWasGiven()
	{
		var feeds = new[]
		{
			Feed("z", properties: Usage("beta:x")),
			Feed("a", properties: Usage("beta:x")),
		};

		Assert.Equal(["z", "a"], CustomPropertiesSummariser.Reported(feeds).Select(f => f.FeedId));
	}

	[Fact]
	public void TheCountDecidesNotTheArray()
	{
		// The rule is on num_custom_properties, as the endpoint documents; a feed claiming zero is not
		// reported even if the array disagrees, and one claiming some is reported even with no entries.
		var feeds = new[]
		{
			Feed("claims-none", numCustomProperties: 0, properties: Usage("beta:x")),
			Feed("claims-some", numCustomProperties: 2),
		};

		Assert.Equal(["claims-some"], CustomPropertiesSummariser.Reported(feeds).Select(f => f.FeedId));
	}

	#endregion

	#region Counts

	[Fact]
	public void EmptyInput_IsAZeroedSummary()
	{
		var summary = CustomPropertiesSummariser.Summarise([]);

		Assert.Equal(0, summary.FeedsAssessed);
		Assert.Equal(0, summary.FeedsWithCustomProperties);
		Assert.Null(summary.FeedShare);
		Assert.Equal(0, summary.DistinctCustomProperties);
		Assert.Equal(0, summary.TotalCustomPropertyUsages);
		Assert.Empty(summary.NamespaceBreakdown);
		Assert.Empty(summary.EntityTypeBreakdown);
		Assert.Empty(summary.PropertyBreakdown);
	}

	[Fact]
	public void AssessedCountsIncludeFeedsThatAreNotReported()
	{
		var summary = CustomPropertiesSummariser.Summarise(
		[
			Feed("a", datasetUrl: "https://one.example/", properties: Usage("beta:x")),
			Feed("b", datasetUrl: "https://one.example/"),
			Feed("c", datasetUrl: "https://two.example/"),
			Feed("d", datasetUrl: "https://three.example/", numCustomProperties: null),
		]);

		Assert.Equal(4, summary.FeedsAssessed);
		Assert.Equal(3, summary.DatasetsAssessed);
		Assert.Equal(1, summary.FeedsWithCustomProperties);
		Assert.Equal(1, summary.DatasetsWithCustomProperties);
		Assert.Equal(0.25, summary.FeedShare);
	}

	[Fact]
	public void PublishersAreDistinctNamedPublishersOfReportedFeeds()
	{
		var summary = CustomPropertiesSummariser.Summarise(
		[
			Feed("a", datasetUrl: "https://one.example/", publisherName: "Acme", properties: Usage("beta:x")),
			Feed("b", datasetUrl: "https://two.example/", publisherName: "ACME", properties: Usage("beta:x")),
			Feed("c", datasetUrl: "https://three.example/", publisherName: null, properties: Usage("beta:x")),
			Feed("d", datasetUrl: "https://four.example/", publisherName: "Other"),
		]);

		// Case-insensitive, blanks dropped, and the unreported publisher left out.
		Assert.Equal(1, summary.PublishersWithCustomProperties);
		Assert.Equal(3, summary.DatasetsWithCustomProperties);
	}

	[Fact]
	public void DistinctPropertiesAreComparedExactly()
	{
		var summary = CustomPropertiesSummariser.Summarise(
		[
			Feed("a", properties: [Usage("beta:foo"), Usage("beta:foo", "Place")]),
			Feed("b", properties: [Usage("beta:foo"), Usage("beta:Foo")]),
		]);

		Assert.Equal(2, summary.DistinctCustomProperties);
	}

	[Fact]
	public void UsagesAreTheSumOfEachReportedFeedsCount()
	{
		var summary = CustomPropertiesSummariser.Summarise(
		[
			Feed("a", numUsages: 3, properties: Usage("beta:x")),
			Feed("b", numUsages: null, properties: Usage("beta:x")),
			Feed("c", numCustomProperties: 0, numUsages: 50),
			Feed("d", numUsages: 4, properties: Usage("beta:y")),
		]);

		Assert.Equal(7, summary.TotalCustomPropertyUsages);
	}

	[Fact]
	public void UnreportedFeedsContributeNothingToTheBreakdowns()
	{
		var summary = CustomPropertiesSummariser.Summarise(
		[
			Feed("a", properties: Usage("beta:x")),
			Feed("b", numCustomProperties: 0, properties: Usage("ext:ignored")),
		]);

		Assert.Equal(["beta:x"], summary.PropertyBreakdown.Select(p => p.Property));
		Assert.Equal(["beta"], summary.NamespaceBreakdown.Select(n => n.Namespace));
		Assert.Equal(1, summary.DistinctCustomProperties);
	}

	#endregion

	#region Breakdowns

	[Fact]
	public void PropertyBreakdown_CountsFeedsAndDatasetsOncePerProperty()
	{
		var summary = CustomPropertiesSummariser.Summarise(
		[
			Feed("a", datasetUrl: "https://one.example/", properties: [Usage("beta:x", "SessionSeries"), Usage("beta:x", "ScheduledSession")]),
			Feed("b", datasetUrl: "https://one.example/", properties: Usage("beta:x", "Place")),
			Feed("c", datasetUrl: "https://two.example/", properties: Usage("beta:y")),
		]);

		var x = Assert.Single(summary.PropertyBreakdown, p => p.Property == "beta:x");
		Assert.Equal(2, x.FeedCount);
		Assert.Equal(1, x.DatasetCount);
		Assert.Equal("beta", x.Namespace);
		Assert.Equal(["Place", "ScheduledSession", "SessionSeries"], x.EntityTypes);
	}

	[Fact]
	public void TheSameFeedIdUnderTwoDatasetsIsTwoFeeds()
	{
		// Seen in live data: one publisher serves identical feed ids from two dataset URLs, and each is
		// assessed separately.
		var summary = CustomPropertiesSummariser.Summarise(
		[
			Feed("shared", datasetUrl: "https://one.example/", properties: Usage("beta:x")),
			Feed("shared", datasetUrl: "https://two.example/", properties: Usage("beta:x")),
		]);

		var x = Assert.Single(summary.PropertyBreakdown);
		Assert.Equal(2, x.FeedCount);
		Assert.Equal(2, x.DatasetCount);
		Assert.Equal(2, Assert.Single(summary.NamespaceBreakdown).FeedCount);
		Assert.Equal(2, Assert.Single(summary.EntityTypeBreakdown).FeedCount);
		Assert.Equal(2, summary.FeedsWithCustomProperties);
	}

	[Fact]
	public void PropertyBreakdown_IsMostWidespreadFirstThenAlphabetical()
	{
		var summary = CustomPropertiesSummariser.Summarise(
		[
			Feed("a", properties: [Usage("beta:b"), Usage("beta:c"), Usage("beta:a")]),
			Feed("b", properties: Usage("beta:c")),
		]);

		Assert.Equal(["beta:c", "beta:a", "beta:b"], summary.PropertyBreakdown.Select(p => p.Property));
	}

	[Fact]
	public void NamespaceBreakdown_GroupsUnprefixedUnderNullAndOrdersItLastOnATie()
	{
		var summary = CustomPropertiesSummariser.Summarise(
		[
			Feed("a", properties: [Usage("beta:x"), Usage("beta:y"), Usage("plain"), Usage("ext:z")]),
			Feed("b", properties: [Usage("beta:x"), Usage("https://example.org/ns#thing")]),
		]);

		// beta and the null namespace tie on two feeds each; null goes after the named prefix.
		Assert.Equal(["beta", null, "ext"], summary.NamespaceBreakdown.Select(n => n.Namespace));

		var beta = summary.NamespaceBreakdown[0];
		Assert.Equal(2, beta.PropertyCount);
		Assert.Equal(2, beta.FeedCount);

		var none = summary.NamespaceBreakdown[1];
		Assert.Equal(2, none.PropertyCount);
		Assert.Equal(2, none.FeedCount);
	}

	[Fact]
	public void EntityTypeBreakdown_LabelsAMissingTypeUnknown()
	{
		var summary = CustomPropertiesSummariser.Summarise(
		[
			Feed("a", properties: [new CustomPropertyUsage("beta:x", "beta", null, 10), Usage("beta:y", "Place")]),
			Feed("b", properties: Usage("beta:z", "Place")),
		]);

		Assert.Equal(["Place", CustomPropertiesSummariser.UnknownValue], summary.EntityTypeBreakdown.Select(e => e.EntityType));

		var place = summary.EntityTypeBreakdown[0];
		Assert.Equal(2, place.PropertyCount);
		Assert.Equal(2, place.FeedCount);
	}

	[Fact]
	public void UsagesWithNoPropertyKeyAreIgnored()
	{
		var summary = CustomPropertiesSummariser.Summarise(
		[
			Feed("a", numCustomProperties: 1, properties: [new CustomPropertyUsage(null, null, "Place", 5), Usage("beta:x")]),
		]);

		Assert.Equal(1, summary.DistinctCustomProperties);
		Assert.Equal(["beta:x"], summary.PropertyBreakdown.Select(p => p.Property));
		Assert.Equal(["SessionSeries"], summary.EntityTypeBreakdown.Select(e => e.EntityType));
	}

	#endregion
}
