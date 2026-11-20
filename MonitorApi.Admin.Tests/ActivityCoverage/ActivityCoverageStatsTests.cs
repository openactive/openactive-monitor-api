using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.ActivityCoverage;

/// <summary>
/// Deterministic tests for the national headline figures: what counts as labelled, what counts as
/// linked, once-per-opportunity counting and the percentages.
/// </summary>
public class ActivityCoverageStatsTests
{
	private readonly ActivityVocabulary vocabulary = TestVocabulary.Build();

	private static ActivityCoverageSourceRow Row(string district, long count, params string[] labels) =>
		new(district, district + " name", "R1", "Region", "C1", "Country", count, labels);

	private ActivityCoverageStats Summarise(params ActivityCoverageSourceRow[] rows) =>
		Services.Admin.ActivityCoverage.Summarise(rows, vocabulary, ActivityLabelMatcher.DefaultMinSimilarity);

	[Fact]
	public void UnlabelledRowsAddToTheTotalOnly()
	{
		var stats = Summarise(Row("D1", 10), Row("D1", 4, "Swimming"));

		Assert.Equal(14, stats.ActivityOpportunities);
		Assert.Equal(4, stats.LabelledOpportunities);
		Assert.Equal(4, stats.LinkedOpportunities);
	}

	[Fact]
	public void RowsWhoseLabelsAllMissAreLabelledButNotLinked()
	{
		var stats = Summarise(Row("D1", 10, "Gym Session", "Fitness Classes"));

		Assert.Equal(10, stats.LabelledOpportunities);
		Assert.Equal(0, stats.LinkedOpportunities);
		Assert.Equal(0, stats.ActivitiesOffered);
	}

	[Fact]
	public void ARowReachingSeveralConceptsIsLinkedOnce()
	{
		var stats = Summarise(Row("D1", 10, "Swimming", "Aqua Fit", "Gym Session"));

		Assert.Equal(10, stats.LinkedOpportunities);
		Assert.Equal(2, stats.ActivitiesOffered);
	}

	[Fact]
	public void ActivitiesOfferedCountsDistinctConceptsAcrossDistricts()
	{
		var stats = Summarise(Row("D1", 1, "Swimming"), Row("D2", 1, "lane swimming"), Row("D2", 1, "Squash"));

		Assert.Equal(2, stats.ActivitiesOffered);
		Assert.Equal(vocabulary.Concepts.Count, stats.VocabularySize);
	}

	[Fact]
	public void ZeroCountRowsOfferNoActivity()
	{
		Assert.Equal(0, Summarise(Row("D1", 0, "Swimming")).ActivitiesOffered);
	}

	[Fact]
	public void PercentagesAreOfTheTotalToOneDecimalPlace()
	{
		var stats = Summarise(Row("D1", 1), Row("D1", 1, "Gym Session"), Row("D1", 1, "Swimming"));

		Assert.Equal(66.7, stats.PercentageOfTotal(stats.LabelledOpportunities));
		Assert.Equal(33.3, stats.PercentageOfTotal(stats.LinkedOpportunities));
	}

	[Fact]
	public void PercentagesAreNullWhenThereAreNoOpportunities()
	{
		var stats = Summarise();

		Assert.Equal(0, stats.ActivityOpportunities);
		Assert.Null(stats.PercentageOfTotal(stats.LinkedOpportunities));
	}

	[Fact]
	public void AStricterThresholdNeverRaisesLinkedOrOffered()
	{
		ActivityCoverageSourceRow[] rows = [Row("D1", 10, "Swimming"), Row("D1", 7, "lane swimming"), Row("D1", 3, "squash booking")];

		var loose = Services.Admin.ActivityCoverage.Summarise(rows, vocabulary, 0.85);
		var strict = Services.Admin.ActivityCoverage.Summarise(rows, vocabulary, 1.0);

		Assert.Equal(20, loose.LinkedOpportunities);
		Assert.Equal(10, strict.LinkedOpportunities);
		Assert.Equal(2, loose.ActivitiesOffered);
		Assert.Equal(1, strict.ActivitiesOffered);
		Assert.Equal(loose.LabelledOpportunities, strict.LabelledOpportunities);
	}
}
