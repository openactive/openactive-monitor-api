using MonitorApi.Models;

// Everything the dataset_orphaned_children monitor is made of, in one file: its thresholds, the
// per-dataset-per-kind counts it detects on, the SQL that aggregates those counts, and the pure rules
// themselves — the same one-file-per-monitor convention as FeedIngestionErrorMonitor.cs, and the same
// split between *deciding* and *fetching* enforced by type rather than by file:
// OrphanedChildrenDetector references no BigQuery and no ASP.NET, so it is unit tested without
// credentials.
//
// Unlike its two siblings this monitor reads `opportunities` rather than `opportunity_ingestion`, and
// `opportunities` is a current-state mirror with no per-day snapshots. Hence no trend endpoint, no
// `as_of`, no date filtering of any kind, and thresholds counted in orphans rather than in days. The
// query takes no parameters at all: it asks one question of the whole table.
namespace MonitorApi.Services.Admin;

/// <summary>
/// Tunable thresholds for the <c>dataset_orphaned_children</c> monitor, both counted in orphaned
/// children.
/// </summary>
/// <remarks>
/// Deliberately carries nothing measured in days — no <c>TrendDays</c>, no lookback.
/// <c>opportunities</c> mirrors what publishers expose right now and holds no history, so there is no
/// series to chart, and every child it holds is checked however long ago it was added.
/// </remarks>
public sealed record OrphanedChildrenThresholds
{
	/// <summary>
	/// Orphaned children that open an incident. At the default of one, a single orphan is enough.
	/// </summary>
	public int MinOrphans { get; init; } = 1;

	/// <summary>Orphaned children after which an open incident is flagged as past threshold.</summary>
	public int PastThresholdOrphans { get; init; } = 100;

	/// <summary>
	/// Past threshold can never be looser than the open threshold, otherwise a summary tile's
	/// <c>past_threshold_count</c> could exceed its <c>count</c>.
	/// </summary>
	public int EffectivePastThresholdOrphans => Math.Max(PastThresholdOrphans, MinOrphans);
}

/// <summary>
/// A referenced <c>superEvent</c> the dataset does not hold, with how many children point at it.
/// </summary>
/// <param name="MissingId">The <c>data_id</c> that could not be found.</param>
/// <param name="ChildCount">Children referencing it — how much one fix would repair.</param>
public sealed record MissingParent(string MissingId, long ChildCount);

/// <summary>
/// One dataset's counts for one child kind, as aggregated by <see cref="OrphanedChildrenQuery"/> —
/// the query returns a row per <c>(dataset_url, kind)</c> pair and the detector folds them per dataset.
/// </summary>
/// <param name="DatasetUrl">The dataset, matching <c>feeds.dataset_url</c>.</param>
/// <param name="Kind">The child kind: <c>Slot</c> or <c>ScheduledSession</c>.</param>
/// <param name="ChildCount">
/// Every child of this kind the dataset publishes, regardless of age or of whether it carries a scalar
/// parent reference. The monitor's denominator.
/// </param>
/// <param name="CheckedCount">
/// Subset of <paramref name="ChildCount"/> actually examined: those naming their parent with a JSON
/// scalar reference. A child that inlines its <c>superEvent</c> as an object carries its parent with
/// it and cannot dangle.
/// </param>
/// <param name="OrphanCount">
/// Subset of <paramref name="CheckedCount"/> whose referenced parent is not held for this dataset.
/// </param>
/// <param name="MissingParentCount">
/// Distinct missing parent ids behind <paramref name="OrphanCount"/> — the actionable figure, since one
/// absent parent can orphan thousands of children.
/// </param>
/// <param name="MissingParents">Worst-first sample of those missing parents, as evidence.</param>
/// <remarks>
/// Counts are <c>long</c>: this is the whole estate, and the orphaned-Slot figure alone is already in
/// the hundreds of thousands.
/// </remarks>
public sealed record DatasetKindOrphanCounts(
	string DatasetUrl,
	string Kind,
	long ChildCount,
	long CheckedCount,
	long OrphanCount,
	long MissingParentCount,
	IReadOnlyList<MissingParent> MissingParents);

/// <summary>
/// A dataset publishing orphaned children, folded across kinds, before dataset metadata is attached.
/// </summary>
public sealed record OrphanedChildren
{
	public required string DatasetUrl { get; init; }

	/// <summary>Every child of the monitored kinds the dataset publishes, across all kinds.</summary>
	public required long ChildCount { get; init; }

	/// <summary>Subset of <see cref="ChildCount"/> examined for a dangling reference.</summary>
	public required long CheckedCount { get; init; }

	/// <summary>Children whose referenced parent is missing.</summary>
	public required long OrphanCount { get; init; }

	/// <summary>
	/// <see cref="OrphanCount"/> over <see cref="ChildCount"/>, clamped to <c>0..1</c>. Zero when the
	/// dataset publishes no children of these kinds at all.
	/// </summary>
	public required double OrphanShare { get; init; }

	/// <summary>Distinct missing parent ids across all kinds.</summary>
	public required long MissingParentCount { get; init; }

	/// <summary>
	/// The per-kind rows this incident was folded from, worst kind first. Lets the dashboard say
	/// "Slots fine, ScheduledSessions broken" rather than only reporting one total.
	/// </summary>
	public required IReadOnlyList<DatasetKindOrphanCounts> ByKind { get; init; }

	/// <summary>
	/// The dataset's worst missing parents, merged across kinds and re-truncated, so the sample is the
	/// dataset's worst offenders rather than one kind's.
	/// </summary>
	public required IReadOnlyList<MissingParent> MissingParents { get; init; }

	/// <summary>Whether <see cref="OrphanCount"/> has reached the past-threshold limit.</summary>
	public required bool PastThreshold { get; init; }
}

/// <summary>
/// Pure detection logic for the <c>dataset_orphaned_children</c> monitor: a dataset publishing children
/// whose parent event is missing from the same dataset.
/// </summary>
/// <remarks>
/// Thin on purpose. Deciding what "orphaned" means is an anti-join over hundreds of thousands of rows
/// and belongs in SQL, which hands this layer one already-aggregated row per dataset and kind. What is
/// left is still where the defects live — folding the kinds together, a past-threshold count that could
/// exceed the open count, a divide-by-zero share, a non-total order that breaks paging — so it lives
/// here, free of BigQuery and ASP.NET, and is unit tested without credentials.
///
/// <c>Detect</c> takes no <c>asOf</c>, unlike <see cref="FeedIngestionErrorDetector"/> and
/// <see cref="SingleFeedStallDetector"/>. That absence is the signature recording that this monitor is
/// not history-derived: <c>opportunities</c> holds current state only.
/// </remarks>
public static class OrphanedChildrenDetector
{
	/// <summary>Monitor identifier echoed on every incident.</summary>
	public const string MonitorId = "dataset_orphaned_children";

	/// <summary>Missing parents carried per dataset as evidence.</summary>
	public const int MissingParentSample = 5;

	/// <summary>Child kinds this monitor checks, and the parent each one references.</summary>
	/// <remarks>
	/// A <c>Slot</c> references its <c>FacilityUse</c>, a <c>ScheduledSession</c> its
	/// <c>SessionSeries</c>. The check itself is kind-agnostic on the parent side — see
	/// <see cref="OrphanedChildrenQuery.OrphanCountsSql"/>.
	/// </remarks>
	public static readonly IReadOnlyList<string> ChildKinds = ["Slot", "ScheduledSession"];

	/// <summary>
	/// Folds the per-kind rows into one incident per dataset and returns those that reach
	/// <see cref="OrphanedChildrenThresholds.MinOrphans"/>, worst first.
	/// </summary>
	/// <remarks>
	/// Ordering is total: orphan count descending, then share descending, then <c>dataset_url</c>
	/// ordinally. That last tiebreak is not cosmetic — orphans cluster in a few datasets, so ties are
	/// common, and without it two pages of the same result set could overlap or drop a row.
	///
	/// Rows with a blank <c>dataset_url</c> are dropped: they cannot be attributed to a publisher.
	/// </remarks>
	public static IReadOnlyList<OrphanedChildren> Detect(
		IEnumerable<DatasetKindOrphanCounts> rows,
		OrphanedChildrenThresholds thresholds)
	{
		var incidents = new List<OrphanedChildren>();

		var byDataset = rows
			.Where(r => !string.IsNullOrWhiteSpace(r.DatasetUrl))
			.GroupBy(r => r.DatasetUrl, StringComparer.Ordinal);

		foreach (var dataset in byDataset)
		{
			// A repeated (dataset, kind) row keeps its first entry. The query groups by both, so this
			// only guards against a hand-built input.
			var kinds = dataset
				.DistinctBy(r => r.Kind, StringComparer.Ordinal)
				.OrderByDescending(r => r.OrphanCount)
				.ThenBy(r => r.Kind, StringComparer.Ordinal)
				.ToList();

			var orphanCount = kinds.Sum(k => k.OrphanCount);
			if (orphanCount < thresholds.MinOrphans)
			{
				continue;
			}

			var childCount = kinds.Sum(k => k.ChildCount);

			incidents.Add(new OrphanedChildren
			{
				DatasetUrl = dataset.Key,
				ChildCount = childCount,
				CheckedCount = kinds.Sum(k => k.CheckedCount),
				OrphanCount = orphanCount,
				OrphanShare = Share(orphanCount, childCount),
				MissingParentCount = kinds.Sum(k => k.MissingParentCount),
				ByKind = kinds,
				MissingParents = WorstMissingParents(kinds),
				PastThreshold = orphanCount >= thresholds.EffectivePastThresholdOrphans,
			});
		}

		return incidents
			.OrderByDescending(i => i.OrphanCount)
			.ThenByDescending(i => i.OrphanShare)
			.ThenBy(i => i.DatasetUrl, StringComparer.Ordinal)
			.ToList();
	}

	/// <summary>
	/// Orphans as a fraction of the dataset's children, clamped to <c>0..1</c>. A dataset with no
	/// children cannot have a meaningful share, and an aggregate reporting more orphans than children is
	/// malformed — neither may reach the dashboard as a divide-by-zero or a share above one.
	/// </summary>
	private static double Share(long orphans, long children) =>
		children <= 0 ? 0 : Math.Clamp((double)orphans / children, 0, 1);

	/// <summary>
	/// The dataset's worst missing parents across every kind, most children first, then by id so the
	/// sample is stable between requests.
	/// </summary>
	private static IReadOnlyList<MissingParent> WorstMissingParents(IEnumerable<DatasetKindOrphanCounts> kinds) =>
		kinds
			.SelectMany(k => k.MissingParents ?? [])
			.OrderByDescending(p => p.ChildCount)
			.ThenBy(p => p.MissingId, StringComparer.Ordinal)
			.Take(MissingParentSample)
			.ToList();
}

/// <summary>
/// SQL and row parsing for the per-dataset orphaned-child counts that
/// <see cref="OrphanedChildrenDetector"/> runs on.
/// </summary>
/// <remarks>
/// The only query in the repo that reads <c>opportunities</c> for the admin surface. Table names are
/// passed in already fully qualified by the caller's <c>Fq</c>.
/// </remarks>
internal static class OrphanedChildrenQuery
{
	/// <summary>
	/// Per-dataset, per-kind child and orphan counts: one row for every
	/// <c>(dataset_url, kind)</c> pair the dataset publishes, including pairs with no orphans at all —
	/// the <c>min_orphans</c> decision belongs to <see cref="OrphanedChildrenDetector"/>, not here.
	/// </summary>
	/// <remarks>
	/// Two scans of <c>opportunities</c>: the child kinds once, and the whole table once for the set of
	/// ids that exist. Every child is counted however long ago it was added — the table holds current
	/// state, so a child in it is a child a consumer can see today, whatever its <c>last_updated</c>.
	///
	/// Children are collapsed to distinct <c>(dataset_url, kind, parent_id)</c> before the anti-join:
	/// many children share one missing parent, so this shrinks the probe side by that fan-in ratio and
	/// makes <c>missing_parent_count</c> and the evidence sample by-products rather than extra passes.
	/// It also references the CTE exactly once — a <c>WITH</c> read twice may be re-evaluated.
	///
	/// Neither side is filtered by date. "Do we hold this id at all" is a question with no time in it,
	/// and a <c>FacilityUse</c> or <c>SessionSeries</c> is slowly changing: ingested once, its
	/// <c>last_updated</c> never moving again while the publisher churns children against it. Ageing
	/// either side out would report stable datasets as broken and would quietly duplicate
	/// <c>single_feed_stall</c>.
	///
	/// It is also kind-agnostic on the parent side — no <c>kind = 'FacilityUse'</c> — which keeps this
	/// a referential-integrity check rather than a schema check.
	/// </remarks>
	/// <param name="opportunitiesTable">Fully qualified <c>opportunities</c> table name.</param>
	public static string OrphanCountsSql(string opportunitiesTable) =>
		$"""
		WITH children AS (
		  SELECT dataset_url,
		         kind,
		         -- NULL for a child that does not name a parent: it counts in the denominator but is
		         -- never examined for orphanhood.
		         --
		         -- JSON_TYPE = 'string' is the reference test. An 'object' is the superEvent inlined in
		         -- the child and cannot dangle, which is most of what it excludes.
		         --
		         -- JSON_VALUE already returns the unquoted scalar, so the TRIM is a no-op for every row
		         -- in the table today (verified). It is kept because it is not a no-op for a
		         -- double-encoded reference ("\"abc\"" -> abc), and dropping it could only ever turn
		         -- such a matching reference into a false orphan.
		         IF(JSON_TYPE(has_superEvent) = 'string',
		            TRIM(JSON_VALUE(has_superEvent), '"'), NULL) AS parent_id,
		         COUNT(*) AS child_rows
		  FROM {opportunitiesTable}
		  WHERE kind IN ('Slot', 'ScheduledSession')
		        -- Defensive: every row carries one today. A NULL here could not be attributed to a
		        -- publisher, and would report as an orphan for free, since NULL never joins.
		        AND dataset_url IS NOT NULL
		  GROUP BY dataset_url, kind, parent_id
		),
		parents AS (
		  -- DISTINCT is load-bearing rather than an optimisation: without it a parent present more than
		  -- once fans its child row out, inflating child_count while leaving orphan_count correct, so
		  -- orphan_share would silently fall.
		  SELECT DISTINCT dataset_url, data_id
		  FROM {opportunitiesTable}
		  WHERE dataset_url IS NOT NULL
		        AND data_id IS NOT NULL
		),
		flagged AS (
		  SELECT c.dataset_url,
		         c.kind,
		         c.parent_id,
		         c.child_rows,
		         (c.parent_id IS NOT NULL AND p.data_id IS NULL) AS is_orphan
		  FROM children AS c
		  LEFT JOIN parents AS p
		    ON p.dataset_url = c.dataset_url
		       AND p.data_id = c.parent_id
		)
		SELECT dataset_url,
		       kind,
		       SUM(child_rows) AS child_count,
		       SUM(IF(parent_id IS NOT NULL, child_rows, 0)) AS checked_count,
		       SUM(IF(is_orphan, child_rows, 0)) AS orphan_count,
		       COUNTIF(is_orphan) AS missing_parent_count,
		       -- Worst offenders first, so the sample is evidence somebody can act on rather than an
		       -- arbitrary slice; parent_id breaks ties so it is stable between requests. The LIMIT
		       -- must be a literal, and this one is code-controlled rather than caller-supplied.
		       ARRAY_AGG(IF(is_orphan, STRUCT(parent_id AS missing_id, child_rows AS child_count), NULL)
		                 IGNORE NULLS
		                 ORDER BY child_rows DESC, parent_id
		                 LIMIT {OrphanedChildrenDetector.MissingParentSample}) AS missing_parents
		FROM flagged
		GROUP BY dataset_url, kind
		""";

	/// <summary>
	/// Reads one dataset-and-kind row. <c>dataset_url</c> and <c>kind</c> are cast directly because the
	/// query groups by them and filters them non-NULL; everything else goes through
	/// <see cref="BigQueryValueParser"/> and <c>GetValueOrDefault</c>, since a NULL column is absent
	/// from the row dictionary rather than present as null.
	/// </summary>
	public static DatasetKindOrphanCounts ParseOrphanCounts(Dictionary<string, object> row) =>
		new(
			(string)row["dataset_url"],
			(string)row["kind"],
			BigQueryValueParser.AsLong(row.GetValueOrDefault("child_count")) ?? 0,
			BigQueryValueParser.AsLong(row.GetValueOrDefault("checked_count")) ?? 0,
			BigQueryValueParser.AsLong(row.GetValueOrDefault("orphan_count")) ?? 0,
			BigQueryValueParser.AsLong(row.GetValueOrDefault("missing_parent_count")) ?? 0,
			ParseMissingParents(row.GetValueOrDefault("missing_parents")));

	/// <summary>
	/// Parses the repeated <c>STRUCT&lt;missing_id STRING, child_count INT64&gt;</c> evidence sample,
	/// preserving the query's worst-first order. The BigQuery client surfaces a repeated struct as
	/// <c>Dictionary&lt;string, object&gt;[]</c>, and a <c>NULL</c> field is absent from that dictionary
	/// rather than present as null.
	/// </summary>
	private static IReadOnlyList<MissingParent> ParseMissingParents(object? cell)
	{
		var parents = new List<MissingParent>();

		if (cell is not System.Collections.IEnumerable rows || cell is string)
		{
			return parents;
		}

		foreach (var item in rows)
		{
			if (item is not IDictionary<string, object> fields)
			{
				continue;
			}

			// An absent or empty id is a blank reference rather than a dangling one. It still counts as
			// an orphan, matching the reference semantics, but it is no use as evidence.
			if (Field(fields, "missing_id") is not string missingId || missingId.Length == 0)
			{
				continue;
			}

			parents.Add(new MissingParent(
				missingId,
				BigQueryValueParser.AsLong(Field(fields, "child_count")) ?? 0));
		}

		return parents;
	}

	private static object? Field(IDictionary<string, object> fields, string name) =>
		fields.TryGetValue(name, out var value) ? value : null;
}
