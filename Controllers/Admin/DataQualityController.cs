using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MonitorApi.Models.Admin;
using MonitorApi.Services.Admin;

namespace MonitorApi.Controllers.Admin;

/// <summary>
/// Data quality for the admin dashboard: how good the data the estate publishes actually is, as
/// opposed to whether it is arriving. Every endpoint requires the admin token as the <c>token</c>
/// query parameter.
/// </summary>
/// <remarks>
/// Derives from <see cref="AdminControllerBase"/> rather than <c>MonitorControllerBase</c>, which is
/// the shape of the difference: this reads <c>feed_quality</c> and needs none of the ingestion-history
/// loaders the monitors share, and it dates itself from its own source table rather than from
/// <c>opportunity_ingestion</c>.
/// </remarks>
public class DataQualityController(IOptions<BigQueryOptions> bigQueryOptions, IOptions<ApiOptions> apiOptions)
	: AdminControllerBase(bigQueryOptions, apiOptions)
{
	/// <summary>
	/// Feed Quality
	/// </summary>
	/// <remarks>
	/// Every assessed feed in <c>feed_quality</c> with its quality assessment, best-scoring first, and
	/// a <c>summary</c> describing the whole set alongside them.
	///
	/// One row per feed. A dataset appears once per feed it publishes, so <c>dataset_url</c> and
	/// <c>dataset_name</c> repeat down the page; <c>summary.total_datasets</c> is the distinct count.
	///
	/// The things worth knowing:
	///
	/// - **The response has a third key.** Every other admin endpoint answers with <c>data</c> and
	///   <c>meta</c>; this one adds <c>summary</c>. <c>data</c> and <c>meta</c> are unchanged, and
	///   paging works identically.
	/// - **`summary` describes the filtered set, not the page.** It covers every row the filters
	///   matched and does not change as you walk the pages, so it can be read once and the rows paged
	///   beneath it. It is reduced from those same rows, so the two can never disagree.
	/// - **Counts account for every feed.** A feed whose <c>status</c>, <c>grade</c>, <c>feed_type</c>
	///   or <c>feed_version</c> is missing is counted as <c>unknown</c> rather than dropped, so each
	///   breakdown's <c>feed_count</c> sums to <c>summary.total_feeds</c>. <c>status</c> and
	///   <c>grade</c> are matched case-insensitively.
	/// - **Averages are unweighted means over the feeds that report the value.** A feed whose
	///   completeness is <c>null</c> is excluded from both the numerator and the denominator, and each
	///   mean carries the <c>feeds_reporting</c> it was taken over — read that before reading the mean,
	///   because the denominators differ sharply between properties. A property no feed reports
	///   averages to <c>null</c>, never to <c>0</c>.
	/// - **`null` is not zero anywhere in this response.** Every measured field is nullable because
	///   every column but <c>feed_id</c> and <c>dataset_url</c> is, and <c>null</c> always means the
	///   assessment did not report the value.
	/// - **`warnings`, `errors` and `missing_required_fields` are passed through exactly as stored.**
	///   Their shape is the assessor's, not this API's. Nothing here counts, parses or aggregates them,
	///   and no summary figure is derived from them — <c>summary.feeds_with_errors</c> counts feeds
	///   whose <c>status</c> is <c>ERROR</c>, which is not the same thing.
	/// - **Filters combine as they do across the API.** Values within one parameter are OR'd, and
	///   different parameters are AND'd. Both accept repeated (<c>?publisher=a&amp;publisher=b</c>) and
	///   comma-separated (<c>?publisher=a,b</c>) values, matched exactly. <c>publisher</c> is resolved
	///   through <c>feeds</c>, which is where publisher identity lives; <c>feed_quality</c> has no
	///   publisher column.
	///
	/// This is not a monitor, and three things follow. It takes no date parameter of any kind — no
	/// <c>as_of</c>, no lookback — because <c>feed_quality</c> holds current state only and a past date
	/// cannot be answered. There is no sibling trend endpoint, and no tile on <c>/admin/summary</c>,
	/// which lists incident monitors. And the row carries no <c>first_detected</c>, <c>days_open</c>,
	/// <c>consecutive_days</c>, <c>past_threshold</c>, <c>status</c> of the incident kind or
	/// <c>trend</c> — those fields are absent rather than null.
	///
	/// <c>meta.snapshot_date</c> is the latest <c>last_assessed</c> across the table, which is the one
	/// place this surface's <c>snapshot_date</c> does not come from <c>opportunity_ingestion</c>: the
	/// assessments are written by their own pipeline and dating them from the ingestion run would label
	/// them with a day the assessor may not have run. Per-feed <c>last_assessed</c> may be older.
	///
	/// Results are cached until the next daily refresh, varying by all query parameters.
	/// </remarks>
	/// <param name="page">One-based page number. Default <c>1</c>.</param>
	/// <param name="page_size">Rows per page. Default <c>500</c>, capped at <c>1000</c>.</param>
	/// <param name="dataset_url">One or more dataset URLs, matched exactly. A feed matches if any of the supplied values is its dataset. Accepts repeated (<c>?dataset_url=a&amp;dataset_url=b</c>) or comma-separated (<c>?dataset_url=a,b</c>) values.</param>
	/// <param name="publisher">One or more publisher names, matched exactly, resolved through <c>feeds</c>. Same repeated/comma-separated forms as <c>dataset_url</c>.</param>
	[HttpGet("feed-quality")]
	[ProducesResponseType(typeof(AdminSummarisedPage<FeedQualityRow, FeedQualitySummary>), StatusCodes.Status200OK)]
	public async Task<ActionResult<AdminSummarisedPage<FeedQualityRow, FeedQualitySummary>>> FeedQuality(
		int page = 1,
		int page_size = DefaultPageSize,
		[FromQuery] string[]? dataset_url = null,
		[FromQuery] string[]? publisher = null)
	{
		var datasetUrls = NormaliseMultiValue(dataset_url);
		var publishers = NormaliseMultiValue(publisher);

		var snapshotDate = await ResolveSnapshotDate();

		var assessments = await LoadFeedQuality(datasetUrls, publishers);
		var rows = assessments.Select(ToRow).ToList();
		var summary = FeedQualitySummariser.Summarise(assessments);

		return Ok(PaginateWithSummary(rows, summary, page, page_size, snapshotDate));
	}

	#region Utilities

	/// <summary>
	/// The day the assessments describe: the latest <c>last_assessed</c> in <c>feed_quality</c>, or
	/// today when the table is empty.
	/// </summary>
	/// <remarks>
	/// Deliberately <em>not</em> the <c>opportunity_ingestion</c> day every other admin endpoint uses.
	/// <c>feed_quality</c> is written by its own assessment pipeline and carries a real per-row
	/// assessment timestamp, so dating it from the ingestion pipeline would label the figures with a
	/// day the assessment may not have run — the opposite of what <c>snapshot_date</c> promises. The
	/// orphaned-children monitor borrows the ingestion day precisely because <c>opportunities</c> has
	/// no timestamp of its own to use; this table does.
	/// </remarks>
	private async Task<DateOnly> ResolveSnapshotDate()
	{
		var row = await QuerySingle(FeedQualityQuery.SnapshotDateSql(Fq(Tables.FeedQuality)));

		return FeedQualityQuery.ParseSnapshotDate(row) ?? DateOnly.FromDateTime(DateTime.UtcNow);
	}

	/// <summary>
	/// Loads every assessment matching the filters, in one query.
	/// </summary>
	/// <remarks>
	/// The whole filtered set is loaded rather than one page, because the summary has to describe all
	/// of it and is reduced from these very rows — a second aggregate query could disagree with the
	/// page beneath it. That is affordable here and nowhere else in the admin surface:
	/// <c>feed_quality</c> holds one row per feed, so this is thousands of rows rather than the
	/// millions <c>opportunities</c> would return.
	/// </remarks>
	private async Task<List<FeedQuality>> LoadFeedQuality(
		IReadOnlyCollection<string> datasetUrls,
		IReadOnlyCollection<string> publishers)
	{
		var rows = await Query(
			FeedQualityQuery.FeedQualitySql(
				Fq(Tables.FeedQuality),
				Fq(Tables.Feeds),
				filterByDatasetUrl: datasetUrls.Count > 0,
				filterByPublisher: publishers.Count > 0),
			FeedQualityQuery.FeedQualityParameters(datasetUrls, publishers));

		return await rows.Select(FeedQualityQuery.ParseFeedQuality).ToListAsync();
	}

	/// <summary>
	/// Hydrates one assessment into the dashboard payload, deriving the display name and publisher slug
	/// through <see cref="DatasetMetadata"/> so they are the same values every other admin endpoint
	/// reports for that dataset.
	/// </summary>
	private static FeedQualityRow ToRow(FeedQuality feed)
	{
		var dataset = new DatasetMetadata(feed.DatasetUrl, feed.DatasetName, feed.PublisherName);

		return new FeedQualityRow
		{
			FeedId = feed.FeedId,
			FeedUrl = feed.FeedUrl,
			FeedType = feed.FeedType,
			FeedVersion = feed.FeedVersion,
			IsRegular = feed.IsRegular,
			DatasetUrl = feed.DatasetUrl,
			DatasetName = dataset.Name,
			PublisherId = dataset.PublisherId,
			PublisherName = dataset.PublisherName ?? "",
			Status = feed.Status,
			Grade = feed.Grade,
			Score = feed.Score,
			NumFutureOpportunityItems = feed.NumFutureOpportunityItems,
			Completeness = new FeedQualityRowCompleteness
			{
				Location = feed.Completeness.Location,
				StartDate = feed.Completeness.StartDate,
				EndDate = feed.Completeness.EndDate,
				Activities = feed.Completeness.Activities,
				Facilities = feed.Completeness.Facilities,
				AgeRange = feed.Completeness.AgeRange,
				Level = feed.Completeness.Level,
				AccessibilitySupport = feed.Completeness.AccessibilitySupport,
				GenderRestriction = feed.Completeness.GenderRestriction,
			},
			Warnings = feed.Warnings,
			Errors = feed.Errors,
			MissingRequiredFields = feed.MissingRequiredFields,
			LastAssessed = feed.LastAssessed,
		};
	}

	#endregion
}
