using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.Quality;

/// <summary>
/// Deterministic tests for the feed-quality summary arithmetic, run against hand-written assessments.
/// These need no BigQuery credentials and no fixture — every rule the endpoint relies on is pinned
/// here, so the live-data endpoint tests only have to check the wiring and the envelope.
/// </summary>
public class FeedQualitySummariserTests
{
	private static readonly DateTime Assessed = new(2026, 9, 14, 3, 0, 0, DateTimeKind.Utc);

	/// <summary>
	/// An assessment with everything defaulted to "not reported", so each test states only the fields
	/// it is about and a null anywhere else is deliberate.
	/// </summary>
	private static FeedQuality Feed(
		string feedId,
		string datasetUrl = "https://example.org/dataset",
		string? publisherName = "Example",
		string? status = null,
		string? grade = null,
		string? feedType = null,
		string? feedVersion = null,
		double? score = null,
		bool? isRegular = null,
		long? futureItems = null,
		DateTime? lastAssessed = null,
		FeedQualityCompletenessValues? completeness = null) =>
		new(
			feedId,
			datasetUrl,
			DatasetName: null,
			publisherName,
			feedType,
			FeedUrl: null,
			isRegular,
			status,
			grade,
			feedVersion,
			score,
			futureItems,
			lastAssessed,
			completeness ?? FeedQualityCompletenessValues.None,
			Warnings: null,
			Errors: null,
			MissingRequiredFields: null);

	/// <summary>Completeness percentages (0–100) in the order <c>Columns</c> presents them; omitted ones are unmeasured.</summary>
	private static FeedQualityCompletenessValues Completeness(
		double? location = null,
		double? startDate = null,
		double? endDate = null,
		double? activities = null,
		double? facilities = null,
		double? ageRange = null,
		double? level = null,
		double? accessibilitySupport = null,
		double? genderRestriction = null) =>
		new(location, startDate, endDate, activities, facilities, ageRange, level, accessibilitySupport, genderRestriction);

	#region Coverage

	[Fact]
	public void EmptyInput_SummarisesToZeroesAndNulls()
	{
		var summary = FeedQualitySummariser.Summarise([]);

		Assert.Equal(0, summary.TotalFeeds);
		Assert.Equal(0, summary.TotalDatasets);
		Assert.Equal(0, summary.TotalPublishers);
		Assert.Equal(0, summary.FeedsScored);
		Assert.Null(summary.AverageScore);
		Assert.Null(summary.MedianScore);
		Assert.Null(summary.MinScore);
		Assert.Null(summary.MaxScore);
		Assert.Null(summary.OldestAssessment);
		Assert.Null(summary.NewestAssessment);
		Assert.Null(summary.Completeness.Location.Average);
		Assert.Equal(0, summary.Completeness.Location.FeedsReporting);

		// The histogram keeps its shape even with nothing in it, so the dashboard draws the same axes.
		Assert.Equal(FeedQualitySummariser.ScoreBucketCount, summary.ScoreBuckets.Count);
		Assert.All(summary.ScoreBuckets, b => Assert.Equal(0, b.FeedCount));

		// A breakdown of nothing is empty rather than a single "unknown" row for zero feeds.
		Assert.Empty(summary.StatusBreakdown);
	}

	[Fact]
	public void DatasetsAndPublishers_AreCountedDistinctly()
	{
		var summary = FeedQualitySummariser.Summarise(
		[
			Feed("a", "https://one.example/d", "Publisher One"),
			Feed("b", "https://one.example/d", "Publisher One"),
			Feed("c", "https://two.example/d", "Publisher One"),
			Feed("d", "https://three.example/d", "Publisher Two"),
		]);

		Assert.Equal(4, summary.TotalFeeds);
		Assert.Equal(3, summary.TotalDatasets);
		Assert.Equal(2, summary.TotalPublishers);
	}

	[Fact]
	public void FeedsWithNoPublisher_CountTowardsNoPublisher()
	{
		// Two unnamed publishers are not one shared publisher, so neither is counted at all.
		var summary = FeedQualitySummariser.Summarise(
		[
			Feed("a", "https://one.example/d", publisherName: null),
			Feed("b", "https://two.example/d", publisherName: "   "),
			Feed("c", "https://three.example/d", publisherName: "Named"),
		]);

		Assert.Equal(1, summary.TotalPublishers);
		Assert.Equal(3, summary.TotalDatasets);
	}

	[Fact]
	public void Regularity_SplitsThreeWaysAndSumsToTheTotal()
	{
		var summary = FeedQualitySummariser.Summarise(
		[
			Feed("a", isRegular: true),
			Feed("b", isRegular: true),
			Feed("c", isRegular: false),
			Feed("d", isRegular: null),
		]);

		Assert.Equal(2, summary.RegularFeeds);
		Assert.Equal(1, summary.IrregularFeeds);
		Assert.Equal(1, summary.RegularityUnknown);
		Assert.Equal(summary.TotalFeeds, summary.RegularFeeds + summary.IrregularFeeds + summary.RegularityUnknown);
	}

	#endregion

	#region Status

	[Fact]
	public void Status_IsMatchedCaseInsensitivelyAndTrimmed()
	{
		var summary = FeedQualitySummariser.Summarise(
		[
			Feed("a", status: "ok"),
			Feed("b", status: " OK "),
			Feed("c", status: "Warning"),
			Feed("d", status: "eRRoR"),
		]);

		Assert.Equal(2, summary.FeedsOk);
		Assert.Equal(1, summary.FeedsWithWarnings);
		Assert.Equal(1, summary.FeedsWithErrors);
		Assert.Equal(0, summary.FeedsStatusUnknown);
	}

	[Fact]
	public void Status_MissingOrUnrecognised_IsCountedAsUnknownRatherThanDropped()
	{
		var summary = FeedQualitySummariser.Summarise(
		[
			Feed("a", status: "OK"),
			Feed("b", status: null),
			Feed("c", status: "   "),
			Feed("d", status: "SOMETHING_ELSE"),
		]);

		Assert.Equal(1, summary.FeedsOk);
		Assert.Equal(3, summary.FeedsStatusUnknown);
		Assert.Equal(
			summary.TotalFeeds,
			summary.FeedsOk + summary.FeedsWithWarnings + summary.FeedsWithErrors + summary.FeedsStatusUnknown);
	}

	[Fact]
	public void DatasetsWithErrors_CountsDatasetsNotFeeds()
	{
		var summary = FeedQualitySummariser.Summarise(
		[
			Feed("a", "https://one.example/d", status: "ERROR"),
			Feed("b", "https://one.example/d", status: "ERROR"),
			Feed("c", "https://two.example/d", status: "OK"),
		]);

		Assert.Equal(2, summary.FeedsWithErrors);
		Assert.Equal(1, summary.DatasetsWithErrors);
	}

	#endregion

	#region Future supply

	[Fact]
	public void FutureData_CountsOnlyPositiveFiguresAndSumsTheRest()
	{
		var summary = FeedQualitySummariser.Summarise(
		[
			Feed("a", "https://one.example/d", futureItems: 100),
			Feed("b", "https://one.example/d", futureItems: 0),
			Feed("c", "https://two.example/d", futureItems: 5),
			// Not reported at all: contributes to neither the count nor the sum.
			Feed("d", "https://three.example/d", futureItems: null),
		]);

		Assert.Equal(2, summary.FeedsWithFutureData);
		Assert.Equal(2, summary.DatasetsWithFutureData);
		Assert.Equal(105, summary.TotalFutureOpportunityItems);
	}

	#endregion

	#region Score

	[Fact]
	public void Score_AveragesOnlyOverScoredFeeds()
	{
		var summary = FeedQualitySummariser.Summarise(
		[
			Feed("a", score: 40),
			Feed("b", score: 60),
			// Unscored: excluded from the numerator and the denominator alike, so the mean is 50 not 33.3.
			Feed("c", score: null),
		]);

		Assert.Equal(2, summary.FeedsScored);
		Assert.Equal(50, summary.AverageScore);
		Assert.Equal(40, summary.MinScore);
		Assert.Equal(60, summary.MaxScore);
	}

	[Fact]
	public void Median_IsTheMiddleValueForAnOddCount()
	{
		var summary = FeedQualitySummariser.Summarise(
		[
			Feed("a", score: 90),
			Feed("b", score: 10),
			Feed("c", score: 50),
		]);

		Assert.Equal(50, summary.MedianScore);
	}

	[Fact]
	public void Median_AveragesTheTwoMiddleValuesForAnEvenCount()
	{
		var summary = FeedQualitySummariser.Summarise(
		[
			Feed("a", score: 10),
			Feed("b", score: 40),
			Feed("c", score: 60),
			Feed("d", score: 90),
		]);

		Assert.Equal(50, summary.MedianScore);
	}

	[Fact]
	public void ScoreBuckets_PutEachBoundaryInTheHigherBucketExceptTheTop()
	{
		var summary = FeedQualitySummariser.Summarise(
		[
			Feed("zero", score: 0),
			Feed("nineteen", score: 19.9),
			Feed("twenty", score: 20),
			Feed("eighty", score: 80),
			Feed("hundred", score: 100),
		]);

		var counts = summary.ScoreBuckets.Select(b => b.FeedCount).ToList();

		// 0–20: 0 and 19.9. 20–40: 20. 80–100: 80 and 100, the top bucket taking its upper bound.
		Assert.Equal([2, 1, 0, 0, 2], counts);
		Assert.Equal([0, 20, 40, 60, 80], summary.ScoreBuckets.Select(b => b.Lower));
		Assert.Equal([20, 40, 60, 80, 100], summary.ScoreBuckets.Select(b => b.Upper));
		Assert.Equal(summary.FeedsScored, counts.Sum());
	}

	[Fact]
	public void ScoreBuckets_ClampScoresStoredOutsideTheZeroToHundredRange()
	{
		// A stored score is not ours to validate, but it must not index off the end of the histogram.
		var summary = FeedQualitySummariser.Summarise(
		[
			Feed("below", score: -5),
			Feed("above", score: 140),
		]);

		Assert.Equal(1, summary.ScoreBuckets[0].FeedCount);
		Assert.Equal(1, summary.ScoreBuckets[^1].FeedCount);
		Assert.Equal(2, summary.ScoreBuckets.Sum(b => b.FeedCount));
	}

	#endregion

	#region Completeness

	[Fact]
	public void Completeness_ExcludesUnreportedFeedsFromBothHalvesOfTheMean()
	{
		var summary = FeedQualitySummariser.Summarise(
		[
			Feed("a", completeness: Completeness(location: 100)),
			Feed("b", completeness: Completeness(location: 50)),
			// Reports no location at all: the mean is 75 over two feeds, not 50 over three.
			Feed("c", completeness: Completeness(startDate: 20)),
		]);

		Assert.Equal(75, summary.Completeness.Location.Average);
		Assert.Equal(2, summary.Completeness.Location.FeedsReporting);

		Assert.Equal(20, summary.Completeness.StartDate.Average);
		Assert.Equal(1, summary.Completeness.StartDate.FeedsReporting);
	}

	[Fact]
	public void Completeness_IsNullNotZeroWhenNoFeedReportsIt()
	{
		var summary = FeedQualitySummariser.Summarise([Feed("a", completeness: Completeness(location: 100))]);

		Assert.Null(summary.Completeness.Facilities.Average);
		Assert.Equal(0, summary.Completeness.Facilities.FeedsReporting);
	}

	[Fact]
	public void Completeness_KeepsARealZeroApartFromAnUnreportedValue()
	{
		var summary = FeedQualitySummariser.Summarise(
		[
			// A stored 0 is a real measurement — no item carries the property — and is averaged in.
			Feed("a", completeness: Completeness(level: 0)),
			Feed("b", completeness: Completeness(level: 100)),
		]);

		Assert.Equal(50, summary.Completeness.Level.Average);
		Assert.Equal(2, summary.Completeness.Level.FeedsReporting);
	}

	[Fact]
	public void Completeness_MapsEachColumnToItsOwnProperty()
	{
		// Distinct values per column, so a transposition between the positional read and the properties
		// it fills would show up rather than cancelling out.
		var summary = FeedQualitySummariser.Summarise(
		[
			Feed("a", completeness: Completeness(
				location: 10,
				startDate: 20,
				endDate: 30,
				activities: 40,
				facilities: 50,
				ageRange: 60,
				level: 70,
				accessibilitySupport: 80,
				genderRestriction: 90)),
		]);

		Assert.Equal(10, summary.Completeness.Location.Average);
		Assert.Equal(20, summary.Completeness.StartDate.Average);
		Assert.Equal(30, summary.Completeness.EndDate.Average);
		Assert.Equal(40, summary.Completeness.Activities.Average);
		Assert.Equal(50, summary.Completeness.Facilities.Average);
		Assert.Equal(60, summary.Completeness.AgeRange.Average);
		Assert.Equal(70, summary.Completeness.Level.Average);
		Assert.Equal(80, summary.Completeness.AccessibilitySupport.Average);
		Assert.Equal(90, summary.Completeness.GenderRestriction.Average);
	}

	#endregion

	#region Breakdowns

	[Fact]
	public void Breakdown_CountsFeedsAndDatasetsAndSharesOfTheWhole()
	{
		var summary = FeedQualitySummariser.Summarise(
		[
			Feed("a", "https://one.example/d", grade: "Gold"),
			Feed("b", "https://two.example/d", grade: "Gold"),
			Feed("c", "https://two.example/d", grade: "Gold"),
			Feed("d", "https://three.example/d", grade: "Bronze"),
		]);

		var gold = summary.GradeBreakdown.Single(b => b.Value == "Gold");

		Assert.Equal(3, gold.FeedCount);
		Assert.Equal(2, gold.DatasetCount);
		Assert.Equal(0.75, gold.Share);
		Assert.Equal(summary.TotalFeeds, summary.GradeBreakdown.Sum(b => b.FeedCount));
	}

	[Fact]
	public void Breakdown_LabelsMissingValuesUnknownRatherThanDroppingThem()
	{
		var summary = FeedQualitySummariser.Summarise(
		[
			Feed("a", feedType: "SessionSeries"),
			Feed("b", feedType: null),
			Feed("c", feedType: "  "),
		]);

		var unknown = summary.FeedTypeBreakdown.Single(b => b.Value == FeedQualitySummariser.UnknownValue);

		Assert.Equal(2, unknown.FeedCount);
		Assert.Equal(summary.TotalFeeds, summary.FeedTypeBreakdown.Sum(b => b.FeedCount));
	}

	[Fact]
	public void Breakdown_IsOrderedByCountDescendingThenValueAscending()
	{
		var summary = FeedQualitySummariser.Summarise(
		[
			Feed("a", feedVersion: "V2.1"),
			Feed("b", feedVersion: "V2.1"),
			Feed("c", feedVersion: "V2.1"),
			// Two versions tied on one feed each: the tiebreak must order them, not the input order.
			Feed("d", feedVersion: "V2.0"),
			Feed("e", feedVersion: "V1.1"),
		]);

		Assert.Equal(["V2.1", "V1.1", "V2.0"], summary.FeedVersionBreakdown.Select(b => b.Value));
	}

	[Fact]
	public void Breakdown_GroupsValuesDifferingOnlyInCase()
	{
		var summary = FeedQualitySummariser.Summarise(
		[
			Feed("a", grade: "Gold"),
			Feed("b", grade: "gold"),
		]);

		var only = Assert.Single(summary.GradeBreakdown);

		Assert.Equal(2, only.FeedCount);
	}

	#endregion

	#region Freshness

	[Fact]
	public void Assessment_TimestampsSpanTheOldestAndNewestReported()
	{
		var summary = FeedQualitySummariser.Summarise(
		[
			Feed("a", lastAssessed: Assessed),
			Feed("b", lastAssessed: Assessed.AddDays(-3)),
			Feed("c", lastAssessed: null),
		]);

		Assert.Equal(Assessed.AddDays(-3), summary.OldestAssessment);
		Assert.Equal(Assessed, summary.NewestAssessment);
	}

	[Fact]
	public void Assessment_TimestampsAreNullWhenNoFeedReportsOne()
	{
		var summary = FeedQualitySummariser.Summarise([Feed("a")]);

		Assert.Null(summary.OldestAssessment);
		Assert.Null(summary.NewestAssessment);
	}

	#endregion
}
