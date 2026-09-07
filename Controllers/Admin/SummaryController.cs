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

		if (await SingleFeedStallSummary(snapshotDate.Value) is { } singleFeedStall)
		{
			monitors.Add(singleFeedStall);
		}

		return monitors;
	}

	private async Task<MonitorSummarySnapshot?> SingleFeedStallSummary(DateOnly snapshotDate)
	{
		// Defaults everywhere except the trend length, so `count` agrees with what
		// /admin/single-feed-stall-incidents reports; the per-incident trend column is not used here, so
		// its window is collapsed to a single day rather than loading counts nothing will read.
		var thresholds = new SingleFeedStallThresholds
		{
			TrendDays = MonitorSummaries.SparklineDays,
			IncidentTrendDays = 1,
		};

		var histories = await LoadHistories(
			snapshotDate,
			thresholds.RequiredHistoryDays,
			thresholds.IncidentTrendDays);

		var trend = SingleFeedStallDetector.Trend(histories, snapshotDate, thresholds)
			.Select(p => new MonitorTrendPoint(p.Date, p.OpenCount, p.PastThresholdCount))
			.ToList();

		return MonitorSummaries.Summarise(SingleFeedStallDetector.MonitorId, trend);
	}

	#endregion
}
