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
	/// - **Filters combine as they do across the API.** Values within one parameter are OR'd, and
	///   different parameters are AND'd.
	///
	/// This is not a monitor, and three things follow. It takes no date parameter of any kind — no
	/// <c>as_of</c>, no lookback — because <c>feed_quality</c> holds current state only and a past date
	/// cannot be answered.
	///
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

	/// <summary>
	/// Feed Custom Properties
	/// </summary>
	/// <remarks>
	/// Schema drift: every assessed feed in <c>custom_properties</c> that publishes at least one property
	/// outside the OpenActive vocabulary, most custom properties first, with the properties it uses and a
	/// <c>summary</c> describing the whole set alongside them.
	///
	/// The things worth knowing:
	///
	/// - **The response has a third key**, <c>summary</c>. It
	///   describes every row the filters matched, not the page, and is reduced from those same rows.
	/// - **`custom_properties` has one entry per (property, entity type)**, most widely present first, so
	///   a property used on both <c>SessionSeries</c> and <c>ScheduledSession</c> appears twice.
	///   <c>presence_pct</c> is the percentage (0–100) of the sampled instances of that type carrying the
	///   property, drawn from <c>sampled_items</c> sampled opportunities rather than the whole feed.
	///
	/// This is not a monitor. <c>custom_properties</c> is a snapshot with no history.
	///
	/// </remarks>
	/// <param name="page">One-based page number. Default <c>1</c>.</param>
	/// <param name="page_size">Rows per page. Default <c>500</c>, capped at <c>1000</c>.</param>
	/// <param name="dataset_url">One or more dataset URLs, matched exactly. A feed matches if any of the supplied values is its dataset. Accepts repeated (<c>?dataset_url=a&amp;dataset_url=b</c>) or comma-separated (<c>?dataset_url=a,b</c>) values.</param>
	/// <param name="publisher">One or more publisher names, matched exactly against the name stored with the assessment. Same repeated/comma-separated forms as <c>dataset_url</c>.</param>
	[HttpGet("feed-custom-properties")]
	[ProducesResponseType(typeof(AdminSummarisedPage<FeedCustomPropertiesRow, CustomPropertiesSummary>), StatusCodes.Status200OK)]
	public async Task<ActionResult<AdminSummarisedPage<FeedCustomPropertiesRow, CustomPropertiesSummary>>> FeedCustomProperties(
		int page = 1,
		int page_size = DefaultPageSize,
		[FromQuery] string[]? dataset_url = null,
		[FromQuery] string[]? publisher = null)
	{
		var datasetUrls = NormaliseMultiValue(dataset_url);
		var publishers = NormaliseMultiValue(publisher);

		var snapshotDate = await ResolveCustomPropertiesSnapshotDate();

		var assessed = await LoadFeedCustomProperties(datasetUrls, publishers);
		var rows = CustomPropertiesSummariser.Reported(assessed).Select(ToRow).ToList();
		var summary = CustomPropertiesSummariser.Summarise(assessed);

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

	/// <summary>
	/// The day the custom-property assessments describe: the latest <c>last_assessed</c> in
	/// <c>custom_properties</c>, or today when the table is empty. Dated from its own table for the
	/// reason <see cref="ResolveSnapshotDate"/> gives.
	/// </summary>
	private async Task<DateOnly> ResolveCustomPropertiesSnapshotDate()
	{
		var row = await QuerySingle(CustomPropertiesQuery.SnapshotDateSql(Fq(Tables.CustomProperties)));

		return CustomPropertiesQuery.ParseSnapshotDate(row) ?? DateOnly.FromDateTime(DateTime.UtcNow);
	}

	/// <summary>
	/// Loads every assessed feed matching the filters, with or without custom properties, in one query.
	/// </summary>
	/// <remarks>
	/// Feeds without custom properties are loaded too, and dropped by
	/// <see cref="CustomPropertiesSummariser.Reported"/> rather than in SQL: the summary needs them as
	/// its denominator, and keeping the rule in C# is what lets it be unit tested. Affordable for the
	/// same reason as <see cref="LoadFeedQuality"/> — one row per feed.
	/// </remarks>
	private async Task<List<FeedCustomProperties>> LoadFeedCustomProperties(
		IReadOnlyCollection<string> datasetUrls,
		IReadOnlyCollection<string> publishers)
	{
		var rows = await Query(
			CustomPropertiesQuery.FeedCustomPropertiesSql(
				Fq(Tables.CustomProperties),
				filterByDatasetUrl: datasetUrls.Count > 0,
				filterByPublisher: publishers.Count > 0),
			CustomPropertiesQuery.FeedCustomPropertiesParameters(datasetUrls, publishers));

		return await rows.Select(CustomPropertiesQuery.ParseFeedCustomProperties).ToListAsync();
	}

	/// <summary>
	/// Hydrates one feed's custom properties into the dashboard payload, with the display name and
	/// publisher slug derived through <see cref="DatasetMetadata"/> as everywhere else.
	/// </summary>
	private static FeedCustomPropertiesRow ToRow(FeedCustomProperties feed)
	{
		var dataset = new DatasetMetadata(feed.DatasetUrl, feed.DatasetName, feed.PublisherName);

		return new FeedCustomPropertiesRow
		{
			FeedId = feed.FeedId,
			FeedUrl = feed.FeedUrl,
			FeedType = feed.FeedType,
			IsRegular = feed.IsRegular,
			DatasetUrl = feed.DatasetUrl,
			DatasetName = dataset.Name,
			PublisherId = dataset.PublisherId,
			PublisherName = dataset.PublisherName ?? "",
			SampledItems = feed.SampledItems,
			NumCustomProperties = feed.NumCustomProperties,
			NumCustomPropertyUsages = feed.NumCustomPropertyUsages,
			CustomProperties = [.. feed.CustomProperties.Select(p => new FeedCustomProperty
			{
				Property = p.Property,
				Namespace = p.Namespace,
				EntityType = p.EntityType,
				PresencePct = p.PresencePct,
			})],
		};
	}

	#endregion
}
