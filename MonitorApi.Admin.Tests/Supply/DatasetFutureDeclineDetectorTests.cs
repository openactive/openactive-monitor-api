using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.Supply;

/// <summary>
/// Deterministic tests for the future-supply decline rules, run against hand-written histories.
/// These need no BigQuery credentials and no fixture — every rule the endpoints rely on is pinned here,
/// so the live-data endpoint tests only have to check that the wiring and envelope are right.
/// </summary>
public class DatasetFutureDeclineDetectorTests
{
	private static readonly DateOnly AsOf = new(2026, 9, 1);

	/// <summary>
	/// The production defaults. Taken from the record rather than restated here, so these tests exercise
	/// whatever the API actually serves; <see cref="ProductionDefaults_AreTheDocumentedValues"/> pins the
	/// values themselves.
	/// </summary>
	private static readonly DatasetFutureDeclineThresholds Defaults = new();

	[Fact]
	public void ProductionDefaults_AreTheDocumentedValues()
	{
		Assert.Equal(5, Defaults.WindowDays);
		Assert.Equal(10, Defaults.DropPercent);
		Assert.Equal(10, Defaults.QualifyWindowDays);
		Assert.Equal(10, Defaults.QualifyDropPercent);
		Assert.Equal(25, Defaults.PastThresholdDropPercent);
		Assert.Equal(50, Defaults.MinFutureOpportunities);
		Assert.Equal(30, Defaults.TrendDays);
		Assert.Equal(10, Defaults.IncidentTrendDays);
		Assert.Equal(3, Defaults.MinObservations);
	}

	/// <summary>
	/// A feed with one completed ingestion run per given day: <c>(daysAgo, futureOpportunities)</c>, offset
	/// back from <see cref="AsOf"/>. Days not listed have no completed run at all, which is what a failed
	/// or missed ingestion looks like here.
	/// </summary>
	private static FeedFutureSupplyHistory Feed(
		string feedId,
		string datasetId,
		params (int DaysAgo, long Future)[] rows) =>
		FeedWithActivity(feedId, datasetId, rows.Select(r => (r.DaysAgo, r.Future, 0L, 0L)).ToArray());

	/// <summary>
	/// The same, with the context columns spelled out: <c>(daysAgo, futureOpportunities, updated,
	/// deletes)</c>. Neither of the last two may change what is detected.
	/// </summary>
	private static FeedFutureSupplyHistory FeedWithActivity(
		string feedId,
		string datasetId,
		params (int DaysAgo, long Future, long Updated, long Deletes)[] rows) =>
		new(
			feedId,
			datasetId,
			rows
				.Select(r => new FeedFutureSupplyDay(AsOf.AddDays(-r.DaysAgo), r.Future, r.Updated, r.Deletes))
				.OrderBy(d => d.Day)
				.ToList());

	#region Detection

	[Fact]
	public void FeedHoldingItsSupplySteady_IsNotAnIncident()
	{
		var feeds = new[] { Feed("f1", "d1", (4, 1000), (3, 1000), (2, 1000), (1, 1000), (0, 1000)) };

		Assert.Empty(DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults));
	}

	[Fact]
	public void FeedGrowing_IsNotAnIncident()
	{
		var feeds = new[] { Feed("f1", "d1", (4, 1000), (3, 1100), (2, 1200), (1, 1300), (0, 1400)) };

		Assert.Empty(DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults));
	}

	[Fact]
	public void FeedFallingEveryDayByALittle_IsAMonotonicDeclineOnceItQualifies()
	{
		// One percent a day: no single step comes close to the ten percent trigger, which is exactly the
		// erosion a percentage threshold on its own would never see. Four percent over the window is below
		// the qualifying drop too, so it is the negative delta — sixty removed against ten added — that
		// earns it a place.
		var feeds = new[]
		{
			FeedWithActivity("f1", "d1",
				(4, 1000, 10, 0), (3, 990, 0, 15), (2, 980, 0, 15), (1, 970, 0, 15), (0, 960, 0, 15)),
		};

		var incident = Assert.Single(DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal(FutureDeclineReasons.MonotonicDecline, incident.Reason);
		Assert.Equal(1000, incident.StartTotal);
		Assert.Equal(960, incident.CurrentTotal);
		Assert.Equal(40, incident.Drop);
		Assert.Equal(4.0, incident.DropPercent, precision: 10);
		Assert.True(incident.Feeds[0].LargestDailyDropPercent < Defaults.DropPercent);
		Assert.Equal(-50, incident.Feeds[0].DeltaInWindow);
		Assert.False(incident.PastThreshold);
	}

	[Fact]
	public void FeedLosingALotInOneStep_IsASharpDropEvenWhenTheRestOfTheWindowHolds()
	{
		var feeds = new[] { Feed("f1", "d1", (4, 1000), (3, 1000), (2, 600), (1, 600), (0, 600)) };

		var incident = Assert.Single(DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal(FutureDeclineReasons.SharpDrop, incident.Reason);
		Assert.Equal(400, incident.Drop);
		Assert.Equal(40.0, incident.Feeds[0].LargestDailyDropPercent, precision: 10);
		Assert.True(incident.PastThreshold);
	}

	[Fact]
	public void FeedFallingEveryDayAndSteeply_ReportsBothRules()
	{
		var feeds = new[] { Feed("f1", "d1", (4, 1000), (3, 900), (2, 500), (1, 400), (0, 300)) };

		var incident = Assert.Single(DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal(FutureDeclineReasons.Both, incident.Reason);
	}

	[Fact]
	public void RecoveryInsideTheWindow_ClearsTheMonotonicRuleButNotTheSharpOne()
	{
		// Halved, then clawing its way back: no longer a straight line down, but the cliff still happened.
		var feeds = new[] { Feed("f1", "d1", (4, 1000), (3, 500), (2, 600), (1, 650), (0, 700)) };

		var incident = Assert.Single(DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal(FutureDeclineReasons.SharpDrop, incident.Reason);
		Assert.Equal(300, incident.Drop);
	}

	[Fact]
	public void OneFlatDayBreaksTheMonotonicRule()
	{
		// A repeated figure is not a fall, so a gentle decline with a plateau in it raises nothing unless
		// one of its steps is steep enough on its own.
		var feeds = new[] { Feed("f1", "d1", (4, 1000), (3, 990), (2, 990), (1, 980), (0, 970)) };

		Assert.Empty(DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults));
	}

	[Fact]
	public void DaysWithNoCompletedRun_NeitherBreakARunNorInventADrop()
	{
		// Polled on three of the five days and falling at each of them: still a decline. The two missing
		// days are an outage the stall and error monitors own, not a collapse to zero.
		var declining = Feed("declining", "d1", (4, 1000), (2, 900), (0, 800));
		var steady = Feed("steady", "d2", (4, 1000), (2, 1000), (0, 1000));

		var incident = Assert.Single(DatasetFutureDeclineDetector.Detect([declining, steady], AsOf, Defaults));

		Assert.Equal("d1", incident.DatasetId);
		Assert.Equal(200, incident.Drop);
	}

	[Fact]
	public void FeedWithNoCompletedRunsAtAll_IsInvisibleToThisMonitor()
	{
		// A feed that stopped or is failing outright belongs to the stall and ingestion-error monitors;
		// this one only ever sees COMPLETE runs, so such a feed has no history here at all.
		var feeds = new[] { new FeedFutureSupplyHistory("gone", "d1", []) };

		Assert.Empty(DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults));
	}

	[Fact]
	public void ObservationsOlderThanTheWindow_AreNotCompared()
	{
		// A collapse six days ago is outside a five-day window; the feed has been steady ever since.
		var feeds = new[]
		{
			Feed("f1", "d1", (6, 5000), (5, 4000), (4, 1000), (3, 1000), (2, 1000), (1, 1000), (0, 1000)),
		};

		Assert.Empty(DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults));
	}

	[Fact]
	public void UpdatedAndDeletes_DoNotChangeWhichRuleFires()
	{
		// Identical supply curves, steep enough to qualify on the drop alone: the publishing columns are
		// carried through but have nothing left to decide, and the verdicts match.
		var withActivity = FeedWithActivity("busy", "d1",
			(4, 1000, 30, 5), (3, 900, 20, 40), (2, 800, 10, 30), (1, 700, 0, 25), (0, 600, 0, 20));
		var withoutActivity = Feed("quiet", "d2", (4, 1000), (3, 900), (2, 800), (1, 700), (0, 600));

		var incidents = DatasetFutureDeclineDetector.Detect([withActivity, withoutActivity], AsOf, Defaults);

		Assert.Equal(2, incidents.Count);
		Assert.All(incidents, i => Assert.Equal(400, i.Drop));
		Assert.All(incidents, i => Assert.Equal(FutureDeclineReasons.Both, i.Reason));

		var busy = incidents.Single(i => i.DatasetId == "d1").Feeds[0];
		Assert.Equal(60, busy.UpdatedInWindow);
		Assert.Equal(120, busy.DeletesInWindow);
		Assert.Equal(-60, busy.DeltaInWindow);
	}

	#endregion

	#region Qualifying gate

	[Fact]
	public void ShallowDeclineThatAddsMoreThanItRemoves_DoesNotQualify()
	{
		// Four percent over the window, nothing removed: a real fall, but too small over the longer window
		// to be worth anyone's morning and not backed by the feed shedding stock.
		var feeds = new[]
		{
			FeedWithActivity("f1", "d1",
				(4, 1000, 20, 0), (3, 990, 20, 0), (2, 980, 20, 0), (1, 970, 20, 0), (0, 960, 20, 0)),
		};

		Assert.Empty(DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults));
	}

	[Fact]
	public void SteepDeclineQualifiesOnTheDropHoweverTheFeedPublished()
	{
		// Publishing far more than it removes, and still down forty percent: the drop clause stands alone.
		var feeds = new[]
		{
			FeedWithActivity("f1", "d1",
				(4, 1000, 500, 0), (3, 900, 500, 0), (2, 800, 500, 0), (1, 700, 500, 0), (0, 600, 500, 0)),
		};

		var incident = Assert.Single(DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults));

		Assert.True(incident.Feeds[0].DeltaInWindow > 0);
		Assert.True(incident.QualifyDropPercent >= Defaults.QualifyDropPercent);
	}

	[Fact]
	public void TheQualifyingDropIsMeasuredOverTheLongerWindow()
	{
		// Only 4.4% across the five days the decline was found in, but 14% measured back over ten — which
		// is the whole point of the longer window: a slide that has been going on for a fortnight reads as
		// trivial through a five-day slot.
		var feeds = new[]
		{
			Feed("f1", "d1",
				(9, 1000), (8, 1000), (7, 1000), (6, 1000), (5, 1000),
				(4, 900), (3, 890), (2, 880), (1, 870), (0, 860)),
		};

		var incident = Assert.Single(DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal(900, incident.StartTotal);
		Assert.Equal(1000, incident.QualifyStartTotal);
		Assert.Equal(4.44, incident.DropPercent, precision: 2);
		Assert.Equal(14.0, incident.QualifyDropPercent, precision: 10);
		Assert.Equal(0, incident.Feeds[0].DeltaInWindow);
	}

	[Fact]
	public void TheDeltaIsReadOverTheDetectionWindowNotTheQualifyingOne()
	{
		// Five hundred removed nine days ago, then a fortnight of healthy publishing while supply erodes
		// gently. The old deletions are outside the window the delta covers, so they cannot resurrect a
		// decline that qualifies on neither clause.
		var feeds = new[]
		{
			FeedWithActivity("f1", "d1",
				(9, 1000, 0, 500), (8, 1000, 0, 0), (7, 1000, 0, 0), (6, 1000, 0, 0), (5, 1000, 0, 0),
				(4, 1000, 50, 0), (3, 990, 50, 0), (2, 980, 50, 0), (1, 970, 50, 0), (0, 960, 50, 0)),
		};

		Assert.Empty(DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults));
	}

	[Fact]
	public void TheQualifyingWindowIsNeverShorterThanTheDetectionWindow()
	{
		// Asking for a qualifying window inside the detection window would judge the decline on fewer days
		// than it was found over; it is widened to the detection window instead.
		var thresholds = Defaults with { WindowDays = 5, QualifyWindowDays = 2 };

		Assert.Equal(5, thresholds.EffectiveQualifyWindowDays);

		var feeds = new[] { Feed("f1", "d1", (4, 1000), (3, 900), (2, 800), (1, 700), (0, 600)) };

		var incident = Assert.Single(DatasetFutureDeclineDetector.Detect(feeds, AsOf, thresholds));

		Assert.Equal(incident.StartTotal, incident.QualifyStartTotal);
	}

	[Fact]
	public void LoweringTheQualifyingDrop_NeverReportsFewerDatasets()
	{
		var feeds = new[] { Feed("f1", "d1", (4, 1000), (3, 990), (2, 980), (1, 970), (0, 960)) };

		Assert.Empty(DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults));
		Assert.Single(DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults with { QualifyDropPercent = 2 }));
	}

	#endregion

	#region Thresholds

	[Fact]
	public void FeedBelowTheMinimumSupply_IsIgnored()
	{
		// Falling from forty opportunities to five is a real decline and not worth anybody's morning.
		var feeds = new[] { Feed("tiny", "d1", (4, 40), (3, 30), (2, 20), (1, 10), (0, 5)) };

		Assert.Empty(DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults));
	}

	[Fact]
	public void FeedWithTooFewObservations_IsIgnored()
	{
		// Two points are one step, which says nothing about a trend.
		var feeds = new[] { Feed("f1", "d1", (4, 1000), (0, 500)) };

		Assert.Empty(DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults));
	}

	[Fact]
	public void PastThreshold_IsSetOnlyOnceTheNetDeclineReachesTheEscalationLimit()
	{
		var feeds = new[]
		{
			// 20% net — an open incident, not yet escalated.
			Feed("just-open", "just-open", (4, 1000), (3, 950), (2, 900), (1, 850), (0, 800)),
			// 40% net.
			Feed("escalated", "escalated", (4, 1000), (3, 900), (2, 800), (1, 700), (0, 600)),
		};

		var incidents = DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults)
			.ToDictionary(i => i.DatasetId, i => i.PastThreshold);

		Assert.False(incidents["just-open"]);
		Assert.True(incidents["escalated"]);
	}

	[Fact]
	public void PastThreshold_IsNeverLooserThanTheTrigger()
	{
		// A caller asking for an escalation percentage below the trigger must not be able to make
		// past_threshold_count exceed open_count.
		var thresholds = Defaults with { DropPercent = 30, PastThresholdDropPercent = 10 };

		Assert.Equal(30, thresholds.EffectivePastThresholdPercent);

		// Raised by the monotonic rule, which the trigger percentage has no say over, and 20% down across
		// the window: above the caller's escalation figure, below the trigger it is clamped to.
		var feeds = new[] { Feed("f1", "d1", (4, 1000), (3, 950), (2, 900), (1, 850), (0, 800)) };

		var incident = Assert.Single(DatasetFutureDeclineDetector.Detect(feeds, AsOf, thresholds));

		Assert.False(incident.PastThreshold);
	}

	[Fact]
	public void LoweringTheTrigger_NeverReportsFewerDatasets()
	{
		var feeds = new[]
		{
			// A 5% step: only a low trigger catches it. Its deletes carry it past the qualifying gate, so
			// this test is about drop_percent alone.
			FeedWithActivity("a", "d1",
				(4, 1000, 0, 20), (3, 1000, 0, 20), (2, 950, 0, 20), (1, 950, 0, 20), (0, 950, 0, 20)),
			// A 50% step: caught at either.
			Feed("b", "d2", (4, 1000), (3, 1000), (2, 1000), (1, 1000), (0, 500)),
		};

		var strict = DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults with { DropPercent = 40 });
		var loose = DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults with { DropPercent = 2 });

		Assert.Single(strict);
		Assert.Equal(2, loose.Count);
	}

	[Fact]
	public void AOneDayWindow_ReportsNothingRatherThanEverything()
	{
		// A single day holds at most one observation and therefore no step at all; the minimum never falls
		// below two so that degenerate window cannot flag every feed it sees.
		var thresholds = Defaults with { WindowDays = 1 };
		var feeds = new[] { Feed("f1", "d1", (0, 1000)) };

		Assert.Equal(2, thresholds.MinObservations);
		Assert.Empty(DatasetFutureDeclineDetector.Detect(feeds, AsOf, thresholds));
	}

	#endregion

	#region Dataset rollup

	[Fact]
	public void ADatasetIsReportedOnceWithEveryDecliningFeedNamed()
	{
		var feeds = new[]
		{
			Feed("slots", "d1", (4, 1000), (3, 900), (2, 800), (1, 700), (0, 600)),
			Feed("sessions", "d1", (4, 500), (3, 450), (2, 400), (1, 350), (0, 300)),
		};

		var incident = Assert.Single(DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal(2, incident.Feeds.Count);
		// Largest loss first, so the feed an operator should look at is the one they read first.
		Assert.Equal(["slots", "sessions"], incident.Feeds.Select(f => f.FeedId));
		Assert.Equal(1500, incident.StartTotal);
		Assert.Equal(900, incident.CurrentTotal);
		Assert.Equal(600, incident.Drop);
		Assert.Equal(40.0, incident.DropPercent, precision: 10);
	}

	[Fact]
	public void DatasetTotalsCoverOnlyTheFeedsThatRaisedTheIncident()
	{
		// The healthy feed's supply is not the incident's to report: folding it in would bury a feed that
		// has lost everything inside a dataset that looks barely changed.
		var feeds = new[]
		{
			Feed("draining", "d1", (4, 1000), (3, 900), (2, 800), (1, 700), (0, 600)),
			Feed("healthy", "d1", (4, 10000), (3, 10000), (2, 10000), (1, 10000), (0, 10000)),
		};

		var incident = Assert.Single(DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal(["draining"], incident.Feeds.Select(f => f.FeedId));
		Assert.Equal(1000, incident.StartTotal);
		Assert.Equal(600, incident.CurrentTotal);
	}

	[Fact]
	public void ADatasetWhoseFeedsFiredDifferentRules_ReportsBoth()
	{
		var feeds = new[]
		{
			FeedWithActivity("eroding", "d1",
				(4, 1000, 0, 10), (3, 990, 0, 10), (2, 980, 0, 10), (1, 970, 0, 10), (0, 960, 0, 10)),
			Feed("cliff", "d1", (4, 1000), (3, 1000), (2, 500), (1, 500), (0, 500)),
		};

		var incident = Assert.Single(DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal(FutureDeclineReasons.Both, incident.Reason);
		Assert.Equal(
			[FutureDeclineReasons.SharpDrop, FutureDeclineReasons.MonotonicDecline],
			incident.Feeds.Select(f => f.Reason));
	}

	[Fact]
	public void DatasetsAreEvaluatedIndependently()
	{
		var feeds = new[]
		{
			Feed("a", "draining", (4, 1000), (3, 900), (2, 800), (1, 700), (0, 600)),
			Feed("b", "healthy", (4, 1000), (3, 1000), (2, 1000), (1, 1000), (0, 1000)),
		};

		var incident = Assert.Single(DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal("draining", incident.DatasetId);
	}

	[Fact]
	public void DeclineStartDatesTheIncidentFromWhereTheFallBegan()
	{
		// Steady for two days, then falling for the last two: the incident began two days ago, not five.
		var feeds = new[] { Feed("f1", "d1", (4, 1000), (3, 1000), (2, 1000), (1, 900), (0, 810)) };

		var incident = Assert.Single(DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal(AsOf.AddDays(-2), incident.DeclineStart);
		Assert.Equal(2, incident.ConsecutiveDays);
	}

	[Fact]
	public void AWindowEndingFlat_IsDatedFromTheDropThatRaisedIt()
	{
		var feeds = new[] { Feed("f1", "d1", (4, 1000), (3, 1000), (2, 600), (1, 600), (0, 600)) };

		var incident = Assert.Single(DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults));

		Assert.Equal(AsOf.AddDays(-3), incident.DeclineStart);
		Assert.Equal(3, incident.ConsecutiveDays);
	}

	#endregion

	#region Ordering and per-incident trend

	[Fact]
	public void IncidentsAreOrderedLargestLossFirst()
	{
		var feeds = new[]
		{
			Feed("small", "small", (4, 1000), (3, 900), (2, 800), (1, 700), (0, 600)),
			Feed("biggest", "biggest", (4, 90000), (3, 80000), (2, 70000), (1, 60000), (0, 50000)),
			Feed("middle", "middle", (4, 9000), (3, 8000), (2, 7000), (1, 6000), (0, 5000)),
		};

		var incidents = DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults);

		Assert.Equal(["biggest", "middle", "small"], incidents.Select(i => i.DatasetId));
	}

	[Fact]
	public void DatasetsLosingTheSameAmount_AreBrokenTiedOrdinally()
	{
		var feeds = new[]
		{
			Feed("f1", "zebra", (4, 1000), (3, 900), (2, 800), (1, 700), (0, 600)),
			Feed("f2", "aardvark", (4, 1000), (3, 900), (2, 800), (1, 700), (0, 600)),
		};

		var incidents = DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults);

		Assert.Equal(["aardvark", "zebra"], incidents.Select(i => i.DatasetId));
	}

	[Fact]
	public void IncidentTrend_SumsTheContributingFeedsPerDayOldestFirst()
	{
		var feeds = new[]
		{
			Feed("a", "d1", (9, 1000), (8, 900), (4, 800), (3, 700), (2, 600), (1, 500), (0, 400)),
			Feed("b", "d1", (9, 100), (4, 80), (3, 70), (2, 60), (1, 50), (0, 40)),
		};

		var incident = Assert.Single(DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults));

		// Day -8 has only feed a; days -7 to -5 have neither, so nothing is known about them at all.
		long?[] expected = [1100, 900, null, null, null, 880, 770, 660, 550, 440];

		Assert.Equal(10, incident.Trend.Count);
		Assert.Equal(expected, incident.Trend);
	}

	[Fact]
	public void IncidentTrend_CoversTheWholeWindowIncludingDaysBeforeTheDeclineBegan()
	{
		// The supply the dataset used to carry is the point of the column, so the healthy days stay in.
		var feeds = new[]
		{
			Feed("f1", "d1",
				(9, 1000), (8, 1000), (7, 1000), (6, 1000), (5, 1000),
				(4, 1000), (3, 900), (2, 800), (1, 700), (0, 600)),
		};

		var incident = Assert.Single(DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults));

		long?[] expected = [1000, 1000, 1000, 1000, 1000, 1000, 900, 800, 700, 600];

		Assert.Equal(expected, incident.Trend);
	}

	[Fact]
	public void IncidentTrend_LengthFollowsIncidentTrendDays()
	{
		var feeds = new[] { Feed("f1", "d1", (4, 1000), (3, 900), (2, 800), (1, 700), (0, 600)) };

		var incident = Assert.Single(
			DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults with { IncidentTrendDays = 3 }));

		Assert.Equal(3, incident.Trend.Count);
	}

	#endregion

	#region Trend series

	[Fact]
	public void Trend_ReturnsOneContiguousPointPerDayOldestFirst()
	{
		var feeds = new[] { Feed("f1", "d1", (4, 1000), (3, 900), (2, 800), (1, 700), (0, 600)) };

		var trend = DatasetFutureDeclineDetector.Trend(feeds, AsOf, Defaults);

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
			Feed("a", "d1", (4, 1000), (3, 900), (2, 800), (1, 700), (0, 600)),
			Feed("b", "d2", (4, 1000), (3, 990), (2, 980), (1, 970), (0, 960)),
			Feed("c", "d3", (4, 1000), (3, 1000), (2, 1000), (1, 1000), (0, 1000)),
		};

		var incidents = DatasetFutureDeclineDetector.Detect(feeds, AsOf, Defaults);
		var trend = DatasetFutureDeclineDetector.Trend(feeds, AsOf, Defaults);

		Assert.Equal(incidents.Count, trend[^1].OpenCount);
		Assert.Equal(incidents.Count(i => i.PastThreshold), trend[^1].PastThresholdCount);
	}

	[Fact]
	public void Trend_TracksADeclineOpeningAndClosing()
	{
		// Falls for four days, then recovers and holds: open while the fall is inside the window, closed
		// once the recovery has pushed it out.
		var feeds = new[]
		{
			Feed("f1", "d1",
				(9, 1000), (8, 900), (7, 800), (6, 700), (5, 600),
				(4, 2000), (3, 2000), (2, 2000), (1, 2000), (0, 2000)),
		};

		var trend = DatasetFutureDeclineDetector.Trend(feeds, AsOf, Defaults with { TrendDays = 10 })
			.ToDictionary(p => p.Date, p => p.OpenCount);

		Assert.Equal(1, trend[AsOf.AddDays(-5)]);
		Assert.Equal(0, trend[AsOf.AddDays(-1)]);
		Assert.Equal(0, trend[AsOf]);
	}

	[Fact]
	public void Trend_PastThresholdCountIsAlwaysASubsetOfOpenCount()
	{
		var feeds = new[]
		{
			Feed("a", "d1", (4, 1000), (3, 900), (2, 800), (1, 700), (0, 600)),
			Feed("b", "d2", (4, 1000), (3, 990), (2, 980), (1, 970), (0, 960)),
		};

		var trend = DatasetFutureDeclineDetector.Trend(feeds, AsOf, Defaults);

		Assert.All(trend, p => Assert.True(p.PastThresholdCount <= p.OpenCount));
	}

	#endregion
}
