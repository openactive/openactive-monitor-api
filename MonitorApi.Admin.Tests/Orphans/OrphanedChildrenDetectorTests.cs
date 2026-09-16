using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.Orphans;

/// <summary>
/// Deterministic tests for the orphaned-children rules, run against hand-written per-kind counts.
/// These need no BigQuery credentials and no fixture — every rule the endpoint relies on is pinned
/// here, so the live-data endpoint tests only have to check that the wiring and envelope are right.
/// </summary>
public class OrphanedChildrenDetectorTests
{
	/// <summary>
	/// The production defaults. Taken from the record rather than restated here, so these tests
	/// exercise whatever the API actually serves; <see cref="ProductionDefaults_AreTheDocumentedValues"/>
	/// pins the values themselves.
	/// </summary>
	private static readonly OrphanedChildrenThresholds Defaults = new();

	/// <summary>
	/// Both opening gates wide open: one orphan is enough and no share is required. Used by every test
	/// that is about folding, share arithmetic or ordering rather than about the gates themselves, so
	/// those tests keep their small, readable inputs and do not silently become tests of
	/// <c>min_orphans</c>. The gates have their own region below, which uses <see cref="Defaults"/>.
	/// </summary>
	private static readonly OrphanedChildrenThresholds Open =
		new() { MinOrphans = 1, MinShare = 0, PastThresholdOrphans = 100 };

	private const string Slot = "Slot";
	private const string Session = "ScheduledSession";

	/// <summary>
	/// One dataset's counts for one kind. By default every child is examined and the orphans sit behind
	/// a single missing parent — the shape a wide lookback over a healthy-ish dataset produces.
	/// </summary>
	private static DatasetKindOrphanCounts Row(
		string url,
		string kind,
		long children,
		long orphans,
		long? examined = null,
		long? missingParents = null,
		IReadOnlyList<MissingParent>? missing = null) =>
		new(
			url,
			kind,
			children,
			examined ?? children,
			orphans,
			missingParents ?? (orphans > 0 ? 1 : 0),
			missing ?? (orphans > 0 ? [new MissingParent(kind + "-parent", orphans)] : []));

	#region Defaults

	[Fact]
	public void ProductionDefaults_AreTheDocumentedValues()
	{
		Assert.Equal(1_000, Defaults.MinOrphans);
		Assert.Equal(0.10, Defaults.MinShare, precision: 10);
		Assert.Equal(10_000, Defaults.PastThresholdOrphans);
	}

	[Fact]
	public void ChildKinds_AreTheDocumentedKinds() =>
		Assert.Equal([Slot, Session], OrphanedChildrenDetector.ChildKinds);

	#endregion

	#region Detection

	[Fact]
	public void DatasetWithNoOrphans_IsNotAnIncident()
	{
		var rows = new[] { Row("d1", Slot, children: 100, orphans: 0) };

		Assert.Empty(OrphanedChildrenDetector.Detect(rows, Open));
	}

	[Fact]
	public void ASingleOrphan_OpensAnIncidentOnceTheGatesAreOpened()
	{
		var rows = new[] { Row("d1", Slot, children: 100, orphans: 1) };

		var incident = Assert.Single(OrphanedChildrenDetector.Detect(rows, Open));

		Assert.Equal("d1", incident.DatasetUrl);
		Assert.Equal(1, incident.OrphanCount);
		Assert.Equal(0.01, incident.OrphanShare, precision: 10);
		Assert.False(incident.PastThreshold);
	}

	[Fact]
	public void ADatasetWithNoUrl_IsDropped()
	{
		var rows = new[]
		{
			Row("", Slot, children: 100, orphans: 10),
			Row("   ", Slot, children: 100, orphans: 10),
		};

		Assert.Empty(OrphanedChildrenDetector.Detect(rows, Open));
	}

	[Fact]
	public void ADuplicateDatasetKindRow_IsCountedOnce()
	{
		// The query groups by (dataset_url, kind) so this cannot happen from BigQuery; it guards a
		// hand-built input against silently doubling a dataset's counts.
		var rows = new[]
		{
			Row("d1", Slot, children: 100, orphans: 10),
			Row("d1", Slot, children: 100, orphans: 10),
		};

		var incident = Assert.Single(OrphanedChildrenDetector.Detect(rows, Open));

		Assert.Equal(10, incident.OrphanCount);
		Assert.Equal(100, incident.ChildCount);
		Assert.Single(incident.ByKind);
	}

	#endregion

	#region Folding the kinds together

	[Fact]
	public void BothKinds_FoldIntoOneIncidentWhoseCountsAreTheSums()
	{
		var rows = new[]
		{
			Row("d1", Slot, children: 100, orphans: 10),
			Row("d1", Session, children: 300, orphans: 30),
		};

		var incident = Assert.Single(OrphanedChildrenDetector.Detect(rows, Open));

		Assert.Equal(400, incident.ChildCount);
		Assert.Equal(400, incident.CheckedCount);
		Assert.Equal(40, incident.OrphanCount);
		Assert.Equal(2, incident.MissingParentCount);
		Assert.Equal(0.1, incident.OrphanShare, precision: 10);
		Assert.Equal(2, incident.ByKind.Count);
	}

	[Fact]
	public void ByKind_IsOrderedWorstKindFirst()
	{
		var rows = new[]
		{
			Row("d1", Slot, children: 100, orphans: 10),
			Row("d1", Session, children: 300, orphans: 30),
		};

		var incident = Assert.Single(OrphanedChildrenDetector.Detect(rows, Open));

		Assert.Equal([Session, Slot], incident.ByKind.Select(k => k.Kind));
	}

	[Fact]
	public void ADatasetPublishingOneKindOnly_StillFolds()
	{
		var rows = new[] { Row("d1", Session, children: 50, orphans: 5) };

		var incident = Assert.Single(OrphanedChildrenDetector.Detect(rows, Open));

		var kind = Assert.Single(incident.ByKind);
		Assert.Equal(Session, kind.Kind);
		Assert.Equal(5, incident.OrphanCount);
	}

	[Fact]
	public void KindsAreFoldedBeforeTheThresholdIsApplied()
	{
		// Neither kind reaches 100 on its own; the dataset does. The gate is on the folded total,
		// because a publisher fixes the dataset, not one kind of it.
		var thresholds = Open with { MinOrphans = 100 };
		var rows = new[]
		{
			Row("d1", Slot, children: 1000, orphans: 50),
			Row("d1", Session, children: 1000, orphans: 50),
		};

		var incident = Assert.Single(OrphanedChildrenDetector.Detect(rows, thresholds));

		Assert.Equal(100, incident.OrphanCount);
	}

	[Fact]
	public void MissingParents_AreMergedAcrossKindsWorstFirstAndTruncated()
	{
		var rows = new[]
		{
			Row("d1", Slot, children: 1000, orphans: 90, missingParents: 3, missing:
			[
				new MissingParent("p-a", 50), new MissingParent("p-b", 30), new MissingParent("p-c", 10),
			]),
			Row("d1", Session, children: 1000, orphans: 66, missingParents: 4, missing:
			[
				new MissingParent("q-a", 40), new MissingParent("q-b", 20),
				new MissingParent("q-c", 5), new MissingParent("q-d", 1),
			]),
		};

		var incident = Assert.Single(OrphanedChildrenDetector.Detect(rows, Open));

		// Seven candidates across two kinds, re-truncated to the sample size so the evidence is the
		// dataset's worst offenders rather than one kind's.
		Assert.Equal(OrphanedChildrenDetector.MissingParentSample, incident.MissingParents.Count);
		Assert.Equal(
			["p-a", "q-a", "p-b", "q-b", "p-c"],
			incident.MissingParents.Select(p => p.MissingId));
		Assert.Equal(7, incident.MissingParentCount);
	}

	[Fact]
	public void MissingParentSamplesWithEqualChildCounts_AreOrderedByIdSoTheyAreStable()
	{
		var rows = new[]
		{
			Row("d1", Slot, children: 100, orphans: 20, missingParents: 2, missing:
				[new MissingParent("zzz", 10), new MissingParent("aaa", 10)]),
		};

		var incident = Assert.Single(OrphanedChildrenDetector.Detect(rows, Open));

		Assert.Equal(["aaa", "zzz"], incident.MissingParents.Select(p => p.MissingId));
	}

	#endregion

	#region Thresholds

	[Fact]
	public void MinOrphans_HoldsAnIncidentBackUntilTheCountReachesIt()
	{
		var thresholds = Open with { MinOrphans = 100 };
		var rows = new[]
		{
			Row("under", Slot, children: 1000, orphans: 99),
			Row("at", Slot, children: 1000, orphans: 100),
		};

		var incident = Assert.Single(OrphanedChildrenDetector.Detect(rows, thresholds));

		Assert.Equal("at", incident.DatasetUrl);
	}

	[Fact]
	public void PastThreshold_IsSetOnlyOnceTheOrphanCountReachesTheLimit()
	{
		// Both clear the opening gates — 20% of a 50,000-child dataset — so only the escalation limit
		// separates them.
		var rows = new[]
		{
			Row("just-open", Slot, children: 50_000, orphans: 9_999),
			Row("escalated", Slot, children: 50_000, orphans: 10_000),
		};

		var flags = OrphanedChildrenDetector.Detect(rows, Defaults)
			.ToDictionary(i => i.DatasetUrl, i => i.PastThreshold);

		Assert.False(flags["just-open"]);
		Assert.True(flags["escalated"]);
	}

	[Fact]
	public void MinShare_HoldsBackADatasetWhoseOrphansAreATinyFractionOfIt()
	{
		// Both clear min_orphans comfortably. Only the share separates them, and the boundary is
		// inclusive: exactly a tenth is reported.
		var rows = new[]
		{
			Row("thin", Slot, children: 100_000, orphans: 9_999),
			Row("thick", Slot, children: 100_000, orphans: 10_000),
		};

		var incident = Assert.Single(OrphanedChildrenDetector.Detect(rows, Defaults));

		Assert.Equal("thick", incident.DatasetUrl);
		Assert.Equal(0.10, incident.OrphanShare, precision: 10);
	}

	[Fact]
	public void TheTwoOpeningGatesAreAnded_NotOred()
	{
		// "wholly-broken" is 99.9% orphaned but too small to matter; "barely-dented" has orphans to
		// spare but they are a thousandth of what it publishes. Neither is an incident on its own terms.
		var rows = new[]
		{
			Row("wholly-broken", Slot, children: 1000, orphans: 999),
			Row("barely-dented", Slot, children: 1_000_000, orphans: 1_000),
			Row("both", Slot, children: 10_000, orphans: 1_000),
		};

		var incident = Assert.Single(OrphanedChildrenDetector.Detect(rows, Defaults));

		Assert.Equal("both", incident.DatasetUrl);
	}

	[Fact]
	public void MinShareOfZero_TurnsTheShareGateOff()
	{
		var thresholds = Defaults with { MinShare = 0 };
		var rows = new[] { Row("d1", Slot, children: 1_000_000, orphans: 1_000) };

		var incident = Assert.Single(OrphanedChildrenDetector.Detect(rows, thresholds));

		Assert.Equal(0.001, incident.OrphanShare, precision: 10);
	}

	[Theory]
	[InlineData(1.5, 1.0)]
	[InlineData(-0.5, 0.0)]
	[InlineData(double.NaN, 0.0)]
	public void MinShareOutsideZeroToOne_IsHeldToARealFraction(double supplied, double effective)
	{
		var thresholds = Defaults with { MinShare = supplied };

		Assert.Equal(effective, thresholds.EffectiveMinShare, precision: 10);
	}

	[Fact]
	public void AMinShareAboveOne_StillReportsAWhollyOrphanedDataset()
	{
		// Clamped to one, and the comparison is inclusive, so the only datasets left are those whose
		// every child is orphaned. A gate that admitted nothing at all would be a silent empty page.
		var thresholds = Defaults with { MinShare = 2.0 };
		var rows = new[]
		{
			Row("whole", Slot, children: 5_000, orphans: 5_000),
			Row("nearly", Slot, children: 5_000, orphans: 4_999),
		};

		var incident = Assert.Single(OrphanedChildrenDetector.Detect(rows, thresholds));

		Assert.Equal("whole", incident.DatasetUrl);
	}

	[Fact]
	public void PastThreshold_IsNeverLooserThanTheOpenThreshold()
	{
		// A caller asking for a past-threshold below min_orphans must not be able to make a summary
		// tile's past_threshold_count exceed its count.
		var thresholds = Defaults with { MinOrphans = 500, PastThresholdOrphans = 10 };
		var rows = new[] { Row("d1", Slot, children: 1000, orphans: 500) };

		var incident = Assert.Single(OrphanedChildrenDetector.Detect(rows, thresholds));

		Assert.Equal(500, thresholds.EffectivePastThresholdOrphans);
		Assert.True(incident.PastThreshold);
	}

	#endregion

	#region Share arithmetic

	[Fact]
	public void OrphanShare_IsOrphansOverChildren()
	{
		var rows = new[] { Row("d1", Slot, children: 400, orphans: 100) };

		var incident = Assert.Single(OrphanedChildrenDetector.Detect(rows, Open));

		Assert.Equal(0.25, incident.OrphanShare, precision: 10);
	}

	[Fact]
	public void OrphanShare_UsesEveryChildAsTheDenominatorNotOnlyThoseExamined()
	{
		// A child that inlines its superEvent cannot dangle, so it is counted but never examined. The
		// share is over every child, which reads lower than the ratio over the examined set.
		var rows = new[] { Row("d1", Slot, children: 1000, orphans: 50, examined: 100) };

		var incident = Assert.Single(OrphanedChildrenDetector.Detect(rows, Open));

		Assert.Equal(100, incident.CheckedCount);
		Assert.Equal(0.05, incident.OrphanShare, precision: 10);
	}

	[Fact]
	public void NoChildrenButOrphansRecorded_DoesNotDivideByZero()
	{
		var rows = new[] { Row("d1", Slot, children: 0, orphans: 5) };

		var incident = Assert.Single(OrphanedChildrenDetector.Detect(rows, Open));

		Assert.Equal(0, incident.OrphanShare);
	}

	[Fact]
	public void MoreOrphansThanChildren_ClampsTheShareToOne()
	{
		// Only a malformed aggregate produces this; a share above one must not reach the dashboard.
		var rows = new[] { Row("d1", Slot, children: 10, orphans: 50) };

		var incident = Assert.Single(OrphanedChildrenDetector.Detect(rows, Open));

		Assert.Equal(1.0, incident.OrphanShare, precision: 10);
	}

	#endregion

	#region Ordering

	[Fact]
	public void IncidentsAreOrderedByOrphanCountDescending()
	{
		var rows = new[]
		{
			Row("small", Slot, children: 1000, orphans: 10),
			Row("largest", Slot, children: 1000, orphans: 900),
			Row("middle", Slot, children: 1000, orphans: 500),
		};

		var incidents = OrphanedChildrenDetector.Detect(rows, Open);

		Assert.Equal(["largest", "middle", "small"], incidents.Select(i => i.DatasetUrl));
	}

	[Fact]
	public void EqualOrphanCounts_AreOrderedByShareBeforeUrl()
	{
		// "a" sorts before "b" ordinally, so b coming first proves share is the earlier tiebreak.
		var rows = new[]
		{
			Row("a", Slot, children: 1000, orphans: 10),
			Row("b", Slot, children: 100, orphans: 10),
		};

		var incidents = OrphanedChildrenDetector.Detect(rows, Open);

		Assert.Equal(["b", "a"], incidents.Select(i => i.DatasetUrl));
	}

	[Fact]
	public void TiesAreBrokenDeterministicallyByDatasetUrl()
	{
		// Orphans cluster, so ties are common. Without this last tiebreak the order is not total and
		// two pages of one result set can overlap or drop a row.
		var rows = new[]
		{
			Row("c", Slot, children: 100, orphans: 10),
			Row("a", Slot, children: 100, orphans: 10),
			Row("b", Slot, children: 100, orphans: 10),
		};

		var incidents = OrphanedChildrenDetector.Detect(rows, Open);

		Assert.Equal(["a", "b", "c"], incidents.Select(i => i.DatasetUrl));
	}

	#endregion
}
