using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.ActivityCoverage;

/// <summary>
/// Deterministic tests for the national activity ranking: once-per-concept counting, district counts,
/// ordering and agreement with the per-district breakdown.
/// </summary>
public class ActivityRankTests
{
	private readonly ActivityVocabulary vocabulary = TestVocabulary.Build();

	private static ActivityCoverageSourceRow Row(string district, long count, params string[] labels) =>
		new(district, district + " name", "R1", "Region", "C1", "Country", count, labels);

	private List<ActivityRankEntry> Rank(params ActivityCoverageSourceRow[] rows) =>
		Services.Admin.ActivityCoverage.Rank(rows, vocabulary, ActivityLabelMatcher.DefaultMinSimilarity);

	[Fact]
	public void CountsAreSummedNationallyAndDistrictsCounted()
	{
		var entry = Assert.Single(Rank(Row("D1", 10, "Swimming"), Row("D1", 5, "lane swimming"), Row("D2", 3, "SWIMMING")));

		Assert.Equal(TestVocabulary.SwimmingId, entry.Activity.Id);
		Assert.Equal(18, entry.OpportunityCount);
		Assert.Equal(2, entry.DistrictCount);
	}

	[Fact]
	public void ARowCountsOncePerDistinctConcept()
	{
		var result = Rank(Row("D1", 10, "Swimming", "Lane Swimming", "Aqua Fit"));

		Assert.Equal(["Aqua Fit", "Swimming"], result.Select(e => e.Activity.PrefLabel).Order(StringComparer.Ordinal));
		Assert.All(result, e => Assert.Equal(10, e.OpportunityCount));
	}

	[Fact]
	public void OrderedByLargestCountThenLabel_WithOneBasedRanks()
	{
		var result = Rank(Row("D1", 5, "Squash"), Row("D1", 9, "Swimming"), Row("D1", 5, "Aqua Fit"));

		Assert.Equal(["Swimming", "Aqua Fit", "Squash"], result.Select(e => e.Activity.PrefLabel));
		Assert.Equal([1, 2, 3], result.Select(e => e.Rank));
	}

	[Fact]
	public void UnmatchedAndZeroCountRowsAreLeftOut()
	{
		Assert.Empty(Rank(Row("D1", 10, "Gym Session"), Row("D1", 0, "Swimming"), Row("D1", 4)));
	}

	[Fact]
	public void AgreesWithThePerDistrictBreakdown()
	{
		ActivityCoverageSourceRow[] rows =
		[
			Row("D1", 10, "Swimming", "Aqua Fit"),
			Row("D2", 4, "lane swimming"),
			Row("D2", 3, "Squash", "Gym Session"),
			Row("D3", 2, "aqua fit"),
		];

		var breakdown = Services.Admin.ActivityCoverage.Build(rows, vocabulary, ActivityLabelMatcher.DefaultMinSimilarity, []);

		Assert.All(Rank(rows), entry =>
		{
			var districts = breakdown.Where(e => e.Activity.Id == entry.Activity.Id).ToList();
			Assert.Equal(districts.Sum(e => e.OpportunityCount), entry.OpportunityCount);
			Assert.Equal(districts.Count, entry.DistrictCount);
		});
	}
}
