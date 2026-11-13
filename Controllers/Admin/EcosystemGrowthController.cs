using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MonitorApi.Models.Admin;
using MonitorApi.Services.Admin;

namespace MonitorApi.Controllers.Admin;

/// <summary>
/// Ecosystem growth for the admin dashboard: how many datasets are crawled each month, which came into
/// and dropped out of the catalogues, which have nothing on offer, and which stopped publishing. Every
/// endpoint requires the admin token as the <c>token</c> query parameter.
/// </summary>
/// <remarks>
/// A month-by-month view rather than a daily monitor: one point per calendar month keeps the series
/// small enough to chart years of it, and a month is long enough that a dataset missed by one crawl is
/// not mistaken for one that has gone.
/// </remarks>
public class EcosystemGrowthController(IOptions<BigQueryOptions> bigQueryOptions, IOptions<ApiOptions> apiOptions)
	: MonitorControllerBase(bigQueryOptions, apiOptions)
{
	/// <summary>
	/// Ecosystem Growth Trend
	/// </summary>
	/// <remarks>
	/// One point per calendar month, oldest first, for the last <c>months</c> months up to <c>as_of</c>.
	/// Months from before the crawl history begins are left out, so the series can be shorter than
	/// <c>months</c>.
	///
	/// The month still being crawled is the last point, flagged `in_progress: true`, once it has at least
	/// <b>five</b> days of crawls. Until then the series ends at the last complete month. An in-progress
	/// point covers only the days so far and changes daily. Its "stopped" and "without future items"
	/// figures cover a shorter window than a complete month's, so treat it as provisional.
	///
	/// For each month:
	///
	/// - `datasets_crawled`: datasets the catalogues listed on at least one crawl (`feed_ingestion`),
	///   whatever came of crawling them. Agrees with `feed_ingestion.number_of_datasets`.
	/// - `datasets_added` / `datasets_removed`: listed this month and not the month before, and the
	///   reverse. `null` when the month before has no data to compare with. A publisher that moves to a
	///   new hostname counts as one removed and one added.
	/// - `datasets_not_ingested`: listed, but the pipeline ingested not a single one of its feeds all month.
	///   That is a pipeline fact, not a publisher one: the dataset page may block the crawler, have gone,
	///   or duplicate another dataset whose feeds are ingested under that one's URL. These datasets are left
	///   out of every figure below.
	/// - `datasets_with_future` / `datasets_without_future`: whether any of an ingested dataset's feeds
	///   reported a non-zero `total_future_opportunities` on any day. Every feed kind counts, `Slot`
	///   included. With `datasets_not_ingested`, these partition `datasets_crawled`.
	/// - `datasets_stopped`: ingested datasets whose `updated` + `deleted` summed to zero across all their
	///   feeds for the month. `datasets_stopped_all_runs_failed` is the subset whose every run ended in
	///   `ERROR`.
	///
	/// Each of those counts except `datasets_crawled` and `datasets_with_future` has a `_list` beside it
	/// (`datasets_added_list`, …) naming the datasets behind it: URL, display name and publisher, ordered
	/// by `dataset_name` then `dataset_url`. A list's length always equals its count, and a list is `null`
	/// exactly when its count is.
	///
	/// The activity figures (future, stopped, not ingested) come from `opportunity_ingestion` and are
	/// `null` for months before its history begins (2026-08-20). The crawl figures go back further.
	///
	/// A month's crawl list is the union of all its crawls, so a partial crawl or a missing day does not
	/// make a dataset look removed. `days_with_data` counts the crawls, and `days_with_feed_data` the days
	/// the activity figures are measured over.
	///
	/// Results are cached until the next daily refresh, varying by all query parameters.
	/// </remarks>
	/// <param name="page">One-based page number. Default <c>1</c>.</param>
	/// <param name="page_size">Rows per page. Default <c>500</c>, capped at <c>1000</c>.</param>
	/// <param name="months">Months to return, counting the month in progress when it is included. Default <c>12</c>, clamped to <c>1</c>..<c>60</c>.</param>
	/// <param name="as_of">Treat this date as the latest day of data instead of the latest day in the ingestion table. ISO <c>yyyy-MM-dd</c>.</param>
	[HttpGet("ecosystem-growth-trend")]
	[ProducesResponseType(typeof(AdminPage<EcosystemGrowthTrendPoint>), StatusCodes.Status200OK)]
	public async Task<ActionResult<AdminPage<EcosystemGrowthTrendPoint>>> EcosystemGrowthTrend(
		int page = 1,
		int page_size = DefaultPageSize,
		int months = EcosystemGrowthCalculator.DefaultMonths,
		[FromQuery] DateOnly? as_of = null)
	{
		var snapshotDate = await ResolveSnapshotDate(as_of);
		if (snapshotDate is null)
		{
			return Ok(Paginate(
				Array.Empty<EcosystemGrowthTrendPoint>(),
				page,
				page_size,
				as_of ?? DateOnly.FromDateTime(DateTime.UtcNow)));
		}

		var candidates = EcosystemGrowthCalculator.CandidateMonths(snapshotDate.Value, months);
		var reported = EcosystemGrowthCalculator.ReportableMonths(await Evaluate(candidates, snapshotDate.Value), months);

		// One metadata lookup for every dataset any month names, rather than one per month.
		var metadata = await LoadDatasetMetadata(reported
			.SelectMany(m => (m.Added ?? [])
				.Concat(m.Removed ?? [])
				.Concat(m.WithoutFuture ?? [])
				.Concat(m.Stopped ?? [])
				.Concat(m.NotIngested ?? []))
			.Distinct(StringComparer.Ordinal)
			.ToList());

		var points = reported
			.Select(month => new EcosystemGrowthTrendPoint
			{
				PeriodStart = month.PeriodStart,
				PeriodEnd = month.PeriodEnd,
				InProgress = month.InProgress,
				DaysWithData = month.DaysWithData,
				DaysWithFeedData = month.DaysWithFeedData,
				DatasetsCrawled = month.Crawled.Count,
				DatasetsAdded = month.Added?.Count,
				DatasetsAddedList = Describe(month.Added, metadata),
				DatasetsRemoved = month.Removed?.Count,
				DatasetsRemovedList = Describe(month.Removed, metadata),
				DatasetsWithFuture = month.WithFuture?.Count,
				DatasetsWithoutFuture = month.WithoutFuture?.Count,
				DatasetsWithoutFutureList = Describe(month.WithoutFuture, metadata),
				DatasetsStopped = month.Stopped?.Count,
				DatasetsStoppedList = Describe(month.Stopped, metadata),
				DatasetsStoppedAllRunsFailed = month.StoppedAllRunsFailed?.Count,
				DatasetsStoppedAllRunsFailedList = Describe(month.StoppedAllRunsFailed, metadata),
				DatasetsNotIngested = month.NotIngested?.Count,
				DatasetsNotIngestedList = Describe(month.NotIngested, metadata),
			})
			.ToList();

		return Ok(Paginate(points, page, page_size, snapshotDate.Value));
	}

	#region Utilities

	/// <summary>
	/// Loads the activity for <paramref name="months"/> and the month before the first of them — the
	/// baseline its added and removed figures are measured against — and evaluates each month.
	/// </summary>
	/// <remarks>
	/// The window stops at <paramref name="snapshot"/>, not at the last month's end: with an <c>as_of</c> in
	/// the past, the month in progress must not see the days after it.
	/// </remarks>
	private async Task<IReadOnlyList<EcosystemGrowthMonth>> Evaluate(IReadOnlyList<DateOnly> months, DateOnly snapshot)
	{
		var windowStart = months[0].AddMonths(-1);
		var lastMonthEnd = EcosystemGrowthCalculator.MonthEnd(months[^1]);
		var windowEnd = lastMonthEnd < snapshot ? lastMonthEnd : snapshot;

		var rows = await Query(
			EcosystemGrowthQuery.DatasetMonthsSql(Fq(Tables.FeedIngestion), Fq(Tables.OpportunityIngestion)),
			EcosystemGrowthQuery.DatasetMonthsParameters(windowStart, windowEnd));

		var catalogue = new List<CatalogueListing>();
		var activity = new List<DatasetMonthActivity>();
		var coverage = new List<MonthCoverage>();

		await foreach (var row in rows)
		{
			var (monthCoverage, listing, datasetActivity) = EcosystemGrowthQuery.ParseDatasetMonth(row);
			coverage.Add(monthCoverage);
			if (listing is not null)
			{
				catalogue.Add(listing);
			}
			if (datasetActivity is not null)
			{
				activity.Add(datasetActivity);
			}
		}

		return EcosystemGrowthCalculator.Compute(catalogue, activity, coverage, months, snapshot);
	}

	/// <summary>
	/// Hydrates dataset ids into list entries, ordered by display name and then URL; <c>null</c> stays
	/// <c>null</c>, so a list is unknown exactly when its count is. A dataset with no <c>feeds</c> or
	/// <c>feed_quality</c> row is still listed, named after its URL's host.
	/// </summary>
	private static IReadOnlyList<EcosystemGrowthDataset>? Describe(
		IEnumerable<string>? datasetIds,
		IReadOnlyDictionary<string, DatasetMetadata> metadata) =>
		datasetIds?
			.Select(id =>
			{
				var dataset = metadata.GetValueOrDefault(id) ?? new DatasetMetadata(id, null, null);
				return new EcosystemGrowthDataset
				{
					DatasetUrl = id,
					DatasetName = dataset.Name,
					PublisherId = dataset.PublisherId,
					PublisherName = dataset.PublisherName ?? "",
				};
			})
			.OrderBy(d => d.DatasetName, StringComparer.OrdinalIgnoreCase)
			.ThenBy(d => d.DatasetUrl, StringComparer.Ordinal)
			.ToList();

	#endregion
}
