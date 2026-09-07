using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.Summary;

/// <summary>
/// Pure tests for the arithmetic behind <c>/admin/summary</c>: no fixture, no BigQuery credentials.
/// </summary>
public class MonitorSummariesTests
{
	private const string MonitorId = "single_feed_stall";

	private static readonly DateOnly Day = new(2026, 9, 1);

	private static IReadOnlyList<MonitorTrendPoint> Trend(params (int Open, int PastThreshold)[] days) =>
		days.Select((d, i) => new MonitorTrendPoint(Day.AddDays(i - (days.Length - 1)), d.Open, d.PastThreshold))
			.ToList();

	[Fact]
	public void EmptyTrend_HasNothingToReport()
	{
		Assert.Null(MonitorSummaries.Summarise(MonitorId, []));
	}

	[Fact]
	public void CountsComeFromTheMostRecentDay()
	{
		var summary = MonitorSummaries.Summarise(MonitorId, Trend((12, 2), (14, 3), (23, 7)))!;

		Assert.Equal(MonitorId, summary.MonitorId);
		Assert.Equal(23, summary.Count);
		Assert.Equal(7, summary.PastThresholdCount);
	}

	[Fact]
	public void SparklineIsTheTrailingWeekOldestFirstAndEndsAtTheCount()
	{
		var summary = MonitorSummaries.Summarise(
			MonitorId,
			Trend((1, 0), (2, 0), (12, 1), (14, 2), (15, 3), (18, 4), (20, 5), (22, 6), (23, 7)))!;

		Assert.Equal([12, 14, 15, 18, 20, 22, 23], summary.Sparkline);
		Assert.Equal(summary.Count, summary.Sparkline[^1]);
	}

	[Fact]
	public void ShortHistory_GivesAShorterSparklineRatherThanPadding()
	{
		var summary = MonitorSummaries.Summarise(MonitorId, Trend((4, 1), (5, 2)))!;

		Assert.Equal([4, 5], summary.Sparkline);
	}

	[Fact]
	public void DeltasCompareTheLastTwoDays()
	{
		var summary = MonitorSummaries.Summarise(MonitorId, Trend((12, 2), (20, 5), (23, 7)))!;

		Assert.Equal(3, summary.CountDelta);
		Assert.Equal(2, summary.PastThresholdDelta);
	}

	[Fact]
	public void DeltasAreNegativeWhenTheEstateImproves()
	{
		var summary = MonitorSummaries.Summarise(MonitorId, Trend((23, 7), (20, 5)))!;

		Assert.Equal(-3, summary.CountDelta);
		Assert.Equal(-2, summary.PastThresholdDelta);
	}

	[Fact]
	public void SingleDayOfHistory_HasNoPreviousDayToCompareAgainst()
	{
		var summary = MonitorSummaries.Summarise(MonitorId, Trend((23, 7)))!;

		Assert.Null(summary.CountDelta);
		Assert.Null(summary.PastThresholdDelta);
		Assert.Equal([23], summary.Sparkline);
	}

	[Fact]
	public void TotalDelta_SumsAcrossMonitorsAndSkipsThoseWithoutOne()
	{
		var withDelta = MonitorSummaries.Summarise("a", Trend((10, 1), (14, 3)))!;
		var withoutDelta = MonitorSummaries.Summarise("b", Trend((99, 9)))!;

		Assert.Equal(4, MonitorSummaries.TotalDelta([withDelta, withoutDelta], m => m.CountDelta));
		Assert.Equal(2, MonitorSummaries.TotalDelta([withDelta, withoutDelta], m => m.PastThresholdDelta));
	}

	[Fact]
	public void TotalDelta_IsNullWhenNoMonitorHasOne()
	{
		var withoutDelta = MonitorSummaries.Summarise("b", Trend((99, 9)))!;

		Assert.Null(MonitorSummaries.TotalDelta([withoutDelta], m => m.CountDelta));
		Assert.Null(MonitorSummaries.TotalDelta([], m => m.CountDelta));
	}
}
