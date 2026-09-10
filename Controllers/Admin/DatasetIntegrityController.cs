using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MonitorApi.Models.Admin;
using MonitorApi.Services.Admin;

namespace MonitorApi.Controllers.Admin;

/// <summary>
/// Dataset-level data integrity monitors for the admin dashboard: publishing that is internally
/// inconsistent rather than absent. Every endpoint requires the admin token as the <c>token</c> query
/// parameter.
/// </summary>
/// <remarks>
/// Reads <c>opportunities</c> rather than <c>opportunity_ingestion</c>, but still derives from
/// <see cref="MonitorControllerBase"/> for <c>ResolveSnapshotDate</c>. That resolves the latest day in
/// the <em>ingestion</em> table, which is deliberate: <c>opportunities</c> is a mirror refreshed by
/// that same pipeline, so the ingestion day is the mirror's provenance, whereas its own
/// <c>last_updated</c> is publisher-supplied and can sit in the future when a publisher's clock is
/// wrong. It also keeps <c>meta.snapshot_date</c> meaning one thing across the whole admin surface,
/// which <c>/admin/summary</c> relies on when it dates every monitor from a single resolution.
/// </remarks>
public class DatasetIntegrityController(IOptions<BigQueryOptions> bigQueryOptions, IOptions<ApiOptions> apiOptions)
	: MonitorControllerBase(bigQueryOptions, apiOptions)
{
	/// <summary>
	/// Dataset Orphaned Children Incidents
	/// </summary>
	/// <remarks>
	/// Datasets publishing children whose <c>has_superEvent</c> names a parent that is not in
	/// <c>opportunities</c> for the same <c>dataset_url</c> — bookable availability hanging off an event
	/// nobody consuming the dataset can resolve. Ordered worst first.
	///
	/// Two child kinds are checked: a <c>Slot</c>, which references its <c>FacilityUse</c>, and a
	/// <c>ScheduledSession</c>, which references its <c>SessionSeries</c>. <c>detail.by_kind</c> splits
	/// every count between them.
	///
	/// The rules worth knowing:
	///
	/// - **One incident per dataset, not per feed.** The missing parent may be published by a different
	///   feed of the same dataset, so the check only means anything at dataset scope, and a publisher
	///   fixes it once.
	/// - **`detail.missing_parents` and `missing_parent_count` are the actionable figures.** One absent
	///   parent can orphan thousands of children, so a six-figure <c>orphan_count</c> is often a
	///   two-figure repair job.
	/// - **Only a scalar reference can dangle.** A child that inlines its <c>superEvent</c> as a JSON
	///   object is counted in <c>child_count</c> but never examined — it carries its parent with it.
	///   This is most of what is excluded, particularly for <c>ScheduledSession</c>.
	/// - **Nothing is filtered by date.** Every child the table holds is counted, however long ago it
	///   was added: <c>opportunities</c> is current state, so anything in it is something a consumer can
	///   see today. Ageing children or parents out would report stable datasets as broken, since a
	///   <c>FacilityUse</c> is ingested once and then sits unchanged while the publisher churns slots
	///   against it.
	/// - **A parent published by a different publisher still counts as missing.** The check is scoped to
	///   one <c>dataset_url</c>, because a consumer of this dataset cannot resolve anything outside it.
	///
	/// This monitor is not history-derived, and three things follow. It takes no date parameter of any
	/// kind — no <c>as_of</c>, no lookback — because <c>opportunities</c> holds current state only, so a
	/// past date cannot be answered and accepting one would return today's figures under yesterday's
	/// label. There is no sibling trend endpoint. And the incident carries no <c>feed_id</c>,
	/// <c>first_detected</c>, <c>days_open</c>, <c>consecutive_days</c> or <c>trend</c> — those fields
	/// are absent rather than null.
	///
	/// <c>status</c> is always <c>open</c> and <c>last_contacted</c> always <c>null</c>, as on every
	/// monitor: outreach state needs an incident-tracking store, which does not exist yet.
	///
	/// Results are cached until the next daily refresh, varying by all query parameters.
	/// </remarks>
	/// <param name="page">One-based page number. Default <c>1</c>.</param>
	/// <param name="page_size">Rows per page. Default <c>500</c>, capped at <c>1000</c>.</param>
	/// <param name="min_orphans">Orphaned children that open an incident, counted across both kinds. Default <c>1</c> — a single orphan is enough.</param>
	/// <param name="past_threshold_orphans">Orphaned children that set <c>past_threshold</c>. Default <c>100</c>; never treated as looser than <c>min_orphans</c>.</param>
	[HttpGet("dataset-orphaned-children-incidents")]
	[ProducesResponseType(typeof(AdminPage<OrphanedChildrenIncident>), StatusCodes.Status200OK)]
	public async Task<ActionResult<AdminPage<OrphanedChildrenIncident>>> DatasetOrphanedChildrenIncidents(
		int page = 1,
		int page_size = DefaultPageSize,
		int min_orphans = 1,
		int past_threshold_orphans = 100)
	{
		var thresholds = BuildThresholds(min_orphans, past_threshold_orphans);

		var snapshotDate = await ResolveSnapshotDate(asOf: null);
		if (snapshotDate is null)
		{
			return Ok(Paginate(
				Array.Empty<OrphanedChildrenIncident>(), page, page_size, DateOnly.FromDateTime(DateTime.UtcNow)));
		}

		var counts = await LoadOrphanCounts();
		var detected = OrphanedChildrenDetector.Detect(counts, thresholds);

		var metadata = await LoadDatasetMetadata(detected.Select(d => d.DatasetUrl).ToList());
		var incidents = detected
			.Select(d => ToIncident(d, metadata.GetValueOrDefault(d.DatasetUrl)))
			.ToList();

		return Ok(Paginate(incidents, page, page_size, snapshotDate.Value));
	}

	#region Utilities

	private static OrphanedChildrenThresholds BuildThresholds(int minOrphans, int pastThresholdOrphans) =>
		new()
		{
			// Floor of one: a dataset with no orphans is not an incident. The ceiling is comfortably
			// above any single dataset and keeps the knob away from overflow.
			MinOrphans = Math.Clamp(minOrphans, 1, 1_000_000),
			PastThresholdOrphans = Math.Clamp(pastThresholdOrphans, 1, 1_000_000),
		};

	/// <summary>
	/// Hydrates a detected dataset into the dashboard payload. <paramref name="metadata"/> is null when
	/// the dataset appears in <c>opportunities</c> but has no <c>feeds</c> row — one currently does —
	/// and the incident is still reported, with the descriptive fields left empty.
	/// </summary>
	private static OrphanedChildrenIncident ToIncident(OrphanedChildren orphans, DatasetMetadata? metadata)
	{
		var dataset = metadata ?? new DatasetMetadata(orphans.DatasetUrl, null, null);

		return new OrphanedChildrenIncident
		{
			MonitorId = OrphanedChildrenDetector.MonitorId,
			PublisherId = dataset.PublisherId,
			PublisherName = dataset.PublisherName ?? "",
			DatasetUrl = orphans.DatasetUrl,
			DatasetName = dataset.Name,
			ChildCount = orphans.ChildCount,
			CheckedCount = orphans.CheckedCount,
			OrphanCount = orphans.OrphanCount,
			OrphanShare = orphans.OrphanShare,
			MissingParentCount = orphans.MissingParentCount,
			PastThreshold = orphans.PastThreshold,
			Status = "open",
			LastContacted = null,
			Detail = new OrphanedChildrenIncidentDetail
			{
				ByKind = orphans.ByKind
					.Select(k => new OrphanedChildrenKindCounts
					{
						Kind = k.Kind,
						ChildCount = k.ChildCount,
						CheckedCount = k.CheckedCount,
						OrphanCount = k.OrphanCount,
						MissingParentCount = k.MissingParentCount,
					})
					.ToList(),
				MissingParents = orphans.MissingParents
					.Select(p => new OrphanedChildrenMissingParent
					{
						MissingId = p.MissingId,
						ChildCount = p.ChildCount,
					})
					.ToList(),
			},
		};
	}

	#endregion
}
