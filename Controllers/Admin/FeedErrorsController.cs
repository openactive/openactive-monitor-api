using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MonitorApi.Models.Admin;
using MonitorApi.Services.Admin;

namespace MonitorApi.Controllers.Admin;

/// <summary>
/// Feed ingestion error monitors for the admin dashboard: feeds whose ingestion is failing now but was
/// completing recently. Every endpoint requires the admin token as the <c>token</c> query parameter.
/// </summary>
public class FeedErrorsController(IOptions<BigQueryOptions> bigQueryOptions, IOptions<ApiOptions> apiOptions)
	: MonitorControllerBase(bigQueryOptions, apiOptions)
{
	/// <summary>
	/// Feed Ingestion Error Incidents
	/// </summary>
	/// <remarks>
	/// Feeds whose ingestion failed on the snapshot day but completed at least once in the
	/// <c>success_lookback_days</c> days before it, ordered longest-failing first.
	///
	/// A day counts as failed when the feed's <c>opportunity_ingestion</c> rows for that day report
	/// <c>ERROR</c> and none of them report <c>COMPLETE</c> — where a feed was polled more than once,
	/// success wins, because the feed did deliver. Days with no ingestion run at all are not evidence of
	/// failure, so they cannot open an incident.
	///
	/// Two exclusions keep this to failures somebody can act on:
	///
	/// - Feeds that have not completed an ingestion within <c>success_lookback_days</c> are treated as
	///   permanently broken rather than newly failing, and are left out.
	/// - Failures whose <c>error_code</c> is <c>401</c> or <c>403</c> are left out: an authorisation
	///   failure is a credentials problem rather than a broken feed, and gets its own monitor.
	///
	/// <c>status</c> is always <c>open</c> and <c>last_contacted</c> is always <c>null</c>: outreach
	/// state needs an incident-tracking store, which does not exist yet.
	///
	/// Results are cached until the next daily refresh, varying by all query parameters.
	/// </remarks>
	/// <param name="page">One-based page number. Default <c>1</c>.</param>
	/// <param name="page_size">Rows per page. Default <c>500</c>, capped at <c>1000</c>.</param>
	/// <param name="success_lookback_days">How recently the feed must have completed an ingestion for its current failure to count as a regression. Default <c>15</c>.</param>
	/// <param name="error_days">Days of failure that open an incident. Default <c>1</c> — a single failing day is enough.</param>
	/// <param name="past_threshold_days">Days of failure that set <c>past_threshold</c>. Default <c>3</c>; never treated as looser than <c>error_days</c>.</param>
	/// <param name="as_of">Evaluate as at this date instead of the latest day in the ingestion table. ISO <c>yyyy-MM-dd</c>.</param>
	[HttpGet("feed-ingestion-error-incidents")]
	[ProducesResponseType(typeof(AdminPage<IngestionErrorIncident>), StatusCodes.Status200OK)]
	public async Task<ActionResult<AdminPage<IngestionErrorIncident>>> FeedIngestionErrorIncidents(
		int page = 1,
		int page_size = DefaultPageSize,
		int success_lookback_days = 15,
		int error_days = 1,
		int past_threshold_days = 3,
		[FromQuery] DateOnly? as_of = null)
	{
		var thresholds = BuildThresholds(success_lookback_days, error_days, past_threshold_days, trendDays: null);

		var snapshotDate = await ResolveSnapshotDate(as_of);
		if (snapshotDate is null)
		{
			return Ok(Paginate(
				Array.Empty<IngestionErrorIncident>(), page, page_size, as_of ?? DateOnly.FromDateTime(DateTime.UtcNow)));
		}

		var histories = await LoadStatusHistories(snapshotDate.Value, thresholds.IncidentHistoryDays);
		var errors = FeedIngestionErrorDetector.Detect(histories, snapshotDate.Value, thresholds);

		var metadata = await LoadFeedMetadata(errors.Select(e => e.FeedId).ToList());
		var incidents = errors.Select(error => ToIncident(error, metadata.GetValueOrDefault(error.FeedId))).ToList();

		return Ok(Paginate(incidents, page, page_size, snapshotDate.Value));
	}

	/// <summary>
	/// Feed Ingestion Error Trend
	/// </summary>
	/// <remarks>
	/// Open ingestion error counts for each of the last <c>trend_days</c> days, oldest first. Each day is
	/// evaluated independently against the same rules as
	/// <see cref="FeedIngestionErrorIncidents(int, int, int, int, int, DateOnly?)"/>, so a point shows
	/// what that endpoint would have reported on that day. <c>past_threshold_count</c> is always a subset
	/// of <c>open_count</c>.
	///
	/// One caveat on the older end of the series: <c>error_code</c> was added to
	/// <c>opportunity_ingestion</c> only recently, so on days that carry no codes the <c>401</c>/<c>403</c>
	/// exclusion cannot apply and those points count authorisation failures as ingestion errors. Expect a
	/// step down on the first day carrying codes.
	///
	/// Results are cached until the next daily refresh, varying by all query parameters.
	/// </remarks>
	/// <param name="page">One-based page number. Default <c>1</c>.</param>
	/// <param name="page_size">Rows per page. Default <c>500</c>, capped at <c>1000</c>.</param>
	/// <param name="trend_days">Days of history to return. Default <c>30</c>.</param>
	/// <param name="success_lookback_days">How recently the feed must have completed an ingestion for its failure to count as a regression. Default <c>15</c>.</param>
	/// <param name="error_days">Days of failure that open an incident. Default <c>1</c>.</param>
	/// <param name="past_threshold_days">Days of failure counted into <c>past_threshold_count</c>. Default <c>3</c>.</param>
	/// <param name="as_of">Evaluate as at this date instead of the latest day in the ingestion table. ISO <c>yyyy-MM-dd</c>.</param>
	[HttpGet("feed-ingestion-error-trend")]
	[ProducesResponseType(typeof(AdminPage<IngestionErrorTrendPoint>), StatusCodes.Status200OK)]
	public async Task<ActionResult<AdminPage<IngestionErrorTrendPoint>>> FeedIngestionErrorTrend(
		int page = 1,
		int page_size = DefaultPageSize,
		int trend_days = 30,
		int success_lookback_days = 15,
		int error_days = 1,
		int past_threshold_days = 3,
		[FromQuery] DateOnly? as_of = null)
	{
		var thresholds = BuildThresholds(success_lookback_days, error_days, past_threshold_days, trend_days);

		var snapshotDate = await ResolveSnapshotDate(as_of);
		if (snapshotDate is null)
		{
			return Ok(Paginate(
				Array.Empty<IngestionErrorTrendPoint>(), page, page_size, as_of ?? DateOnly.FromDateTime(DateTime.UtcNow)));
		}

		var histories = await LoadStatusHistories(snapshotDate.Value, thresholds.RequiredHistoryDays);
		var trend = FeedIngestionErrorDetector.Trend(histories, snapshotDate.Value, thresholds);

		var points = trend
			.Select(p => new IngestionErrorTrendPoint
			{
				Date = p.Date,
				OpenCount = p.OpenCount,
				PastThresholdCount = p.PastThresholdCount,
			})
			.ToList();

		return Ok(Paginate(points, page, page_size, snapshotDate.Value));
	}

	#region Utilities

	private static FeedIngestionErrorThresholds BuildThresholds(
		int successLookbackDays,
		int errorDays,
		int pastThresholdDays,
		int? trendDays)
	{
		var thresholds = new FeedIngestionErrorThresholds
		{
			SuccessLookbackDays = Math.Clamp(successLookbackDays, 1, 730),
			ErrorDays = Math.Clamp(errorDays, 1, 365),
			PastThresholdDays = Math.Clamp(pastThresholdDays, 1, 365),
		};

		return trendDays is null
			? thresholds
			: thresholds with { TrendDays = Math.Clamp(trendDays.Value, 1, 365) };
	}

	private async Task<Dictionary<string, FeedMetadata>> LoadFeedMetadata(IReadOnlyCollection<string> feedIds)
	{
		if (feedIds.Count == 0)
		{
			return [];
		}

		var rows = await Query(
			IngestionHistoryQuery.FeedMetadataSql(Fq(Tables.Feeds), Fq(Tables.FeedQuality)),
			IngestionHistoryQuery.FeedMetadataParameters(feedIds));

		var metadata = new Dictionary<string, FeedMetadata>();
		await foreach (var row in rows)
		{
			var record = IngestionHistoryQuery.ParseFeedMetadata(row);
			metadata[record.FeedId] = record;
		}

		return metadata;
	}

	/// <summary>
	/// Hydrates a detected error into the dashboard payload. <paramref name="metadata"/> is null when the
	/// feed appears in the ingestion table but has no <c>feeds</c> row; the incident is still reported,
	/// with the descriptive fields left empty.
	/// </summary>
	private static IngestionErrorIncident ToIncident(FeedIngestionError error, FeedMetadata? metadata)
	{
		var feed = metadata ?? new FeedMetadata(error.FeedId, null, null, null, null);

		return new IngestionErrorIncident
		{
			MonitorId = FeedIngestionErrorDetector.MonitorId,
			PublisherId = feed.PublisherId,
			PublisherName = feed.PublisherName ?? "",
			FeedId = error.FeedId,
			FeedName = feed.FeedName,
			FeedType = feed.FeedType,
			FeedUrl = feed.FeedUrl,
			// The day after the last completed ingestion: the first day the feed can be shown to have
			// been failing.
			FirstDetected = error.LastCompleted.AddDays(1),
			// As with the stall monitor, an incident opens when the signal starts and stays open until it
			// clears, so days open and consecutive failing days always coincide. They would diverge once
			// incidents are tracked and resolved independently of the raw signal.
			DaysOpen = error.ConsecutiveDays,
			ConsecutiveDays = error.ConsecutiveDays,
			PastThreshold = error.PastThreshold,
			Status = "open",
			LastContacted = null,
			Trend = error.Trend,
			Detail = new IngestionErrorIncidentDetail
			{
				ErrorCode = error.ErrorCode,
				ErrorMessage = error.ErrorMessage,
				LastCompleted = error.LastCompleted,
			},
			QualityScore = feed.QualityScore,
		};
	}

	#endregion
}
