using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.Errors;

/// <summary>
/// Deterministic tests for the feed ingestion error rules, run against hand-written histories.
/// These need no BigQuery credentials and no fixture — every rule the endpoints rely on is pinned here,
/// so the live-data endpoint tests only have to check that the wiring and envelope are right.
/// </summary>
public class FeedIngestionErrorDetectorTests
{
	private static readonly DateOnly AsOf = new(2026, 9, 9);

	/// <summary>
	/// The production defaults. Taken from the record rather than restated here, so these tests exercise
	/// whatever the API actually serves; <see cref="ProductionDefaults_AreTheDocumentedValues"/> pins the
	/// values themselves.
	/// </summary>
	private static readonly FeedIngestionErrorThresholds Defaults = new();

	[Fact]
	public void ProductionDefaults_AreTheDocumentedValues()
	{
		Assert.Equal(15, Defaults.SuccessLookbackDays);
		Assert.Equal(1, Defaults.ErrorDays);
		Assert.Equal(3, Defaults.PastThresholdDays);
		Assert.Equal(30, Defaults.TrendDays);
		Assert.Equal(10, Defaults.IncidentTrendDays);
	}

	/// <summary>
	/// A feed with one ingestion day per entry, given as <c>(daysAgo, status)</c> offset back from
	/// <see cref="AsOf"/>. Days not listed have no ingestion run at all. Failing days carry no
	/// <c>error_code</c>, as every day before the column was added does.
	/// </summary>
	private static FeedStatusHistory Feed(string feedId, string datasetId, params (int DaysAgo, string Status)[] days) =>
		new(
			feedId,
			datasetId,
			days.Select(d => new FeedIngestionDay(AsOf.AddDays(-d.DaysAgo), d.Status)).OrderBy(d => d.Day).ToList());

	/// <summary>The same, with a failure code and message on the given day.</summary>
	private static FeedStatusHistory FeedFailingWith(
		string feedId,
		string errorCode,
		params (int DaysAgo, string Status)[] days) =>
		new(
			feedId,
			"d1",
			days
				.Select(d => d.Status == FeedIngestionDay.Error
					? new FeedIngestionDay(AsOf.AddDays(-d.DaysAgo), d.Status, errorCode, $"HTTP {errorCode} fetching …")
					: new FeedIngestionDay(AsOf.AddDays(-d.DaysAgo), d.Status))
				.OrderBy(d => d.Day)
				.ToList());

	private const string Complete = FeedIngestionDay.Complete;
	private const string Error = FeedIngestionDay.Error;
	private const string Warning = "warning";

	#region Detection

	[Fact]
	public void FeedCompletingToday_IsNotAnIncident()
	{
		var feeds = new[] { Feed("f1", "d1", (0, Complete), (1, Complete), (2, Error)) };

		Assert.Empty(FeedIngestionErrorDetector.Detect(feeds, AsOf, Defaults));
	}

	[Fact]
	public void FeedFailingTodayAfterCompletingYesterday_IsAnIncident()
	{
		var feeds = new[] { Feed("f1", "d1", (0, Error), (1, Complete), (2, Complete)) };

		var incident = Assert.Single(FeedIngestionErrorDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal("f1", incident.FeedId);
		Assert.Equal("d1", incident.DatasetId);
		Assert.Equal(AsOf.AddDays(-1), incident.LastCompleted);
		Assert.Equal(1, incident.ConsecutiveDays);
		Assert.False(incident.PastThreshold);
	}

	[Fact]
	public void FeedWarningToday_IsNotAnIncident()
	{
		// WARNING is a successful ingestion that reported something odd, not a failure.
		var feeds = new[] { Feed("f1", "d1", (0, Warning), (1, Complete)) };

		Assert.Empty(FeedIngestionErrorDetector.Detect(feeds, AsOf, Defaults));
	}

	[Fact]
	public void FeedWithNoIngestionRunToday_IsNotAnIncident()
	{
		// Absence of a run is not evidence of failure — that silence is the stall monitor's business.
		var feeds = new[] { Feed("f1", "d1", (1, Error), (2, Complete)) };

		Assert.Empty(FeedIngestionErrorDetector.Detect(feeds, AsOf, Defaults));
	}

	[Fact]
	public void FeedThatNeverCompleted_IsIgnoredAsPermanentlyBroken()
	{
		var feeds = new[] { Feed("always-broken", "d1", (0, Error), (1, Error), (2, Error)) };

		Assert.Empty(FeedIngestionErrorDetector.Detect(feeds, AsOf, Defaults));
	}

	[Fact]
	public void FeedWhoseLastSuccessIsOutsideTheLookback_IsIgnoredAsPermanentlyBroken()
	{
		// Completed 16 days ago, one day beyond the 15-day success lookback.
		var feeds = new[] { Feed("f1", "d1", (0, Error), (16, Complete)) };

		Assert.Empty(FeedIngestionErrorDetector.Detect(feeds, AsOf, Defaults));
	}

	[Fact]
	public void FeedWhoseLastSuccessIsAtTheEdgeOfTheLookback_IsStillAnIncident()
	{
		var feeds = new[] { Feed("f1", "d1", (0, Error), (15, Complete)) };

		var incident = Assert.Single(FeedIngestionErrorDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal(15, incident.ConsecutiveDays);
		Assert.True(incident.PastThreshold);
	}

	[Fact]
	public void ADayWithBothACompletedAndAFailedRun_CountsAsCompleted()
	{
		// The query collapses a day's runs with success winning, so the detector only ever sees one entry
		// per day; this pins the consequence for a feed polled twice today.
		var feeds = new[] { Feed("f1", "d1", (0, Complete), (1, Error), (2, Complete)) };

		Assert.Empty(FeedIngestionErrorDetector.Detect(feeds, AsOf, Defaults));
	}

	[Fact]
	public void MissingIngestionDaysBetweenSuccessAndFailure_CountTowardsDaysFailing()
	{
		// Nothing ran between day 5 and day 0, so all that can be said is the feed has not completed for
		// five days.
		var feeds = new[] { Feed("f1", "d1", (0, Error), (5, Complete)) };

		var incident = Assert.Single(FeedIngestionErrorDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal(5, incident.ConsecutiveDays);
		Assert.Equal(AsOf.AddDays(-5), incident.LastCompleted);
	}

	[Fact]
	public void RecoveringThenFailingAgain_MeasuresFromTheMostRecentSuccess()
	{
		var feeds = new[]
		{
			Feed("f1", "d1", (0, Error), (1, Error), (2, Complete), (3, Error), (4, Error), (5, Complete)),
		};

		var incident = Assert.Single(FeedIngestionErrorDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal(2, incident.ConsecutiveDays);
		Assert.Equal(AsOf.AddDays(-2), incident.LastCompleted);
	}

	[Fact]
	public void EveryFeedInADatasetFailing_IsStillReportedFeedByFeed()
	{
		// Unlike the stall monitor there is no dataset-wide exclusion: a publisher whose whole estate
		// errors on one day is exactly what this monitor is for.
		var feeds = new[]
		{
			Feed("a", "dataset-down", (0, Error), (1, Complete)),
			Feed("b", "dataset-down", (0, Error), (1, Complete)),
		};

		var incidents = FeedIngestionErrorDetector.Detect(feeds, AsOf, Defaults);

		Assert.Equal(["a", "b"], incidents.Select(i => i.FeedId).Order());
	}

	#endregion

	#region Authorisation failures

	[Theory]
	[InlineData("401")]
	[InlineData("403")]
	public void AuthorisationFailures_AreLeftToTheirOwnMonitor(string errorCode)
	{
		var feeds = new[] { FeedFailingWith("f1", errorCode, (0, Error), (1, Complete)) };

		Assert.Empty(FeedIngestionErrorDetector.Detect(feeds, AsOf, Defaults));
	}

	[Theory]
	[InlineData("500")]
	[InlineData("404")]
	[InlineData("BATCH_FAILED")]
	[InlineData("CONNECTION_ERROR")]
	[InlineData("MISSING_ITEMS")]
	public void OtherFailureCodes_AreReportedWithTheirCodeAndMessage(string errorCode)
	{
		var feeds = new[] { FeedFailingWith("f1", errorCode, (0, Error), (1, Complete)) };

		var incident = Assert.Single(FeedIngestionErrorDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal(errorCode, incident.ErrorCode);
		Assert.Equal($"HTTP {errorCode} fetching …", incident.ErrorMessage);
	}

	[Fact]
	public void AFailureWithNoCodeAtAll_IsStillReported()
	{
		// error_code was added to the table recently, so older failures carry none. They are ingestion
		// errors until something says otherwise.
		var feeds = new[] { Feed("f1", "d1", (0, Error), (1, Complete)) };

		var incident = Assert.Single(FeedIngestionErrorDetector.Detect(feeds, AsOf, Defaults));

		Assert.Null(incident.ErrorCode);
		Assert.Null(incident.ErrorMessage);
	}

	[Fact]
	public void AnAuthorisationFailureOnAnEarlierDay_DoesNotExcludeTodaysOtherFailure()
	{
		// The exclusion looks only at the day being evaluated: yesterday's 403 says nothing about today's
		// HTTP 500.
		var feed = new FeedStatusHistory("f1", "d1",
		[
			new FeedIngestionDay(AsOf.AddDays(-2), Complete),
			new FeedIngestionDay(AsOf.AddDays(-1), Error, "403", "HTTP 403 fetching …"),
			new FeedIngestionDay(AsOf, Error, "500", "HTTP 500 fetching …"),
		]);

		var incident = Assert.Single(FeedIngestionErrorDetector.Detect([feed], AsOf, Defaults));

		Assert.Equal("500", incident.ErrorCode);
		Assert.Equal(2, incident.ConsecutiveDays);
	}

	[Fact]
	public void AuthErrorCodes_AreTheDocumentedCodesAndAreMatchedCaseInsensitively()
	{
		Assert.Equal(["401", "403"], FeedIngestionErrorDetector.AuthErrorCodes.Order());
		Assert.Contains("401", FeedIngestionErrorDetector.AuthErrorCodes);
		Assert.DoesNotContain("500", FeedIngestionErrorDetector.AuthErrorCodes);
	}

	#endregion

	#region Thresholds

	[Fact]
	public void PastThreshold_IsSetOnlyOnceTheEscalationLimitIsReached()
	{
		var feeds = new[]
		{
			Feed("just-open", "d1", (0, Error), (1, Error), (2, Complete)),
			Feed("escalated", "d1", (0, Error), (1, Error), (2, Error), (3, Complete)),
		};

		var incidents = FeedIngestionErrorDetector.Detect(feeds, AsOf, Defaults)
			.ToDictionary(i => i.FeedId, i => i.PastThreshold);

		Assert.False(incidents["just-open"]);
		Assert.True(incidents["escalated"]);
	}

	[Fact]
	public void ErrorDays_HoldsAnIncidentBackUntilTheFailureHasRunThatLong()
	{
		var thresholds = Defaults with { ErrorDays = 3 };
		var feeds = new[]
		{
			Feed("one-day", "d1", (0, Error), (1, Complete)),
			Feed("three-days", "d1", (0, Error), (1, Error), (2, Error), (3, Complete)),
		};

		var incident = Assert.Single(FeedIngestionErrorDetector.Detect(feeds, AsOf, thresholds));

		Assert.Equal("three-days", incident.FeedId);
	}

	[Fact]
	public void PastThreshold_IsNeverLooserThanTheFailureThreshold()
	{
		// A caller asking for a past-threshold shorter than error_days must not be able to make
		// past_threshold_count exceed open_count.
		var thresholds = Defaults with { ErrorDays = 5, PastThresholdDays = 2 };
		var feeds = new[] { Feed("f1", "d1", (0, Error), (5, Complete)) };

		var incident = Assert.Single(FeedIngestionErrorDetector.Detect(feeds, AsOf, thresholds));

		Assert.True(incident.PastThreshold);
		Assert.Equal(5, thresholds.EffectivePastThresholdDays);
	}

	[Fact]
	public void SuccessLookbackShorterThanTheFailureThreshold_ReportsNothing()
	{
		// A feed cannot both have completed within two days and have been failing for five.
		var thresholds = Defaults with { SuccessLookbackDays = 2, ErrorDays = 5 };
		var feeds = new[] { Feed("f1", "d1", (0, Error), (1, Error), (5, Complete)) };

		Assert.Empty(FeedIngestionErrorDetector.Detect(feeds, AsOf, thresholds));
	}

	[Fact]
	public void RequiredHistory_CoversEveryDayTheTrendEvaluates()
	{
		var thresholds = Defaults with { TrendDays = 30, SuccessLookbackDays = 15, IncidentTrendDays = 10 };

		// The oldest trend point sits 29 days back and needs 15 days of history behind it.
		Assert.Equal(15, thresholds.IncidentHistoryDays);
		Assert.Equal(44, thresholds.RequiredHistoryDays);
	}

	#endregion

	#region Ordering and per-incident trend

	[Fact]
	public void IncidentsAreOrderedLongestFailingFirst()
	{
		var feeds = new[]
		{
			Feed("short", "d1", (0, Error), (1, Complete)),
			Feed("longest", "d1", (0, Error), (12, Complete)),
			Feed("middle", "d1", (0, Error), (4, Complete)),
		};

		var incidents = FeedIngestionErrorDetector.Detect(feeds, AsOf, Defaults);

		Assert.Equal(["longest", "middle", "short"], incidents.Select(i => i.FeedId));
	}

	[Fact]
	public void IncidentTrend_FlagsTheFailingDaysOneDayPerEntryOldestFirst()
	{
		var feeds = new[]
		{
			Feed("f1", "d1",
				(9, Complete), (8, Complete), (7, Warning), (6, Complete), (5, Complete),
				(4, Complete), (3, Error), (2, Error), (1, Error), (0, Error)),
		};

		var incident = Assert.Single(FeedIngestionErrorDetector.Detect(feeds, AsOf, Defaults));

		// A warning is a successful ingestion, so it reads as 0 alongside the completed days.
		Assert.Equal(10, incident.Trend.Count);
		Assert.Equal([0, 0, 0, 0, 0, 0, 1, 1, 1, 1], incident.Trend);
		Assert.Equal(4, incident.ConsecutiveDays);
	}

	[Fact]
	public void IncidentTrend_CountsADayWithNoIngestionRunAsNotFailing()
	{
		// Days 9 to 4 and day 1 had no run at all; the series answers "did this feed fail that day", and
		// an absent run did not.
		var feeds = new[] { Feed("f1", "d1", (3, Complete), (2, Error), (0, Error)) };

		var incident = Assert.Single(FeedIngestionErrorDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal([0, 0, 0, 0, 0, 0, 0, 1, 0, 1], incident.Trend);
	}

	[Fact]
	public void IncidentTrend_IsTheSameLengthAndAlignmentForEveryIncident()
	{
		var feeds = new[]
		{
			Feed("long-running", "d1", (12, Complete), (0, Error)),
			Feed("just-opened", "d1", (1, Complete), (0, Error)),
		};

		var incidents = FeedIngestionErrorDetector.Detect(feeds, AsOf, Defaults);

		// Oldest first and ending at the snapshot, so a day N days ago sits at index (length - 1 - N)
		// in every incident's array regardless of how old the incident is.
		int IndexOf(int daysAgo) => Defaults.IncidentTrendDays - 1 - daysAgo;

		Assert.Equal(2, incidents.Count);
		Assert.All(incidents, incident => Assert.Equal(Defaults.IncidentTrendDays, incident.Trend.Count));

		var longRunning = incidents.Single(i => i.FeedId == "long-running");
		var justOpened = incidents.Single(i => i.FeedId == "just-opened");

		Assert.Equal(1, longRunning.Trend[IndexOf(0)]);
		Assert.Equal(1, justOpened.Trend[IndexOf(0)]);
		// Completed the day before, so not failing then.
		Assert.Equal(0, justOpened.Trend[IndexOf(1)]);
		// The long-running incident had no run at all that day, which also reads as 0.
		Assert.Equal(0, longRunning.Trend[IndexOf(1)]);
	}

	[Fact]
	public void IncidentTrend_LengthFollowsIncidentTrendDays()
	{
		var feeds = new[] { Feed("f1", "d1", (2, Complete), (1, Error), (0, Error)) };

		var incident = Assert.Single(
			FeedIngestionErrorDetector.Detect(feeds, AsOf, Defaults with { IncidentTrendDays = 3 }));

		Assert.Equal(3, incident.Trend.Count);
	}

	#endregion

	#region Trend series

	[Fact]
	public void Trend_ReturnsOneContiguousPointPerDayOldestFirst()
	{
		var feeds = new[] { Feed("f1", "d1", (0, Error), (1, Complete)) };

		var trend = FeedIngestionErrorDetector.Trend(feeds, AsOf, Defaults);

		Assert.Equal(30, trend.Count);
		Assert.Equal(AsOf, trend[^1].Date);
		Assert.Equal(AsOf.AddDays(-29), trend[0].Date);
		Assert.Equal(trend.Select(p => p.Date).Order(), trend.Select(p => p.Date));
	}

	[Fact]
	public void Trend_FinalPointMatchesTheIncidentsReportedForTheSameDay()
	{
		var feeds = new[]
		{
			Feed("a", "d1", (0, Error), (1, Complete)),
			Feed("b", "d1", (0, Error), (4, Complete)),
			Feed("always-broken", "d1", (0, Error)),
			Feed("healthy", "d1", (0, Complete)),
		};

		var incidents = FeedIngestionErrorDetector.Detect(feeds, AsOf, Defaults);
		var trend = FeedIngestionErrorDetector.Trend(feeds, AsOf, Defaults);

		Assert.Equal(2, incidents.Count);
		Assert.Equal(incidents.Count, trend[^1].OpenCount);
		Assert.Equal(incidents.Count(i => i.PastThreshold), trend[^1].PastThresholdCount);
	}

	[Fact]
	public void Trend_TracksAnIncidentOpeningAndClosing()
	{
		// Completed 6 days ago, failed for the four days after that, completed again 2 days ago.
		var feeds = new[]
		{
			Feed("f1", "d1",
				(6, Complete), (5, Error), (4, Error), (3, Error), (2, Complete), (1, Complete), (0, Complete)),
		};

		var trend = FeedIngestionErrorDetector.Trend(feeds, AsOf, Defaults with { TrendDays = 7 })
			.ToDictionary(p => p.Date, p => p.OpenCount);

		Assert.Equal(0, trend[AsOf.AddDays(-6)]);  // completed
		Assert.Equal(1, trend[AsOf.AddDays(-5)]);  // first failing day, opens
		Assert.Equal(1, trend[AsOf.AddDays(-3)]);  // still failing
		Assert.Equal(0, trend[AsOf.AddDays(-2)]);  // completed again, closed
		Assert.Equal(0, trend[AsOf]);
	}

	[Fact]
	public void Trend_PastThresholdCountIsAlwaysASubsetOfOpenCount()
	{
		var feeds = new[]
		{
			Feed("a", "d1", (0, Error), (1, Error), (2, Complete)),
			Feed("b", "d1", (0, Error), (6, Complete)),
			Feed("c", "d1", (0, Error), (14, Complete)),
		};

		var trend = FeedIngestionErrorDetector.Trend(feeds, AsOf, Defaults);

		Assert.All(trend, p => Assert.True(p.PastThresholdCount <= p.OpenCount));
	}

	[Fact]
	public void Trend_AppliesTheAuthorisationExclusionOnlyToDaysThatCarryACode()
	{
		// The same 403 failure every day, but only today's row carries the code — the shape of the live
		// table, where error_code exists only for recent days.
		var feed = new FeedStatusHistory("f1", "d1",
		[
			new FeedIngestionDay(AsOf.AddDays(-2), Complete),
			new FeedIngestionDay(AsOf.AddDays(-1), Error),
			new FeedIngestionDay(AsOf, Error, "403", "HTTP 403 fetching …"),
		]);

		var trend = FeedIngestionErrorDetector.Trend([feed], AsOf, Defaults with { TrendDays = 3 });

		Assert.Equal(0, trend[0].OpenCount);  // completed that day
		Assert.Equal(1, trend[1].OpenCount);  // failing, no code to exclude it by
		Assert.Equal(0, trend[2].OpenCount);  // failing with 403, excluded
	}

	#endregion
}
