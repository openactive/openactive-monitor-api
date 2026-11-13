using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.Growth;

/// <summary>
/// Deterministic tests for the ecosystem growth rules, run against hand-written monthly activity. These
/// need no BigQuery credentials and no fixture: every rule the endpoints rely on is pinned here, so the
/// live-data endpoint tests only have to check wiring and invariants.
/// </summary>
public class EcosystemGrowthCalculatorTests
{
	private static readonly DateOnly Jun = new(2026, 6, 1);
	private static readonly DateOnly Jul = new(2026, 7, 1);
	private static readonly DateOnly Aug = new(2026, 8, 1);
	private static readonly DateOnly Sep = new(2026, 9, 1);
	private static readonly DateOnly Oct = new(2026, 10, 1);
	private static readonly DateOnly Nov = new(2026, 11, 1);

	/// <summary>
	/// A dataset's month that published, carried future items and had at least one successful run — the
	/// healthy default, which each test bends to the case it is about.
	/// </summary>
	private static DatasetMonthActivity Active(
		DateOnly month,
		string datasetId,
		long changes = 100,
		long maxFuture = 50,
		bool allRunsFailed = false) =>
		new(month, datasetId, changes, maxFuture, allRunsFailed);

	/// <summary>A month the crawler ran and the feeds were ingested on the same <paramref name="days"/> days.</summary>
	private static MonthCoverage Days(DateOnly month, int days) => new(month, days, days);

	private static CatalogueListing Listed(DateOnly month, string datasetId) => new(month, datasetId);

	/// <summary>
	/// Evaluates months in which every dataset with activity was also listed by the catalogues — the
	/// ordinary case, and the one most rules are about. Tests about listings without activity (or the
	/// reverse) call <see cref="EcosystemGrowthCalculator.Compute"/> directly.
	/// </summary>
	private static IReadOnlyList<EcosystemGrowthMonth> Compute(
		IReadOnlyList<DatasetMonthActivity> activity,
		IEnumerable<MonthCoverage> coverage,
		IReadOnlyList<DateOnly> months,
		DateOnly? snapshot = null) =>
		EcosystemGrowthCalculator.Compute(
			activity.Select(a => Listed(a.Month, a.DatasetId)),
			activity,
			coverage,
			months,
			snapshot);

	#region Month arithmetic

	[Theory]
	[InlineData("2026-11-11", "2026-10-01")]
	[InlineData("2026-11-01", "2026-10-01")]
	[InlineData("2026-10-31", "2026-10-01")]
	[InlineData("2026-10-30", "2026-09-01")]
	[InlineData("2026-02-28", "2026-02-01")]
	[InlineData("2028-02-28", "2028-01-01")]
	[InlineData("2028-02-29", "2028-02-01")]
	[InlineData("2027-01-15", "2026-12-01")]
	public void LastCompleteMonth_IsTheLatestMonthWhoseLastDayIsOnOrBeforeTheSnapshot(string snapshot, string expected)
	{
		Assert.Equal(DateOnly.Parse(expected), EcosystemGrowthCalculator.LastCompleteMonth(DateOnly.Parse(snapshot)));
	}

	[Theory]
	[InlineData("2026-11-12", "2026-11-01")]
	[InlineData("2026-11-01", "2026-11-01")]
	[InlineData("2026-02-27", "2026-02-01")]
	public void InProgressMonth_IsTheSnapshotsMonthBeforeItsLastDay(string snapshot, string expected)
	{
		Assert.Equal(DateOnly.Parse(expected), EcosystemGrowthCalculator.InProgressMonth(DateOnly.Parse(snapshot)));
	}

	[Theory]
	[InlineData("2026-10-31")]
	[InlineData("2026-02-28")]
	[InlineData("2028-02-29")]
	public void InProgressMonth_IsNullOnTheLastDayOfTheMonth(string snapshot)
	{
		Assert.Null(EcosystemGrowthCalculator.InProgressMonth(DateOnly.Parse(snapshot)));
	}

	[Fact]
	public void CandidateMonths_AreTheCompleteMonthsThenTheMonthInProgress()
	{
		var months = EcosystemGrowthCalculator.CandidateMonths(new DateOnly(2026, 11, 11), 3);

		Assert.Equal([Aug, Sep, Oct, Nov], months);
	}

	[Fact]
	public void CandidateMonths_OnTheLastDayOfAMonth_HaveNoMonthInProgress()
	{
		var months = EcosystemGrowthCalculator.CandidateMonths(new DateOnly(2026, 10, 31), 3);

		Assert.Equal([Aug, Sep, Oct], months);
	}

	[Fact]
	public void CandidateMonths_CrossYearBoundaries()
	{
		var months = EcosystemGrowthCalculator.CandidateMonths(new DateOnly(2027, 2, 3), 3);

		Assert.Equal([new(2026, 11, 1), new(2026, 12, 1), new(2027, 1, 1), new(2027, 2, 1)], months);
	}

	[Theory]
	[InlineData(0, 1)]
	[InlineData(-5, 1)]
	[InlineData(12, 12)]
	[InlineData(500, EcosystemGrowthCalculator.MaxMonths)]
	public void CandidateMonths_ClampTheCountOfCompleteMonths(int requested, int expected)
	{
		// The 31st has no month in progress, so the candidates are the complete months alone.
		Assert.Equal(expected, EcosystemGrowthCalculator.CandidateMonths(new DateOnly(2026, 10, 31), requested).Count);
	}

	#endregion

	#region Crawled, added, removed

	[Fact]
	public void AddedAndRemoved_AreTheDifferenceFromThePreviousMonth()
	{
		var result = Compute(
			[
				Active(Sep, "a"), Active(Sep, "b"), Active(Sep, "c"),
				Active(Oct, "b"), Active(Oct, "c"), Active(Oct, "d"), Active(Oct, "e"),
			],
			[Days(Sep, 30), Days(Oct, 31)],
			[Oct]).Single();

		Assert.Equal(["b", "c", "d", "e"], result.Crawled);
		Assert.Equal(["d", "e"], result.Added);
		Assert.Equal(["a"], result.Removed);
		Assert.Equal(31, result.DaysWithData);
		Assert.Equal(Oct, result.PeriodStart);
		Assert.Equal(new DateOnly(2026, 10, 31), result.PeriodEnd);
	}

	[Fact]
	public void AddedAndRemoved_AreNullWhenThePreviousMonthHasNoData()
	{
		var result = Compute(
			[Active(Oct, "a"), Active(Oct, "b")],
			[Days(Oct, 12)],
			[Oct]).Single();

		Assert.Equal(2, result.Crawled.Count);
		Assert.Null(result.Added);
		Assert.Null(result.Removed);
	}

	[Fact]
	public void AddedAndRemoved_AreEmptyNotNullWhenNothingChanged()
	{
		var result = Compute(
			[Active(Sep, "a"), Active(Oct, "a")],
			[Days(Sep, 30), Days(Oct, 31)],
			[Oct]).Single();

		Assert.Empty(result.Added!);
		Assert.Empty(result.Removed!);
	}

	/// <summary>
	/// A crawl that failed is still a crawl that was tried: a dataset whose every run errored is in the
	/// crawl list, and so is not reported as removed.
	/// </summary>
	[Fact]
	public void ADatasetWhoseEveryRunFailed_IsStillCrawled()
	{
		var result = Compute(
			[Active(Sep, "a"), Active(Oct, "a", changes: 0, maxFuture: 0, allRunsFailed: true)],
			[Days(Sep, 30), Days(Oct, 31)],
			[Oct]).Single();

		Assert.Equal(["a"], result.Crawled);
		Assert.Empty(result.Removed!);
	}

	/// <summary>
	/// The publisher keeps its feed ids, but the catalogue lists a new URL. Counted as the catalogue shows
	/// it: the old URL removed, the new one added.
	/// </summary>
	[Fact]
	public void AHostnameMove_IsOneRemovedAndOneAdded()
	{
		var result = Compute(
			[
				Active(Sep, "https://chelmsford.leisurecloud.net/OpenActive/"),
				Active(Oct, "https://chelmsford.gs-signature.cloud/OpenActive/"),
			],
			[Days(Sep, 30), Days(Oct, 31)],
			[Oct]).Single();

		Assert.Equal(["https://chelmsford.gs-signature.cloud/OpenActive/"], result.Added);
		Assert.Equal(["https://chelmsford.leisurecloud.net/OpenActive/"], result.Removed);
	}

	[Fact]
	public void EachMonthIsComparedWithItsOwnPreviousMonth()
	{
		var result = Compute(
			[
				Active(Sep, "a"),
				Active(Oct, "a"), Active(Oct, "b"),
				Active(Nov, "b"),
			],
			[Days(Sep, 30), Days(Oct, 31), Days(Nov, 30)],
			[Sep, Oct, Nov]);

		Assert.Equal([Sep, Oct, Nov], result.Select(m => m.PeriodStart));
		Assert.Null(result[0].Added);
		Assert.Equal(["b"], result[1].Added);
		Assert.Empty(result[1].Removed!);
		Assert.Empty(result[2].Added!);
		Assert.Equal(["a"], result[2].Removed);
	}

	[Fact]
	public void AMonthWithNoDataAfterTheHistoryStarts_ReportsEverythingRemovedAndLeavesTheNextWithoutABaseline()
	{
		var result = Compute(
			[Active(Sep, "a"), Active(Nov, "a")],
			[Days(Sep, 30), Days(Nov, 30)],
			[Oct, Nov]);

		Assert.Equal(0, result[0].DaysWithData);
		Assert.Empty(result[0].Crawled);
		Assert.Equal(["a"], result[0].Removed);
		Assert.Null(result[1].Added);
		Assert.Null(result[1].Removed);
	}

	[Fact]
	public void DuplicateRowsForOneDatasetAndMonth_AreFoldedNotDoubleCounted()
	{
		var result = Compute(
			[
				Active(Oct, "a", changes: 0, maxFuture: 0, allRunsFailed: true),
				Active(Oct, "a", changes: 5, maxFuture: 9, allRunsFailed: false),
			],
			[Days(Oct, 31)],
			[Oct]).Single();

		Assert.Equal(["a"], result.Crawled);
		Assert.Equal(["a"], result.WithFuture);
		Assert.Empty(result.Stopped!);
	}

	[Fact]
	public void ListsAreOrderedOrdinallyByDatasetId()
	{
		var result = Compute(
			[Active(Oct, "c"), Active(Oct, "A"), Active(Oct, "b")],
			[Days(Oct, 31)],
			[Oct]).Single();

		Assert.Equal(["A", "b", "c"], result.Crawled);
	}

	#endregion

	#region Catalogue listings and feed activity

	/// <summary>
	/// A dataset the catalogues list but whose feeds the pipeline does not ingest is still crawled — and so
	/// is never reported as removed. Counting from feed rows alone would drop it out of the crawl list.
	/// </summary>
	[Fact]
	public void AListedDatasetNotIngested_IsCrawledNotRemoved()
	{
		var result = EcosystemGrowthCalculator.Compute(
			[Listed(Sep, "a"), Listed(Oct, "a"), Listed(Sep, "b"), Listed(Oct, "b")],
			[Active(Sep, "a"), Active(Sep, "b"), Active(Oct, "a")],
			[Days(Sep, 30), Days(Oct, 31)],
			[Oct]).Single();

		Assert.Equal(["a", "b"], result.Crawled);
		Assert.Empty(result.Removed!);
		Assert.Equal(["b"], result.NotIngested);
	}

	/// <summary>
	/// Not being ingested is a fact about the pipeline — a blocked or vanished dataset page, or a duplicate
	/// of another dataset — so it is no evidence about future items or publishing either way.
	/// </summary>
	[Fact]
	public void ADatasetNotIngested_IsInNeitherTheFutureNorTheStoppedFigures()
	{
		var result = EcosystemGrowthCalculator.Compute(
			[Listed(Oct, "ingested"), Listed(Oct, "not-ingested")],
			[Active(Oct, "ingested")],
			[Days(Oct, 31)],
			[Oct]).Single();

		Assert.Equal(["ingested"], result.WithFuture);
		Assert.Empty(result.WithoutFuture!);
		Assert.Empty(result.Stopped!);
		Assert.Empty(result.StoppedAllRunsFailed!);
		Assert.Equal(["not-ingested"], result.NotIngested);
	}

	[Fact]
	public void WithFutureWithoutFutureAndNotIngested_PartitionTheCrawledDatasets()
	{
		var result = EcosystemGrowthCalculator.Compute(
			[Listed(Oct, "has"), Listed(Oct, "none"), Listed(Oct, "missing"), Listed(Oct, "silent")],
			[Active(Oct, "has"), Active(Oct, "none", maxFuture: 0), Active(Oct, "silent", changes: 0, maxFuture: 0)],
			[Days(Oct, 31)],
			[Oct]).Single();

		Assert.Equal(["has"], result.WithFuture);
		Assert.Equal(["none", "silent"], result.WithoutFuture);
		Assert.Equal(["missing"], result.NotIngested);
		Assert.Equal(["silent"], result.Stopped);
		Assert.Equal(
			result.Crawled,
			result.WithFuture!.Concat(result.WithoutFuture!).Concat(result.NotIngested!).Order(StringComparer.Ordinal));
	}

	/// <summary>A dataset whose feeds were ingested was tried, whether or not the listing shows it.</summary>
	[Fact]
	public void ADatasetWithActivityButNoListing_IsStillCrawled()
	{
		var result = EcosystemGrowthCalculator.Compute(
			[Listed(Oct, "a")],
			[Active(Oct, "a"), Active(Oct, "unlisted")],
			[Days(Oct, 31)],
			[Oct]).Single();

		Assert.Equal(["a", "unlisted"], result.Crawled);
		Assert.Empty(result.NotIngested!);
	}

	[Fact]
	public void AddedAndRemoved_FollowTheListingsNotTheFeeds()
	{
		var result = EcosystemGrowthCalculator.Compute(
			[Listed(Sep, "a"), Listed(Sep, "gone"), Listed(Oct, "a"), Listed(Oct, "new")],
			// Neither the new dataset nor the departed one was ever ingested.
			[Active(Sep, "a"), Active(Oct, "a")],
			[Days(Sep, 30), Days(Oct, 31)],
			[Oct]).Single();

		Assert.Equal(["new"], result.Added);
		Assert.Equal(["gone"], result.Removed);
	}

	/// <summary>
	/// Before the feed ingestion history begins the catalogue still says what was crawled, but nothing
	/// says what was published: the activity figures are unknown, not zero.
	/// </summary>
	[Fact]
	public void AMonthWithListingsButNoFeedData_HasCrawlFiguresAndNullActivityFigures()
	{
		var result = EcosystemGrowthCalculator.Compute(
			[Listed(Jun, "a"), Listed(Jul, "a"), Listed(Jul, "b")],
			[],
			[new MonthCoverage(Jun, 30, 0), new MonthCoverage(Jul, 31, 0)],
			[Jul]).Single();

		Assert.Equal(31, result.DaysWithData);
		Assert.Equal(0, result.DaysWithFeedData);
		Assert.Equal(["a", "b"], result.Crawled);
		Assert.Equal(["b"], result.Added);
		Assert.Empty(result.Removed!);
		Assert.Null(result.WithFuture);
		Assert.Null(result.WithoutFuture);
		Assert.Null(result.Stopped);
		Assert.Null(result.StoppedAllRunsFailed);
		Assert.Null(result.NotIngested);
	}

	/// <summary>
	/// The month the feed history starts in part way through is measured over the days it has: a listed
	/// dataset with no feed rows in those days is not ingested.
	/// </summary>
	[Fact]
	public void AMonthWithSomeFeedData_IsMeasuredOverTheDaysItHas()
	{
		var result = EcosystemGrowthCalculator.Compute(
			[Listed(Aug, "a"), Listed(Aug, "b")],
			[Active(Aug, "a")],
			[new MonthCoverage(Aug, 31, 11)],
			[Aug]).Single();

		Assert.Equal(11, result.DaysWithFeedData);
		Assert.Equal(["a"], result.WithFuture);
		Assert.Equal(["b"], result.NotIngested);
	}

	[Fact]
	public void TheInProgressThresholdCountsCrawlDays()
	{
		var snapshot = new DateOnly(2026, 11, 7);
		var result = EcosystemGrowthCalculator.Compute(
			[Listed(Nov, "a")],
			[Active(Nov, "a")],
			[new MonthCoverage(Nov, 5, 3)],
			[Nov],
			snapshot).Single();

		Assert.True(result.InProgress);
		Assert.True(EcosystemGrowthCalculator.IsReportable(result));
	}

	#endregion

	#region Future items

	[Fact]
	public void FutureItems_AreSplitOnAnyNonZeroFigureInTheMonth()
	{
		var result = Compute(
			[
				Active(Oct, "has", maxFuture: 1),
				Active(Oct, "none", maxFuture: 0),
				Active(Oct, "negative", maxFuture: -3),
			],
			[Days(Oct, 31)],
			[Oct]).Single();

		Assert.Equal(["has"], result.WithFuture);
		Assert.Equal(["negative", "none"], result.WithoutFuture);
		Assert.Equal(result.Crawled.Count, result.WithFuture!.Count + result.WithoutFuture!.Count);
	}

	/// <summary>Future items and publishing are independent: a dataset can be stopped with stock on offer.</summary>
	[Fact]
	public void ADatasetCanBeStoppedAndStillHaveFutureItems()
	{
		var result = Compute(
			[Active(Oct, "a", changes: 0, maxFuture: 400)],
			[Days(Oct, 31)],
			[Oct]).Single();

		Assert.Equal(["a"], result.WithFuture);
		Assert.Equal(["a"], result.Stopped);
	}

	#endregion

	#region Stopped

	[Fact]
	public void Stopped_IsZeroChangesAcrossTheMonth()
	{
		var result = Compute(
			[
				Active(Oct, "silent", changes: 0),
				Active(Oct, "one-update", changes: 1),
				Active(Oct, "busy", changes: 10_000),
			],
			[Days(Oct, 31)],
			[Oct]).Single();

		Assert.Equal(["silent"], result.Stopped);
	}

	[Fact]
	public void StoppedAllRunsFailed_IsTheSubsetWhoseEveryRunErrored()
	{
		var result = Compute(
			[
				Active(Oct, "silent", changes: 0, allRunsFailed: false),
				Active(Oct, "unreachable", changes: 0, allRunsFailed: true),
				// Every run errored, yet something was counted: not stopped, so not in the subset either.
				Active(Oct, "failing-but-counted", changes: 3, allRunsFailed: true),
			],
			[Days(Oct, 31)],
			[Oct]).Single();

		Assert.Equal(["silent", "unreachable"], result.Stopped);
		Assert.Equal(["unreachable"], result.StoppedAllRunsFailed);
	}

	#endregion

	#region Month in progress

	/// <summary>November with <paramref name="days"/> days of data, evaluated as at the given day.</summary>
	private static IReadOnlyList<EcosystemGrowthMonth> WithNovemberDays(int days, int months = 3) =>
		Compute(
			[Active(Sep, "a"), Active(Oct, "a"), Active(Nov, "a")],
			[Days(Sep, 30), Days(Oct, 31), Days(Nov, days)],
			EcosystemGrowthCalculator.CandidateMonths(new DateOnly(2026, 11, days), months),
			new DateOnly(2026, 11, days));

	[Fact]
	public void Compute_MarksOnlyAMonthEndingAfterTheSnapshotAsInProgress()
	{
		var result = WithNovemberDays(12);

		Assert.Equal([false, false, false, true], result.Select(m => m.InProgress));
		Assert.Equal(new DateOnly(2026, 11, 30), result[^1].PeriodEnd);
	}

	[Fact]
	public void Compute_WithoutASnapshot_TreatsEveryMonthAsComplete()
	{
		var result = Compute([Active(Nov, "a")], [Days(Nov, 3)], [Nov]);

		Assert.False(result.Single().InProgress);
	}

	[Theory]
	[InlineData(4, false)]
	[InlineData(EcosystemGrowthCalculator.MinDaysForInProgressMonth, true)]
	[InlineData(20, true)]
	public void IsReportable_TheMonthInProgressNeedsFiveDaysOfData(int days, bool expected)
	{
		Assert.Equal(5, EcosystemGrowthCalculator.MinDaysForInProgressMonth);
		Assert.Equal(expected, EcosystemGrowthCalculator.IsReportable(WithNovemberDays(days)[^1]));
	}

	[Fact]
	public void IsReportable_ACompleteMonthAlwaysIs()
	{
		var thin = Compute([Active(Sep, "a")], [Days(Sep, 1)], [Sep], new DateOnly(2026, 11, 11));

		Assert.True(EcosystemGrowthCalculator.IsReportable(thin.Single()));
	}

	[Fact]
	public void ReportableMonths_IncludeTheMonthInProgressOnceItHasFiveDays()
	{
		var kept = EcosystemGrowthCalculator.ReportableMonths(WithNovemberDays(5), 3);

		Assert.Equal([Sep, Oct, Nov], kept.Select(m => m.PeriodStart));
		Assert.True(kept[^1].InProgress);
	}

	[Fact]
	public void ReportableMonths_EndAtTheLastCompleteMonthUntilThen()
	{
		var kept = EcosystemGrowthCalculator.ReportableMonths(WithNovemberDays(4), 3);

		Assert.Equal([Sep, Oct], kept.Select(m => m.PeriodStart));
		Assert.All(kept, m => Assert.False(m.InProgress));
	}

	/// <summary>Asking for N months returns N whether or not the month in progress makes the cut.</summary>
	[Fact]
	public void ReportableMonths_AreAsManyAsAskedForEitherWay()
	{
		IReadOnlyList<EcosystemGrowthMonth> Evaluate(int novemberDays) =>
			Compute(
				[Active(Aug, "a"), Active(Sep, "a"), Active(Oct, "a"), Active(Nov, "a")],
				[Days(Aug, 31), Days(Sep, 30), Days(Oct, 31), Days(Nov, novemberDays)],
				EcosystemGrowthCalculator.CandidateMonths(new DateOnly(2026, 11, novemberDays), 2),
				new DateOnly(2026, 11, novemberDays));

		Assert.Equal([Oct, Nov], EcosystemGrowthCalculator.ReportableMonths(Evaluate(9), 2).Select(m => m.PeriodStart));
		Assert.Equal([Sep, Oct], EcosystemGrowthCalculator.ReportableMonths(Evaluate(2), 2).Select(m => m.PeriodStart));
	}

	#endregion

	#region Months before the history

	[Fact]
	public void DropMonthsBeforeData_RemovesOnlyTheLeadingEmptyMonths()
	{
		var computed = Compute(
			[Active(Sep, "a"), Active(Nov, "a")],
			[Days(Sep, 11), Days(Nov, 30)],
			[Aug, Sep, Oct, Nov]);

		var kept = EcosystemGrowthCalculator.DropMonthsBeforeData(computed);

		Assert.Equal([Sep, Oct, Nov], kept.Select(m => m.PeriodStart));
	}

	[Fact]
	public void DropMonthsBeforeData_KeepsMonthsWithListingsButNoFeedData()
	{
		var computed = EcosystemGrowthCalculator.Compute(
			[Listed(Jul, "a"), Listed(Aug, "a")],
			[Active(Aug, "a")],
			[new MonthCoverage(Jul, 31, 0), new MonthCoverage(Aug, 31, 11)],
			[Jun, Jul, Aug]);

		Assert.Equal([Jul, Aug], EcosystemGrowthCalculator.DropMonthsBeforeData(computed).Select(m => m.PeriodStart));
	}

	[Fact]
	public void DropMonthsBeforeData_WithNoDataAtAll_IsEmpty()
	{
		var computed = EcosystemGrowthCalculator.Compute([], [], [], [Sep, Oct]);

		Assert.Empty(EcosystemGrowthCalculator.DropMonthsBeforeData(computed));
	}

	#endregion
}
