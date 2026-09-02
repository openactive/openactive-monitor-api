using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.Stalls;

/// <summary>
/// Deterministic tests for the single-feed stall rules, run against hand-written histories.
/// These need no BigQuery credentials and no fixture — every rule the endpoints rely on is pinned here,
/// so the live-data endpoint tests only have to check that the wiring and envelope are right.
/// </summary>
public class SingleFeedStallDetectorTests
{
	private static readonly DateOnly AsOf = new(2026, 9, 1);

	/// <summary>
	/// The production defaults. Taken from the record rather than restated here, so these tests exercise
	/// whatever the API actually serves; <see cref="ProductionDefaults_AreTheDocumentedValues"/> pins the
	/// values themselves.
	/// </summary>
	private static readonly SingleFeedStallThresholds Defaults = new();

	[Fact]
	public void ProductionDefaults_AreTheDocumentedValues()
	{
		Assert.Equal(120, Defaults.LookbackDays);
		Assert.Equal(5, Defaults.StallDays);
		Assert.Equal(7, Defaults.PastThresholdDays);
		Assert.Equal(30, Defaults.TrendDays);
		Assert.Equal(10, Defaults.IncidentTrendDays);
	}

	/// <summary>
	/// A feed that published on each of the given days, offset back from <see cref="AsOf"/>. The only
	/// ingestion rows it is given are those publishing days, each with an updated count of one, so its
	/// history is self-consistent.
	/// </summary>
	private static FeedIngestionHistory Feed(string feedId, string datasetId, params int[] daysAgo) =>
		FeedWithUpdated(feedId, datasetId, daysAgo.Select(d => (d, 1L)).ToArray());

	/// <summary>
	/// A feed with an explicit ingestion row per given day: <c>(daysAgo, updated)</c>. Days not listed
	/// have no ingestion row at all, which is distinct from a listed day whose updated count is zero.
	/// </summary>
	private static FeedIngestionHistory FeedWithUpdated(
		string feedId,
		string datasetId,
		params (int DaysAgo, long Updated)[] rows)
	{
		var updatedByDay = rows.ToDictionary(r => AsOf.AddDays(-r.DaysAgo), r => r.Updated);

		return new FeedIngestionHistory(
			feedId,
			datasetId,
			updatedByDay.Where(kv => kv.Value > 0).Select(kv => kv.Key).Order().ToList(),
			updatedByDay);
	}

	#region Detection

	[Fact]
	public void FeedPublishingToday_IsNotAnIncident()
	{
		var feeds = new[] { Feed("f1", "d1", 0, 1, 2, 3) };

		Assert.Empty(SingleFeedStallDetector.Detect(feeds, AsOf, Defaults));
	}

	[Fact]
	public void FeedSilentBelowThreshold_IsNotAnIncident()
	{
		// Last published four days ago; the threshold is five.
		var feeds = new[] { Feed("f1", "d1", 4, 5, 6), Feed("healthy", "d1", 0) };

		Assert.Empty(SingleFeedStallDetector.Detect(feeds, AsOf, Defaults));
	}

	[Fact]
	public void FeedSilentExactlyAtThreshold_IsAnIncident()
	{
		var feeds = new[] { Feed("f1", "d1", 5, 6, 7), Feed("healthy", "d1", 0) };

		var incident = Assert.Single(SingleFeedStallDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal("f1", incident.FeedId);
		Assert.Equal(5, incident.ConsecutiveDays);
		Assert.Equal(AsOf.AddDays(-5), incident.LastPublished);
		Assert.False(incident.PastThreshold);
	}

	[Fact]
	public void FeedSilentBeyondLookbackWindow_IsTreatedAsRetired()
	{
		// Last published 200 days ago, outside the 120-day lookback: not a stall, just gone.
		var feeds = new[] { Feed("f1", "d1", 200), Feed("healthy", "d1", 0) };

		Assert.Empty(SingleFeedStallDetector.Detect(feeds, AsOf, Defaults));
	}

	[Fact]
	public void FeedThatNeverPublished_IsNotAnIncident()
	{
		var feeds = new[]
		{
			new FeedIngestionHistory("never", "d1", []),
			Feed("healthy", "d1", 0),
		};

		Assert.Empty(SingleFeedStallDetector.Detect(feeds, AsOf, Defaults));
	}

	[Fact]
	public void MissingIngestionDays_ExtendSilenceRatherThanBreakIt()
	{
		// Nothing was ingested for this feed between day 10 and day 0 — a gap is not a publish.
		var feeds = new[] { Feed("f1", "d1", 10, 11, 12), Feed("healthy", "d1", 0) };

		var incident = Assert.Single(SingleFeedStallDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal(10, incident.ConsecutiveDays);
	}

	[Fact]
	public void PublishingAgainAfterASilence_ClosesTheIncident()
	{
		var feeds = new[] { Feed("f1", "d1", 1, 30, 31), Feed("healthy", "d1", 0) };

		Assert.Empty(SingleFeedStallDetector.Detect(feeds, AsOf, Defaults));
	}

	#endregion

	#region Dataset-wide exclusion

	[Fact]
	public void DatasetWithEveryFeedSilent_IsExcludedAsADatasetWideOutage()
	{
		var feeds = new[]
		{
			Feed("a", "dataset-down", 9),
			Feed("b", "dataset-down", 9),
			Feed("c", "dataset-down", 9),
		};

		Assert.Empty(SingleFeedStallDetector.Detect(feeds, AsOf, Defaults));
	}

	[Fact]
	public void DatasetWithOneStillPublishingFeed_ReportsItsSilentSiblings()
	{
		var feeds = new[]
		{
			Feed("a", "dataset-partial", 9),
			Feed("b", "dataset-partial", 9),
			Feed("still-publishing", "dataset-partial", 0),
		};

		var incidents = SingleFeedStallDetector.Detect(feeds, AsOf, Defaults);

		Assert.Equal(["a", "b"], incidents.Select(i => i.FeedId).Order());
	}

	[Fact]
	public void DatasetIsFullySilent_EvenWhenSomeFeedsNeverPublished()
	{
		// A feed that never published still counts as "not publishing" for the dataset-wide check,
		// otherwise a dead dataset containing one never-seen feed would leak through as single-feed stalls.
		var feeds = new[]
		{
			Feed("a", "dataset-down", 9),
			new FeedIngestionHistory("never", "dataset-down", []),
		};

		Assert.Empty(SingleFeedStallDetector.Detect(feeds, AsOf, Defaults));
	}

	[Fact]
	public void DatasetsAreEvaluatedIndependently()
	{
		var feeds = new[]
		{
			Feed("down-a", "dataset-down", 9),
			Feed("down-b", "dataset-down", 9),
			Feed("partial-stalled", "dataset-partial", 9),
			Feed("partial-healthy", "dataset-partial", 0),
		};

		var incident = Assert.Single(SingleFeedStallDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal("partial-stalled", incident.FeedId);
	}

	#endregion

	#region Thresholds

	[Fact]
	public void PastThreshold_IsSetOnlyOnceTheEscalationLimitIsReached()
	{
		var feeds = new[]
		{
			Feed("just-open", "d1", 6),
			Feed("escalated", "d1", 7),
			Feed("healthy", "d1", 0),
		};

		var incidents = SingleFeedStallDetector.Detect(feeds, AsOf, Defaults)
			.ToDictionary(i => i.FeedId, i => i.PastThreshold);

		Assert.False(incidents["just-open"]);
		Assert.True(incidents["escalated"]);
	}

	[Fact]
	public void PastThreshold_IsNeverLooserThanTheStallThreshold()
	{
		// A caller asking for a past-threshold shorter than the stall threshold must not be able to make
		// past_threshold_count exceed open_count.
		var thresholds = Defaults with { StallDays = 5, PastThresholdDays = 2 };
		var feeds = new[] { Feed("f1", "d1", 5), Feed("healthy", "d1", 0) };

		var incident = Assert.Single(SingleFeedStallDetector.Detect(feeds, AsOf, thresholds));

		Assert.True(incident.PastThreshold);
		Assert.Equal(5, thresholds.EffectivePastThresholdDays);
	}

	#endregion

	#region Ordering and per-incident trend

	[Fact]
	public void IncidentsAreOrderedLongestRunningFirst()
	{
		var feeds = new[]
		{
			Feed("short", "d1", 5),
			Feed("longest", "d1", 30),
			Feed("middle", "d1", 12),
			Feed("healthy", "d1", 0),
		};

		var incidents = SingleFeedStallDetector.Detect(feeds, AsOf, Defaults);

		Assert.Equal(["longest", "middle", "short"], incidents.Select(i => i.FeedId));
	}

	[Fact]
	public void IncidentTrend_ReportsOneDailyUpdatedCountPerTrendDayOldestFirst()
	{
		// Active until five days ago, then polled every day but publishing nothing.
		var feed = FeedWithUpdated("f1", "d1",
			(9, 400), (8, 300), (7, 200), (6, 100), (5, 50),
			(4, 0), (3, 0), (2, 0), (1, 0), (0, 0));

		var incident = Assert.Single(SingleFeedStallDetector.Detect([feed, Feed("healthy", "d1", 0)], AsOf, Defaults));

		long?[] expected = [400, 300, 200, 100, 50, 0, 0, 0, 0, 0];

		Assert.Equal(10, incident.Trend.Count);
		Assert.Equal(expected, incident.Trend);
	}

	[Fact]
	public void IncidentTrend_DistinguishesADayWithNoIngestionRunFromADayThatPublishedNothing()
	{
		// Day 3 was polled and published nothing; day 2 has no ingestion row at all.
		var feed = FeedWithUpdated("f1", "d1", (5, 50), (4, 0), (3, 0), (1, 0), (0, 0));

		var incident = Assert.Single(SingleFeedStallDetector.Detect([feed, Feed("healthy", "d1", 0)], AsOf, Defaults));

		long?[] expected = [null, null, null, null, 50, 0, 0, null, 0, 0];

		Assert.Equal(expected, incident.Trend);
	}

	[Fact]
	public void IncidentTrend_CoversTheWholeWindowIncludingDaysBeforeTheIncidentOpened()
	{
		// The pre-stall activity is the point of the column, so it is not filtered out — unlike the
		// silent-day series this replaced, which only started once the incident was open.
		var feed = FeedWithUpdated("f1", "d1", (6, 999), (5, 0), (4, 0), (3, 0), (2, 0), (1, 0), (0, 0));

		var incident = Assert.Single(SingleFeedStallDetector.Detect([feed, Feed("healthy", "d1", 0)], AsOf, Defaults));

		Assert.Equal(6, incident.ConsecutiveDays);
		Assert.Equal(999, incident.Trend[3]);
		Assert.All(incident.Trend.Skip(4), value => Assert.Equal(0, value));
	}

	[Fact]
	public void IncidentTrend_IsTheSameLengthAndAlignmentForEveryIncident()
	{
		var feeds = new[]
		{
			FeedWithUpdated("long-running", "d1", (22, 10), (1, 0), (0, 0)),
			FeedWithUpdated("just-opened", "d1", (5, 7), (4, 0), (0, 0)),
			Feed("healthy", "d1", 0),
		};

		var incidents = SingleFeedStallDetector.Detect(feeds, AsOf, Defaults);

		// Oldest first and ending at the snapshot, so a day N days ago sits at index (length - 1 - N)
		// in every incident's array regardless of how old the incident is.
		int IndexOf(int daysAgo) => Defaults.IncidentTrendDays - 1 - daysAgo;

		Assert.Equal(2, incidents.Count);
		Assert.All(incidents, incident => Assert.Equal(Defaults.IncidentTrendDays, incident.Trend.Count));

		var longRunning = incidents.Single(i => i.FeedId == "long-running");
		var justOpened = incidents.Single(i => i.FeedId == "just-opened");

		Assert.Equal(0, longRunning.Trend[IndexOf(0)]);
		Assert.Equal(0, justOpened.Trend[IndexOf(0)]);
		Assert.Equal(7, justOpened.Trend[IndexOf(5)]);
		// The long-running incident had no ingestion row that day at all.
		Assert.Null(longRunning.Trend[IndexOf(5)]);
	}

	[Fact]
	public void IncidentTrend_LengthFollowsIncidentTrendDays()
	{
		var feed = FeedWithUpdated("f1", "d1", (9, 5), (5, 0), (0, 0));

		var incident = Assert.Single(
			SingleFeedStallDetector.Detect([feed, Feed("healthy", "d1", 0)], AsOf, Defaults with { IncidentTrendDays = 3 }));

		Assert.Equal(3, incident.Trend.Count);
	}

	[Fact]
	public void IncidentTrend_IsAllNullWhenNoRecentCountsWereLoaded()
	{
		// The trend endpoint builds histories without the recent counts; detection must still work.
		var feeds = new[]
		{
			new FeedIngestionHistory("f1", "d1", [AsOf.AddDays(-9)]),
			Feed("healthy", "d1", 0),
		};

		var incident = Assert.Single(SingleFeedStallDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal(10, incident.Trend.Count);
		Assert.All(incident.Trend, Assert.Null);
	}

	#endregion

	#region Trend series

	[Fact]
	public void Trend_ReturnsOneContiguousPointPerDayOldestFirst()
	{
		var feeds = new[] { Feed("f1", "d1", 9), Feed("healthy", "d1", 0) };

		var trend = SingleFeedStallDetector.Trend(feeds, AsOf, Defaults with { TrendDays = 30 });

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
			Feed("a", "d1", 6),
			Feed("b", "d1", 20),
			Feed("healthy", "d1", 0),
		};

		var incidents = SingleFeedStallDetector.Detect(feeds, AsOf, Defaults);
		var trend = SingleFeedStallDetector.Trend(feeds, AsOf, Defaults);

		Assert.Equal(incidents.Count, trend[^1].OpenCount);
		Assert.Equal(incidents.Count(i => i.PastThreshold), trend[^1].PastThresholdCount);
	}

	[Fact]
	public void Trend_TracksAnIncidentOpeningAndClosing()
	{
		// Published 10 days ago and again 2 days ago: open on the days in between, closed at the end.
		var feeds = new[] { Feed("f1", "d1", 2, 10), Feed("healthy", "d1", 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10) };

		var trend = SingleFeedStallDetector.Trend(feeds, AsOf, Defaults with { TrendDays = 11 })
			.ToDictionary(p => p.Date, p => p.OpenCount);

		Assert.Equal(0, trend[AsOf.AddDays(-6)]);  // silent 4 days, below threshold
		Assert.Equal(1, trend[AsOf.AddDays(-5)]);  // silent 5 days, opens
		Assert.Equal(1, trend[AsOf.AddDays(-3)]);  // silent 7 days, still open
		Assert.Equal(0, trend[AsOf.AddDays(-2)]);  // published again, closed
		Assert.Equal(0, trend[AsOf]);
	}

	[Fact]
	public void Trend_PastThresholdCountIsAlwaysASubsetOfOpenCount()
	{
		var feeds = new[]
		{
			Feed("a", "d1", 6),
			Feed("b", "d1", 20),
			Feed("c", "d1", 40),
			Feed("healthy", "d1", 0),
		};

		var trend = SingleFeedStallDetector.Trend(feeds, AsOf, Defaults);

		Assert.All(trend, p => Assert.True(p.PastThresholdCount <= p.OpenCount));
	}

	[Fact]
	public void Trend_ExcludesDatasetWideOutagesOnEachDayIndependently()
	{
		var feeds = new[]
		{
			Feed("a", "dataset-down", 9),
			Feed("b", "dataset-down", 9),
		};

		var trend = SingleFeedStallDetector.Trend(feeds, AsOf, Defaults);

		Assert.All(trend, p => Assert.Equal(0, p.OpenCount));
	}

	#endregion
}
