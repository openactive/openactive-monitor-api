using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MonitorApi.Models.Admin;
using MonitorApi.Services.Admin;

namespace MonitorApi.Controllers.Admin;

/// <summary>
/// The admin dashboard's landing figures: the size of the monitored estate, how much of it is
/// currently unhealthy, and one line per monitor. Requires the admin token as the <c>token</c> query
/// parameter.
/// </summary>
public class SummaryController(IOptions<BigQueryOptions> bigQueryOptions, IOptions<ApiOptions> apiOptions)
	: MonitorControllerBase(bigQueryOptions, apiOptions)
{
	/// <summary>
	/// Dashboard Summary
	/// </summary>
	/// <remarks>
	/// Headline counts for the admin dashboard, returned as a single object rather than a list — the
	/// <c>meta</c> envelope is the same as every other admin endpoint, with its paging fields fixed at
	/// one row on one page.
	///
	/// **Coverage** (<c>publishers_monitored</c>, <c>datasets</c>, <c>feeds</c>) comes from the most
	/// recent <c>feed_ingestion</c> run, which also supplies <c>meta.generated_at</c> and therefore
	/// <c>meta.snapshot_date</c>. One dataset is one publisher, so <c>publishers_monitored</c> and
	/// <c>datasets</c> always agree.
	///
	/// **<c>publishers_with_issues</c>** counts distinct datasets with at least one <c>ERROR</c>
	/// ingestion row dated today — failures in today's run, not a running total. Early in the day,
	/// before the pipeline has run, this is legitimately zero.
	///
	/// **<c>monitors</c>** carries one entry per monitor, each evaluated at the latest day in
	/// <c>opportunity_ingestion</c> with that monitor's default thresholds. <c>count</c> therefore
	/// matches the <c>meta.total</c> of the monitor's own incidents endpoint called without arguments,
	/// and <c>sparkline</c> is the last seven days of its trend endpoint's <c>open_count</c>, oldest
	/// first. Note that this day is the ingestion table's latest day and may differ from
	/// <c>meta.snapshot_date</c>, which dates the coverage figures.
	///
	/// <c>dataset_orphaned_children</c> is the exception to all of that. Its <c>count</c> is the total
	/// number of orphaned children across the estate — a count of broken items, not of datasets — so it
	/// does <em>not</em> match its incidents endpoint's <c>meta.total</c>, which counts the datasets
	/// responsible. It reads <c>opportunities</c>, a current-state mirror with no per-day snapshots, so
	/// it has no trend endpoint, its <c>sparkline</c> is always empty and its
	/// <c>past_threshold_count</c> is always <c>0</c> (that threshold applies to datasets, not to this
	/// total). Its day-on-day change is unknowable rather than zero, so it contributes nothing to the
	/// <c>*_delta</c> figures below rather than dragging them towards zero.
	///
	/// The three <c>*_delta</c> fields are day-on-day changes: the latest day's figure minus the
	/// previous day's, summed across monitors. Positive means the estate got worse.
	///
	/// <c>open_incidents</c>, <c>past_threshold</c> and <c>open_incidents_delta</c> are always
	/// <c>null</c>: incidents are derived per request rather than tracked, so there is no cross-monitor
	/// total yet. Read the per-monitor figures in <c>monitors</c> instead.
	///
	/// Results are cached until the next daily refresh, varying by all query parameters.
	/// </remarks>
	[HttpGet("summary")]
	[ProducesResponseType(typeof(AdminDocument<AdminSummary>), StatusCodes.Status200OK)]
	public async Task<ActionResult<AdminDocument<AdminSummary>>> Summary()
	{
		var coverage = AdminSummaryQuery.ParseCoverage(
			await QuerySingle(AdminSummaryQuery.CoverageSql(Fq(Tables.FeedIngestion))),
			fallbackGeneratedAt: DateTime.UtcNow);

		var publishersWithIssues = AdminSummaryQuery.ParseErrorDatasetCount(
			await QuerySingle(AdminSummaryQuery.DatasetsWithErrorsTodaySql(Fq(Tables.OpportunityIngestion))));

		var monitors = await LoadMonitors();

		var summary = new AdminSummary
		{
			PublishersMonitored = coverage.Datasets,
			PublishersWithIssues = publishersWithIssues,
			OpenIncidents = null,
			PastThreshold = null,
			Feeds = coverage.Feeds,
			Datasets = coverage.Datasets,
			Monitors = monitors
				.Select(m => new MonitorSummary
				{
					MonitorId = m.MonitorId,
					Count = m.Count,
					PastThresholdCount = m.PastThresholdCount,
					Sparkline = m.Sparkline,
				})
				.ToList(),
			PublishersWithIssuesDelta = MonitorSummaries.TotalDelta(monitors, m => m.CountDelta),
			OpenIncidentsDelta = null,
			PastThresholdDelta = MonitorSummaries.TotalDelta(monitors, m => m.PastThresholdDelta),
		};

		return Ok(Document(summary, DateOnly.FromDateTime(coverage.GeneratedAt), coverage.GeneratedAt));
	}

	#region Utilities

	/// <summary>
	/// Runs every monitor over the sparkline window and reduces each to its summary line. Monitors with
	/// no history to report are dropped rather than shown as zero.
	/// </summary>
	private async Task<IReadOnlyList<MonitorSummarySnapshot>> LoadMonitors()
	{
		var snapshotDate = await ResolveSnapshotDate(asOf: null);
		if (snapshotDate is null)
		{
			return [];
		}

		var monitors = new List<MonitorSummarySnapshot>();

		// Defaults everywhere except the trend length, so each `count` agrees with what the monitor's own
		// incidents endpoint reports; the per-incident trend columns are not used here, so their windows
		// are collapsed to a single day rather than loading counts nothing will read.
		var singleFeedStallThresholds = new SingleFeedStallThresholds
		{
			TrendDays = MonitorSummaries.SparklineDays,
			IncidentTrendDays = 1,
		};
		var datasetStallThresholds = new DatasetStallThresholds
		{
			TrendDays = MonitorSummaries.SparklineDays,
			IncidentTrendDays = 1,
		};

		// One load for both stall monitors: they detect on the same per-feed publishing history, one feed
		// at a time and one dataset at a time, and running them over the very same rows is what keeps
		// their two tiles from disagreeing about a day.
		var histories = await LoadHistories(
			snapshotDate.Value,
			Math.Max(singleFeedStallThresholds.RequiredHistoryDays, datasetStallThresholds.RequiredHistoryDays),
			trendDays: 1,
			ignoreFirstIngestionDate: true);

		if (SingleFeedStallSummary(histories, snapshotDate.Value, singleFeedStallThresholds) is { } singleFeedStall)
		{
			monitors.Add(singleFeedStall);
		}

		if (DatasetStallSummary(histories, snapshotDate.Value, datasetStallThresholds) is { } datasetStall)
		{
			monitors.Add(datasetStall);
		}

		if (await FeedIngestionErrorSummary(snapshotDate.Value) is { } feedIngestionError)
		{
			monitors.Add(feedIngestionError);
		}

		if (await OrphanedChildrenSummary() is { } orphanedChildren)
		{
			monitors.Add(orphanedChildren);
		}

		return monitors;
	}

	private static MonitorSummarySnapshot? SingleFeedStallSummary(
		IReadOnlyList<FeedIngestionHistory> histories,
		DateOnly snapshotDate,
		SingleFeedStallThresholds thresholds)
	{
		var trend = SingleFeedStallDetector.Trend(histories, snapshotDate, thresholds)
			.Select(p => new MonitorTrendPoint(p.Date, p.OpenCount, p.PastThresholdCount))
			.ToList();

		return MonitorSummaries.Summarise(SingleFeedStallDetector.MonitorId, trend);
	}

	/// <summary>
	/// The dataset-wide stall tile, counting datasets rather than feeds.
	/// </summary>
	/// <remarks>
	/// Runs over the same histories as <see cref="SingleFeedStallSummary"/> and with the same stall
	/// threshold, which is what makes the two tiles complementary: a silent feed is counted towards one
	/// of them or towards the other, never towards both.
	/// </remarks>
	private static MonitorSummarySnapshot? DatasetStallSummary(
		IReadOnlyList<FeedIngestionHistory> histories,
		DateOnly snapshotDate,
		DatasetStallThresholds thresholds)
	{
		var trend = DatasetStallDetector.Trend(histories, snapshotDate, thresholds)
			.Select(p => new MonitorTrendPoint(p.Date, p.OpenCount, p.PastThresholdCount))
			.ToList();

		return MonitorSummaries.Summarise(DatasetStallDetector.MonitorId, trend);
	}

	private async Task<MonitorSummarySnapshot?> FeedIngestionErrorSummary(DateOnly snapshotDate)
	{
		// Defaults everywhere except the trend length, so `count` agrees with what
		// /admin/feed-ingestion-error-incidents reports; the per-incident status strip is not used here,
		// so its window is collapsed to a single day.
		var thresholds = new FeedIngestionErrorThresholds
		{
			TrendDays = MonitorSummaries.SparklineDays,
			IncidentTrendDays = 1,
		};

		var histories = await LoadStatusHistories(snapshotDate, thresholds.RequiredHistoryDays);

		var trend = FeedIngestionErrorDetector.Trend(histories, snapshotDate, thresholds)
			.Select(p => new MonitorTrendPoint(p.Date, p.OpenCount, p.PastThresholdCount))
			.ToList();

		return MonitorSummaries.Summarise(FeedIngestionErrorDetector.MonitorId, trend);
	}

	/// <summary>
	/// The orphaned-children tile. <c>count</c> is the total number of orphaned children across the
	/// estate, not the number of datasets reporting them.
	/// </summary>
	/// <remarks>
	/// The one tile whose <c>count</c> is not an incident count. The other monitors answer "how many
	/// feeds are broken"; the useful headline here is the size of the defect itself, since a single
	/// dataset routinely accounts for hundreds of thousands of orphans. The number of datasets is
	/// <c>meta.total</c> on /admin/dataset-orphaned-children-incidents.
	///
	/// Built directly rather than through <see cref="MonitorSummaries.Summarise"/>, which derives its
	/// figures from a daily trend this monitor does not have. <c>past_threshold_count</c> is zero and
	/// <c>sparkline</c> empty: the escalation threshold applies to datasets rather than to this total,
	/// so counting it here would mix two units, and <c>opportunities</c> holds no history to draw a
	/// series from. Both deltas are null, which
	/// <see cref="MonitorSummaries.TotalDelta"/> skips rather than counting as zero, so the headline
	/// deltas stay the sum of the monitors that do have a yesterday.
	/// </remarks>
	private async Task<MonitorSummarySnapshot?> OrphanedChildrenSummary()
	{
		var counts = await LoadOrphanCounts();
		var incidents = OrphanedChildrenDetector.Detect(counts, new OrphanedChildrenThresholds());

		var orphans = incidents.Sum(i => i.OrphanCount);

		return new MonitorSummarySnapshot(
			OrphanedChildrenDetector.MonitorId,
			// The estate's orphan total. Clamped into int for the shared tile shape; the true figure is
			// six digits today, so there is a lot of headroom before this could bite.
			(int)Math.Clamp(orphans, 0, int.MaxValue),
			PastThresholdCount: 0,
			Sparkline: [],
			CountDelta: null,
			PastThresholdDelta: null);
	}

	#endregion
}
