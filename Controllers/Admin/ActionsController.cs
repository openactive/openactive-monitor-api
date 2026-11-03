using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MonitorApi.Models.Admin;
using MonitorApi.Services.Admin;

namespace MonitorApi.Controllers.Admin;

/// <summary>
/// Monitor actions: what the Monitor Action Decision Tree says a steward should do today.
/// Requires the admin token as the <c>token</c> query parameter.
/// </summary>
public class ActionsController(IOptions<BigQueryOptions> bigQueryOptions, IOptions<ApiOptions> apiOptions)
    : MonitorControllerBase(bigQueryOptions, apiOptions)
{
    /// <summary>
    /// Monitor Actions
    /// </summary>
    /// <remarks>
    /// Runs every monitor for one day and applies the Monitor Actions Decision Tree to the results.
    /// Each row is one problem to raise with a publisher, worst first.
    ///
    /// A problem is only reported once it reaches its trigger: 10 days for HTTP and authentication
    /// failures, 5 for dataset stalls, 10 for single-feed stalls (Slot and ScheduledSession feeds only),
    /// 20% of a dataset's children for orphans, and 15 days for future decline (Slot and
    /// ScheduledSession only).
    ///
    /// When a bigger problem explains a smaller one in the same dataset — for example an HTTP failure
    /// behind a set of orphaned children — only the bigger one is reported, and the smaller one is
    /// listed in its <c>suppressed</c> field. Orphans whose missing parents use a different id format
    /// from the parents the dataset publishes are reported as <c>id_pattern_mismatch</c>.
    ///
    /// The seasonal and counter-reset checks for future decline are not built yet, so those rows carry
    /// <c>seasonal_check: "unavailable"</c>.
    ///
    /// Results are cached until the next daily refresh, varying by all query parameters.
    /// </remarks>
    /// <param name="page">One-based page number. Default <c>1</c>.</param>
    /// <param name="page_size">Rows per page. Default <c>500</c>, capped at <c>1000</c>.</param>
    /// <param name="kind">Only return these action kinds, e.g. <c>single_feed_stall</c>. Repeated or comma-separated.</param>
    /// <param name="as_of">Evaluate as at this date instead of the latest ingestion day. ISO <c>yyyy-MM-dd</c>.</param>
    [HttpGet("actions")]
    [ProducesResponseType(typeof(AdminPage<MonitorActionRow>), StatusCodes.Status200OK)]
    public async Task<ActionResult<AdminPage<MonitorActionRow>>> Actions(
        int page = 1,
        int page_size = DefaultPageSize,
        [FromQuery] string[]? kind = null,
        [FromQuery] DateOnly? as_of = null)
    {
        var snapshotDate = await ResolveSnapshotDate(as_of);
        if (snapshotDate is null)
        {
            return Ok(Paginate(Array.Empty<MonitorActionRow>(), page, page_size, as_of ?? DateOnly.FromDateTime(DateTime.UtcNow)));
        }

        var day = snapshotDate.Value;
        var inputs = await LoadInputs(day);
        var actions = ActionRouter.Route(inputs);

        var datasets = await LoadDatasetMetadata(actions.Select( a => a.DatasetUrl).Distinct().ToList());
        var feeds = await LoadFeedMetadata(actions.Where(a => a.FeedId is not null).Select(a => a.FeedId!).Distinct().ToList());

        var rows = actions
            .Select(a => MonitorActionRows.From(
                a,
                datasets.GetValueOrDefault(a.DatasetUrl),
                a.FeedId is null ? null : feeds.GetValueOrDefault(a.FeedId)))
            .ToList();

        var kinds = NormaliseMultiValue(kind);
        if (kinds.Count > 0)
        {
            rows = rows.Where(r => kinds.Contains(r.Kind)).ToList();
        }

        return Ok(Paginate(rows, page, page_size, day));
    }

    private async Task<ActionRouterInputs> LoadInputs(DateOnly day)
    {
        // Trend windows are cut to one day because nothing here reads them.
        var feedStallThresholds = new SingleFeedStallThresholds { TrendDays = 1, IncidentTrendDays = 1 };
        var datasetStallThresholds = new DatasetStallThresholds { TrendDays = 1, IncidentTrendDays = 1 };
        var errorThresholds = new FeedIngestionErrorThresholds { TrendDays = 1, IncidentTrendDays = 1 };
        var declineThresholds = new DatasetFutureDeclineThresholds { TrendDays = 1, IncidentTrendDays = 1 };

        var histories = await LoadHistories(
            day,
            Math.Max(feedStallThresholds.RequiredHistoryDays, datasetStallThresholds.RequiredHistoryDays),
            trendDays: 1,
            ignoreFirstIngestionDate: true);
        
        var statusHistories = await LoadStatusHistories(day, errorThresholds.RequiredHistoryDays);
        var supply = await LoadFutureSupply(day, declineThresholds.RequiredHistoryDays, DatasetSupplyController.IgnoredKinds);
        var orphanCounts = await LoadOrphanCounts();

        var singleFeedStalls = SingleFeedStallDetector.Detect(histories, day, feedStallThresholds);
        var declines = DatasetFutureDeclineDetector.Detect(supply, day, declineThresholds);
        var orphans = OrphanedChildrenDetector.Detect(orphanCounts, new OrphanedChildrenThresholds());

        // Only fetch parent ids for datasets whose orphans are bad enough to trigger.
        var triggers = new ActionTriggers();
        var orphanDatasets = orphans
            .Where(o => o.OrphanShare >= triggers.OrphanShare)
            .Select(o => o.DatasetUrl)
            .ToList();

        var feedIds = singleFeedStalls.Select(s => s.FeedId)
            .Concat(declines.SelectMany(d => d.Feeds).Select(f => f.FeedId))
            .Distinct()
            .ToList();
        var feedKinds = (await LoadFeedMetadata(feedIds))
            .Where(f => f.Value.FeedType is not null)
            .ToDictionary(f => f.Key, f => f.Value.FeedType!, StringComparer.Ordinal);

        return new ActionRouterInputs
        {
            HttpFailures = FeedIngestionErrorDetector.Detect(statusHistories, day, errorThresholds),
            AuthFailures = AuthRequiredDetector.Detect(statusHistories, day),
            DatasetStalls = DatasetStallDetector.Detect(histories, day, datasetStallThresholds),
            SingleFeedStalls = singleFeedStalls,
            Orphans = orphans,
            ParentIdSamples = await LoadParentIdSamples(orphanDatasets),
            FutureDeclines = declines,
            FeedKinds = feedKinds,
        };
    }

    private async Task<List<ParentIdSample>> LoadParentIdSamples(IReadOnlyCollection<string> datasetUrls)
    {
        if (datasetUrls.Count == 0)
        {
            return [];
        }

        var rows = await Query(
            ParentIdSampleQuery.ParentIdSampleSql(Fq(Tables.Opportunities)),
            ParentIdSampleQuery.ParentIdSampleParameters(datasetUrls));

        return await rows.Select(ParentIdSampleQuery.ParseParentIdSample).ToListAsync();
    }
}