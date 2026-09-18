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
	/// <c>opportunities</c> for the same <c>dataset_url</c>, <em>and</em> which carry no location of
	/// their own — bookable availability hanging off an event nobody consuming the dataset can resolve,
	/// with nothing on the child to fall back on. Ordered worst first.
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
	/// - **The child must also have no location of its own.** <c>location</c> must be <c>NULL</c>, a
	///   JSON <c>null</c>, or the empty object <c>{}</c>. A child that publishes a real location is
	///   still placeable, mappable and bookable by a consumer who cannot resolve its parent, so it is
	///   counted in <c>child_count</c> but not in <c>checked_count</c> or <c>orphan_count</c>. The
	///   effect is that <c>orphan_count</c> reports items a consumer genuinely cannot use, not every
	///   dangling reference.
	/// - **`orphan_share` keeps the full denominator.** Both extra conditions narrow the numerator
	///   only; <c>child_count</c> is still every <c>Slot</c> and <c>ScheduledSession</c> the dataset
	///   publishes, so shares stay comparable between datasets.
	/// - **Two gates open an incident, and both must be met.** A dataset is reported only when it has at
	///   least <c>min_orphans</c> orphans (default <c>1000</c>) <em>and</em> an <c>orphan_share</c> of at
	///   least <c>min_share</c> (default <c>0.1</c>, a tenth of everything it publishes). Absolute count
	///   alone lets a large publisher in on a rounding error of its catalogue; share alone lets a
	///   three-child dataset in on a full house. Lower either parameter to see the smaller cases.
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
	/// <param name="min_orphans">Orphaned children that open an incident, counted across both kinds. Default <c>1000</c>. Pass <c>1</c> to see every dataset with a single orphan.</param>
	/// <param name="min_share">Smallest <c>orphan_share</c> that opens an incident, as a fraction of <c>child_count</c>. Default <c>0.1</c> (10%). Pass <c>0</c> to disable the gate. Values outside <c>0..1</c> are clamped.</param>
	/// <param name="past_threshold_orphans">Orphaned children that set <c>past_threshold</c>. Default <c>10000</c>; never treated as looser than <c>min_orphans</c>.</param>
	[HttpGet("dataset-orphaned-children-incidents")]
	[ProducesResponseType(typeof(AdminPage<OrphanedChildrenIncident>), StatusCodes.Status200OK)]
	public async Task<ActionResult<AdminPage<OrphanedChildrenIncident>>> DatasetOrphanedChildrenIncidents(
		int page = 1,
		int page_size = DefaultPageSize,
		int min_orphans = 1_000,
		double min_share = 0.10,
		int past_threshold_orphans = 10_000)
	{
		var thresholds = BuildThresholds(min_orphans, min_share, past_threshold_orphans);

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

	private static OrphanedChildrenThresholds BuildThresholds(int minOrphans, double minShare, int pastThresholdOrphans) =>
		new()
		{
			// Floor of one: a dataset with no orphans is not an incident. The ceiling is comfortably
			// above any single dataset and keeps the knob away from overflow.
			MinOrphans = Math.Clamp(minOrphans, 1, 1_000_000),
			// A share is a fraction, so anything outside 0..1 is caller error: 0 turns the gate off,
			// above 1 would admit nothing at all. NaN, which no clamp catches, falls back to 0.
			MinShare = double.IsNaN(minShare) ? 0 : Math.Clamp(minShare, 0, 1),
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
