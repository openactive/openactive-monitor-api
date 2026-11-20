using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.ActivityCoverage;

/// <summary>
/// Deterministic tests for turning summary rows into per-district, per-concept counts: the
/// once-per-concept rule, dropping unmatched labels, the activity filter and the ordering.
/// </summary>
public class ActivityCoverageTests
{
	private readonly ActivityVocabulary vocabulary = TestVocabulary.Build();

	private static ActivityCoverageSourceRow Row(string district, long count, params string[] labels) =>
		new(district, district + " name", "R1", "Region", "C1", "Country", count, labels);

	private List<ActivityCoverageEntry> Build(IEnumerable<ActivityCoverageSourceRow> rows, params string[] filter) =>
		Services.Admin.ActivityCoverage.Build(rows, vocabulary, ActivityLabelMatcher.DefaultMinSimilarity, filter);

	[Fact]
	public void TwoLabelsResolvingToOneConcept_CountTheRowOnce()
	{
		var result = Build([Row("D1", 10, "Swimming", "Lane Swimming")]);

		var entry = Assert.Single(result);
		Assert.Equal("Swimming", entry.Activity.PrefLabel);
		Assert.Equal(10, entry.OpportunityCount);
	}

	[Fact]
	public void LabelsResolvingToDifferentConcepts_CountTheRowForEach()
	{
		var result = Build([Row("D1", 10, "Swimming", "Aqua Fit")]);

		Assert.Equal(["Aqua Fit", "Swimming"], result.Select(e => e.Activity.PrefLabel));
		Assert.All(result, e => Assert.Equal(10, e.OpportunityCount));
	}

	[Fact]
	public void RowsInOneDistrictAreSummedPerConcept()
	{
		var result = Build([Row("D1", 10, "Swimming"), Row("D1", 5, "public swimming"), Row("D2", 3, "swimming")]);

		Assert.Equal(15, result.Single(e => e.DistrictCode == "D1").OpportunityCount);
		Assert.Equal(3, result.Single(e => e.DistrictCode == "D2").OpportunityCount);
	}

	[Fact]
	public void UnmatchedLabelsAreDropped()
	{
		var result = Build([Row("D1", 10, "Gym Session", "Fitness Classes"), Row("D1", 4, "Gym Session", "Squash")]);

		var entry = Assert.Single(result);
		Assert.Equal("Squash", entry.Activity.PrefLabel);
		Assert.Equal(4, entry.OpportunityCount);
	}

	[Fact]
	public void TheCanonicalLabelAndIdAreReported()
	{
		var entry = Assert.Single(Build([Row("D1", 1, "SWIMMING")]));

		Assert.Equal("Swimming", entry.Activity.PrefLabel);
		Assert.Equal(TestVocabulary.SwimmingId, entry.Activity.Id);
	}

	[Fact]
	public void LocationFieldsAreCarriedThrough()
	{
		var entry = Assert.Single(Build([Row("D1", 1, "Swimming")]));

		Assert.Equal("D1", entry.DistrictCode);
		Assert.Equal("D1 name", entry.DistrictName);
		Assert.Equal("R1", entry.RegionCode);
		Assert.Equal("C1", entry.CountryCode);
	}

	[Theory]
	[InlineData("Swimming")]
	[InlineData("swimming")]
	[InlineData(TestVocabulary.SwimmingId)]
	public void ActivityFilter_AcceptsTheLabelInAnyCaseOrTheId(string filter)
	{
		var result = Build([Row("D1", 10, "Swimming", "Aqua Fit")], filter);

		Assert.Equal("Swimming", Assert.Single(result).Activity.PrefLabel);
	}

	[Fact]
	public void ActivityFilter_ValuesAreOred()
	{
		var result = Build([Row("D1", 10, "Swimming", "Aqua Fit", "Squash")], "Swimming", TestVocabulary.AquaFitId);

		Assert.Equal(["Aqua Fit", "Swimming"], result.Select(e => e.Activity.PrefLabel));
	}

	[Fact]
	public void ActivityFilter_NamingNoConceptReturnsNothingRatherThanEverything()
	{
		Assert.Empty(Build([Row("D1", 10, "Swimming")], "Underwater Basket Weaving"));
	}

	[Fact]
	public void ActivityFilter_DoesNotFuzzyMatch()
	{
		// The filter names a concept; it is not a published label, so "lane swimming" is not Swimming here.
		Assert.Empty(Build([Row("D1", 10, "Swimming")], "lane swimming"));
	}

	[Fact]
	public void OrderedByActivityThenLargestCountThenDistrict()
	{
		var result = Build(
		[
			Row("D2", 5, "Swimming"),
			Row("D1", 5, "Swimming"),
			Row("D3", 9, "Swimming"),
			Row("D1", 1, "Aqua Fit"),
		]);

		Assert.Equal(
			["Aqua Fit/D1", "Swimming/D3", "Swimming/D1", "Swimming/D2"],
			result.Select(e => $"{e.Activity.PrefLabel}/{e.DistrictCode}"));
	}

	[Fact]
	public void ZeroCountRowsProduceNoEntry()
	{
		Assert.Empty(Build([Row("D1", 0, "Swimming")]));
	}

	[Fact]
	public void AStricterThresholdNeverIncreasesACount()
	{
		ActivityCoverageSourceRow[] rows = [Row("D1", 10, "Swimming"), Row("D1", 7, "lane swimming")];

		var loose = Services.Admin.ActivityCoverage.Build(rows, vocabulary, 0.85, []).Single();
		var strict = Services.Admin.ActivityCoverage.Build(rows, vocabulary, 1.0, []).Single();

		Assert.Equal(17, loose.OpportunityCount);
		Assert.Equal(10, strict.OpportunityCount);
	}
}
