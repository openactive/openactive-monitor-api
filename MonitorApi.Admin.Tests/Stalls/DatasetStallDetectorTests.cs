using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.Stalls;

/// <summary>
/// Deterministic tests for the dataset-wide stall rules, run against hand-written histories.
/// These need no BigQuery credentials and no fixture — every rule the endpoints rely on is pinned here,
/// so the live-data endpoint tests only have to check that the wiring and envelope are right.
/// </summary>
/// <remarks>
/// <see cref="TheTwoStallMonitorsPartitionTheSameSilence"/> is the one that matters most: this monitor
/// exists because <see cref="SingleFeedStallDetector"/> excludes fully silent datasets, and the two
/// only add up while they agree on what silence is.
/// </remarks>
public class DatasetStallDetectorTests
{
	private static readonly DateOnly AsOf = new(2026, 9, 1);

	/// <summary>
	/// The production defaults. Taken from the record rather than restated here, so these tests exercise
	/// whatever the API actually serves; <see cref="ProductionDefaults_AreTheDocumentedValues"/> pins the
	/// values themselves.
	/// </summary>
	private static readonly DatasetStallThresholds Defaults = new();

	[Fact]
	public void ProductionDefaults_AreTheDocumentedValues()
	{
		Assert.Equal(120, Defaults.LookbackDays);
		Assert.Equal(5, Defaults.StallDays);
		Assert.Equal(7, Defaults.PastThresholdDays);
		Assert.Equal(30, Defaults.TrendDays);
		Assert.Equal(10, Defaults.IncidentTrendDays);
	}

	[Fact]
	public void ProductionDefaults_MatchTheSingleFeedMonitorsWhereTheyOverlap()
	{
		// The two monitors partition the same signal, and the partition is only exact while their
		// definitions of silence agree.
		var singleFeed = new SingleFeedStallThresholds();

		Assert.Equal(singleFeed.StallDays, Defaults.StallDays);
		Assert.Equal(singleFeed.LookbackDays, Defaults.LookbackDays);
		Assert.Equal(singleFeed.PastThresholdDays, Defaults.PastThresholdDays);
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
	public void DatasetWithEveryFeedSilent_IsAnIncident()
	{
		var feeds = new[]
		{
			Feed("a", "dataset-down", 9),
			Feed("b", "dataset-down", 9),
			Feed("c", "dataset-down", 9),
		};

		var incident = Assert.Single(DatasetStallDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal("dataset-down", incident.DatasetId);
		Assert.Equal(9, incident.ConsecutiveDays);
		Assert.Equal(AsOf.AddDays(-9), incident.LastPublished);
		Assert.Equal(3, incident.Feeds.Count);
		Assert.True(incident.PastThreshold);
	}

	[Fact]
	public void DatasetWithOneStillPublishingFeed_IsNotAnIncident()
	{
		// The single-feed monitor reports the two silent feeds; the dataset itself is still publishing.
		var feeds = new[]
		{
			Feed("a", "dataset-partial", 9),
			Feed("b", "dataset-partial", 9),
			Feed("still-publishing", "dataset-partial", 0),
		};

		Assert.Empty(DatasetStallDetector.Detect(feeds, AsOf, Defaults));
	}

	[Fact]
	public void DatasetSilentBelowThreshold_IsNotAnIncident()
	{
		// Its most recent feed published four days ago; the threshold is five.
		var feeds = new[] { Feed("a", "d1", 4), Feed("b", "d1", 30) };

		Assert.Empty(DatasetStallDetector.Detect(feeds, AsOf, Defaults));
	}

	[Fact]
	public void DatasetSilentExactlyAtThreshold_IsAnIncident()
	{
		var feeds = new[] { Feed("a", "d1", 5), Feed("b", "d1", 30) };

		var incident = Assert.Single(DatasetStallDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal(5, incident.ConsecutiveDays);
		Assert.False(incident.PastThreshold);
	}

	[Fact]
	public void DatasetGoesQuietWhenItsLastRemainingFeedDoes()
	{
		// One feed stopped a month ago, the other only nine days ago: the dataset has been dark for nine.
		var feeds = new[] { Feed("stopped-first", "d1", 30), Feed("stopped-last", "d1", 9) };

		var incident = Assert.Single(DatasetStallDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal(9, incident.ConsecutiveDays);
		Assert.Equal(AsOf.AddDays(-9), incident.LastPublished);
		// Most recently active first, so the feed that dates the incident leads the list.
		Assert.Equal(["stopped-last", "stopped-first"], incident.Feeds.Select(f => f.FeedId));
		Assert.Equal([9, 30], incident.Feeds.Select(f => f.ConsecutiveDays));
	}

	[Fact]
	public void DatasetThatNeverPublished_IsNotAnIncident()
	{
		var feeds = new[]
		{
			new FeedIngestionHistory("a", "never-seen", []),
			new FeedIngestionHistory("b", "never-seen", []),
		};

		Assert.Empty(DatasetStallDetector.Detect(feeds, AsOf, Defaults));
	}

	[Fact]
	public void DatasetSilentBeyondLookbackWindow_IsTreatedAsRetired()
	{
		var feeds = new[] { Feed("a", "d1", 200), Feed("b", "d1", 300) };

		Assert.Empty(DatasetStallDetector.Detect(feeds, AsOf, Defaults));
	}

	[Fact]
	public void FeedThatNeverPublished_DoesNotStopTheDatasetBeingReported()
	{
		// A never-seen feed says nothing about when the dataset was last live, but it is still one of the
		// feeds the incident accounts for, and it sorts last because it has no last publishing day.
		var feeds = new[]
		{
			Feed("stopped", "d1", 9),
			new FeedIngestionHistory("never", "d1", []),
		};

		var incident = Assert.Single(DatasetStallDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal(9, incident.ConsecutiveDays);
		Assert.Equal(["stopped", "never"], incident.Feeds.Select(f => f.FeedId));
		Assert.Null(incident.Feeds[^1].LastPublished);
		Assert.Null(incident.Feeds[^1].ConsecutiveDays);
	}

	[Fact]
	public void MissingIngestionDays_ExtendSilenceRatherThanBreakIt()
	{
		// Nothing was ingested for this dataset between day 10 and day 0 — a gap is not a publish.
		var feeds = new[] { Feed("a", "d1", 10, 11), Feed("b", "d1", 12) };

		var incident = Assert.Single(DatasetStallDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal(10, incident.ConsecutiveDays);
	}

	[Fact]
	public void AnyFeedPublishingAgain_ClosesTheIncident()
	{
		var feeds = new[] { Feed("a", "d1", 1, 30), Feed("b", "d1", 30) };

		Assert.Empty(DatasetStallDetector.Detect(feeds, AsOf, Defaults));
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
			Feed("healthy", "dataset-healthy", 0),
		};

		var incident = Assert.Single(DatasetStallDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal("dataset-down", incident.DatasetId);
	}

	#endregion

	#region Partition with the single-feed monitor

	[Fact]
	public void TheTwoStallMonitorsPartitionTheSameSilence()
	{
		var feeds = new[]
		{
			Feed("down-a", "dataset-down", 9),
			Feed("down-b", "dataset-down", 9),
			Feed("partial-stalled", "dataset-partial", 9),
			Feed("partial-healthy", "dataset-partial", 0),
			Feed("healthy", "dataset-healthy", 0),
			new FeedIngestionHistory("never", "dataset-never-seen", []),
			Feed("retired", "dataset-retired", 200),
		};

		var datasetStalls = DatasetStallDetector.Detect(feeds, AsOf, Defaults);
		var singleFeedStalls = SingleFeedStallDetector.Detect(feeds, AsOf, new SingleFeedStallThresholds());

		// The fully silent dataset is reported once, as a dataset; its feeds are not reported again.
		Assert.Equal(["dataset-down"], datasetStalls.Select(i => i.DatasetId));
		Assert.Equal(["partial-stalled"], singleFeedStalls.Select(i => i.FeedId));

		var datasetStallFeeds = datasetStalls.SelectMany(i => i.Feeds).Select(f => f.FeedId);
		Assert.Empty(datasetStallFeeds.Intersect(singleFeedStalls.Select(i => i.FeedId)));

		// And neither monitor claims a dataset the other one has.
		Assert.Empty(datasetStalls.Select(i => i.DatasetId).Intersect(singleFeedStalls.Select(i => i.DatasetId)));
	}

	#endregion

	#region Thresholds

	[Fact]
	public void PastThreshold_IsSetOnlyOnceTheEscalationLimitIsReached()
	{
		var feeds = new[]
		{
			Feed("a", "just-open", 6),
			Feed("b", "escalated", 7),
		};

		var incidents = DatasetStallDetector.Detect(feeds, AsOf, Defaults)
			.ToDictionary(i => i.DatasetId, i => i.PastThreshold);

		Assert.False(incidents["just-open"]);
		Assert.True(incidents["escalated"]);
	}

	[Fact]
	public void PastThreshold_IsNeverLooserThanTheStallThreshold()
	{
		// A caller asking for a past-threshold shorter than the stall threshold must not be able to make
		// past_threshold_count exceed open_count.
		var thresholds = Defaults with { StallDays = 5, PastThresholdDays = 2 };
		var feeds = new[] { Feed("a", "d1", 5) };

		var incident = Assert.Single(DatasetStallDetector.Detect(feeds, AsOf, thresholds));

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
			Feed("a", "short", 5),
			Feed("b", "longest", 30),
			Feed("c", "middle", 12),
		};

		var incidents = DatasetStallDetector.Detect(feeds, AsOf, Defaults);

		Assert.Equal(["longest", "middle", "short"], incidents.Select(i => i.DatasetId));
	}

	[Fact]
	public void IncidentTrend_SumsTheDatasetsFeedsPerDayOldestFirst()
	{
		var feeds = new[]
		{
			FeedWithUpdated("a", "d1", (9, 400), (8, 300), (5, 50), (4, 0), (0, 0)),
			FeedWithUpdated("b", "d1", (9, 100), (8, 200), (5, 25), (4, 0), (0, 0)),
		};

		var incident = Assert.Single(DatasetStallDetector.Detect(feeds, AsOf, Defaults));

		long?[] expected = [500, 500, null, null, 75, 0, null, null, null, 0];

		Assert.Equal(10, incident.Trend.Count);
		Assert.Equal(expected, incident.Trend);
	}

	[Fact]
	public void IncidentTrend_DistinguishesADayNothingWasPolledFromADayNothingWasPublished()
	{
		// Day 3: one feed was polled and published nothing. Day 2: neither feed has an ingestion row.
		var feeds = new[]
		{
			FeedWithUpdated("a", "d1", (5, 50), (3, 0)),
			FeedWithUpdated("b", "d1", (5, 10), (1, 0)),
		};

		var incident = Assert.Single(DatasetStallDetector.Detect(feeds, AsOf, Defaults));

		long?[] expected = [null, null, null, null, 60, null, 0, null, 0, null];

		Assert.Equal(expected, incident.Trend);
	}

	[Fact]
	public void IncidentTrend_IsTheSameLengthAndAlignmentForEveryIncident()
	{
		var feeds = new[]
		{
			FeedWithUpdated("a", "long-running", (22, 10), (1, 0), (0, 0)),
			FeedWithUpdated("b", "just-opened", (5, 7), (4, 0), (0, 0)),
		};

		var incidents = DatasetStallDetector.Detect(feeds, AsOf, Defaults);

		// Oldest first and ending at the snapshot, so a day N days ago sits at index (length - 1 - N)
		// in every incident's array regardless of how old the incident is.
		int IndexOf(int daysAgo) => Defaults.IncidentTrendDays - 1 - daysAgo;

		Assert.Equal(2, incidents.Count);
		Assert.All(incidents, incident => Assert.Equal(Defaults.IncidentTrendDays, incident.Trend.Count));

		var longRunning = incidents.Single(i => i.DatasetId == "long-running");
		var justOpened = incidents.Single(i => i.DatasetId == "just-opened");

		Assert.Equal(0, longRunning.Trend[IndexOf(0)]);
		Assert.Equal(7, justOpened.Trend[IndexOf(5)]);
		// The long-running incident had no ingestion row that day at all.
		Assert.Null(longRunning.Trend[IndexOf(5)]);
	}

	[Fact]
	public void IncidentTrend_LengthFollowsIncidentTrendDays()
	{
		var feeds = new[] { FeedWithUpdated("a", "d1", (9, 5), (5, 0), (0, 0)) };

		var incident = Assert.Single(
			DatasetStallDetector.Detect(feeds, AsOf, Defaults with { IncidentTrendDays = 3 }));

		Assert.Equal(3, incident.Trend.Count);
	}

	[Fact]
	public void IncidentTrend_IsAllNullWhenNoRecentCountsWereLoaded()
	{
		// The trend endpoint builds histories without the recent counts; detection must still work.
		var feeds = new[] { new FeedIngestionHistory("a", "d1", [AsOf.AddDays(-9)]) };

		var incident = Assert.Single(DatasetStallDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal(10, incident.Trend.Count);
		Assert.All(incident.Trend, Assert.Null);
	}

	#endregion

	#region Trend series

	[Fact]
	public void Trend_ReturnsOneContiguousPointPerDayOldestFirst()
	{
		var feeds = new[] { Feed("a", "d1", 9) };

		var trend = DatasetStallDetector.Trend(feeds, AsOf, Defaults with { TrendDays = 30 });

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
			Feed("a", "down-recent", 6),
			Feed("b", "down-long", 20),
			Feed("c", "healthy", 0),
		};

		var incidents = DatasetStallDetector.Detect(feeds, AsOf, Defaults);
		var trend = DatasetStallDetector.Trend(feeds, AsOf, Defaults);

		Assert.Equal(incidents.Count, trend[^1].OpenCount);
		Assert.Equal(incidents.Count(i => i.PastThreshold), trend[^1].PastThresholdCount);
	}

	[Fact]
	public void Trend_TracksADatasetGoingDarkAndComingBack()
	{
		// The dataset's feeds last published 10 days ago; one of them published again 2 days ago.
		var feeds = new[] { Feed("a", "d1", 10), Feed("b", "d1", 2, 10) };

		var trend = DatasetStallDetector.Trend(feeds, AsOf, Defaults with { TrendDays = 11 })
			.ToDictionary(p => p.Date, p => p.OpenCount);

		Assert.Equal(0, trend[AsOf.AddDays(-6)]);  // silent 4 days, below threshold
		Assert.Equal(1, trend[AsOf.AddDays(-5)]);  // silent 5 days, opens
		Assert.Equal(1, trend[AsOf.AddDays(-3)]);  // silent 7 days, still open
		Assert.Equal(0, trend[AsOf.AddDays(-2)]);  // one feed published again, closed
		Assert.Equal(0, trend[AsOf]);
	}

	[Fact]
	public void Trend_PastThresholdCountIsAlwaysASubsetOfOpenCount()
	{
		var feeds = new[]
		{
			Feed("a", "d1", 6),
			Feed("b", "d2", 20),
			Feed("c", "d3", 40),
			Feed("d", "d4", 0),
		};

		var trend = DatasetStallDetector.Trend(feeds, AsOf, Defaults);

		Assert.All(trend, p => Assert.True(p.PastThresholdCount <= p.OpenCount));
	}

	[Fact]
	public void Trend_CountsDatasetsRatherThanFeeds()
	{
		// Six silent feeds, one dark dataset.
		var feeds = Enumerable.Range(0, 6).Select(i => Feed($"f{i}", "d1", 9)).ToArray();

		var trend = DatasetStallDetector.Trend(feeds, AsOf, Defaults with { TrendDays = 1 });

		Assert.Equal(1, Assert.Single(trend).OpenCount);
	}

	#endregion
}
