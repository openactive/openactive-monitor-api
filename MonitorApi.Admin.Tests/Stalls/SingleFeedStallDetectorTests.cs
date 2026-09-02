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

	private static readonly SingleFeedStallThresholds Defaults = new()
	{
		LookbackDays = 120,
		StallDays = 5,
		PastThresholdDays = 14,
		TrendDays = 30,
		IncidentTrendDays = 7,
	};

	/// <summary>A feed that published on each of the given days, offset back from <see cref="AsOf"/>.</summary>
	private static FeedIngestionHistory Feed(string feedId, string datasetId, params int[] daysAgo) =>
		new(feedId, datasetId, daysAgo.Select(d => AsOf.AddDays(-d)).OrderBy(d => d).ToList());

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
			Feed("just-open", "d1", 13),
			Feed("escalated", "d1", 14),
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
	public void IncidentTrend_IsTheTrailingSilentDayCountsEndingAtToday()
	{
		var feeds = new[] { Feed("f1", "d1", 22), Feed("healthy", "d1", 0) };

		var incident = Assert.Single(SingleFeedStallDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal([16, 17, 18, 19, 20, 21, 22], incident.Trend);
		Assert.Equal(incident.ConsecutiveDays, incident.Trend[^1]);
	}

	[Fact]
	public void IncidentTrend_SkipsDaysBeforeTheIncidentOpened()
	{
		// Silent for six days, so only the days at or past the five-day threshold appear.
		var feeds = new[] { Feed("f1", "d1", 6), Feed("healthy", "d1", 0) };

		var incident = Assert.Single(SingleFeedStallDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal([5, 6], incident.Trend);
	}

	[Fact]
	public void IncidentTrend_ExcludesAnEarlierSilenceThatAlreadyRecovered()
	{
		// Published 6 days ago (ending a long earlier silence) and again 5 days ago, then went quiet.
		// The trend must describe only the current 5-day silence, not the earlier closed one.
		var feeds = new[] { Feed("f1", "d1", 5, 6, 20), Feed("healthy", "d1", 0) };

		var incident = Assert.Single(SingleFeedStallDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal(5, incident.ConsecutiveDays);
		Assert.Equal([5], incident.Trend);
	}

	[Fact]
	public void IncidentTrend_IsAlwaysStrictlyIncreasing()
	{
		var feeds = new[]
		{
			Feed("recovered-then-stalled", "d1", 5, 6, 20),
			Feed("long-running", "d1", 22),
			Feed("just-opened", "d1", 5),
			Feed("healthy", "d1", 0),
		};

		var incidents = SingleFeedStallDetector.Detect(feeds, AsOf, Defaults);

		Assert.NotEmpty(incidents);
		Assert.All(incidents, incident =>
		{
			Assert.NotEmpty(incident.Trend);
			Assert.Equal(incident.Trend.Order(), incident.Trend);
			Assert.Equal(incident.Trend.Distinct(), incident.Trend);
			Assert.Equal(incident.ConsecutiveDays, incident.Trend[^1]);
		});
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
