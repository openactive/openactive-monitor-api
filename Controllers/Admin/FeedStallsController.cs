using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MonitorApi.Models.Admin;
using MonitorApi.Services.Admin;

namespace MonitorApi.Controllers.Admin;

/// <summary>
/// Feed stall monitors for the admin dashboard: feeds that were publishing recently but have gone
/// quiet. Every endpoint requires the admin token as the <c>token</c> query parameter.
/// </summary>
public class FeedStallsController(IOptions<BigQueryOptions> bigQueryOptions, IOptions<ApiOptions> apiOptions)
	: AdminControllerBase(bigQueryOptions, apiOptions)
{
	/// <summary>
	/// Single Feed Stall Incidents
	/// </summary>
	/// <remarks>
	/// Feeds that published at least once within the lookback window but have since been silent for
	/// <c>stall_days</c> or more consecutive days, ordered longest-running first.
	///
	/// A day counts as published when the feed's ingestion rows for that day report at least one updated
	/// item. Days on which no ingestion run happened are not evidence of publishing, so they extend a
	/// silence rather than break it.
	///
	/// Datasets whose feeds have <em>all</em> gone quiet are excluded — that is a dataset-wide outage,
	/// reported by its own monitor rather than as a set of independent single-feed stalls.
	///
	/// <c>status</c> is always <c>open</c> and <c>last_contacted</c> is always <c>null</c>: outreach
	/// state needs an incident-tracking store, which does not exist yet.
	///
	/// Results are cached for fifteen minutes, varying by all query parameters.
	/// </remarks>
	/// <param name="page">One-based page number. Default <c>1</c>.</param>
	/// <param name="page_size">Rows per page. Default <c>500</c>, capped at <c>1000</c>.</param>
	/// <param name="lookback_days">How recently a feed must have published to count as live rather than retired. Default <c>120</c>.</param>
	/// <param name="stall_days">Consecutive silent days that open an incident. Default <c>5</c>.</param>
	/// <param name="past_threshold_days">Consecutive silent days that set <c>past_threshold</c>. Default <c>7</c>; never treated as looser than <c>stall_days</c>.</param>
	/// <param name="as_of">Evaluate as at this date instead of the latest day in the ingestion table. ISO <c>yyyy-MM-dd</c>.</param>
	[HttpGet("single-feed-stall-incidents")]
	[ProducesResponseType(typeof(AdminPage<StallIncident>), StatusCodes.Status200OK)]
	public async Task<ActionResult<AdminPage<StallIncident>>> SingleFeedStallIncidents(
		int page = 1,
		int page_size = DefaultPageSize,
		int lookback_days = 120,
		int stall_days = 5,
		int past_threshold_days = 7,
		[FromQuery] DateOnly? as_of = null)
	{
		var thresholds = BuildThresholds(lookback_days, stall_days, past_threshold_days, trendDays: null);

		var snapshotDate = await ResolveSnapshotDate(as_of);
		if (snapshotDate is null)
		{
			return Ok(Paginate(Array.Empty<StallIncident>(), page, page_size, as_of ?? DateOnly.FromDateTime(DateTime.UtcNow)));
		}

		var histories = await LoadHistories(
			snapshotDate.Value,
			thresholds.LookbackDays + thresholds.IncidentTrendDays,
			thresholds.IncidentTrendDays);
		var stalls = SingleFeedStallDetector.Detect(histories, snapshotDate.Value, thresholds);

		var metadata = await LoadFeedMetadata(stalls.Select(s => s.FeedId).ToList());
		var incidents = stalls.Select(stall => ToIncident(stall, metadata.GetValueOrDefault(stall.FeedId))).ToList();

		return Ok(Paginate(incidents, page, page_size, snapshotDate.Value));
	}

	/// <summary>
	/// Single Feed Stall Trend
	/// </summary>
	/// <remarks>
	/// Open single-feed stall counts for each of the last <c>trend_days</c> days, oldest first. Each day
	/// is evaluated independently against the same rules as
	/// <see cref="SingleFeedStallIncidents(int, int, int, int, int, DateOnly?)"/>, so a point shows what
	/// that endpoint would have reported on that day. <c>past_threshold_count</c> is always a subset of
	/// <c>open_count</c>.
	///
	/// Results are cached for fifteen minutes, varying by all query parameters.
	/// </remarks>
	/// <param name="page">One-based page number. Default <c>1</c>.</param>
	/// <param name="page_size">Rows per page. Default <c>500</c>, capped at <c>1000</c>.</param>
	/// <param name="trend_days">Days of history to return. Default <c>30</c>.</param>
	/// <param name="lookback_days">How recently a feed must have published to count as live rather than retired. Default <c>120</c>.</param>
	/// <param name="stall_days">Consecutive silent days that open an incident. Default <c>5</c>.</param>
	/// <param name="past_threshold_days">Consecutive silent days counted into <c>past_threshold_count</c>. Default <c>7</c>.</param>
	/// <param name="as_of">Evaluate as at this date instead of the latest day in the ingestion table. ISO <c>yyyy-MM-dd</c>.</param>
	[HttpGet("single-feed-stall-trend")]
	[ProducesResponseType(typeof(AdminPage<StallTrendPoint>), StatusCodes.Status200OK)]
	public async Task<ActionResult<AdminPage<StallTrendPoint>>> SingleFeedStallTrend(
		int page = 1,
		int page_size = DefaultPageSize,
		int trend_days = 30,
		int lookback_days = 120,
		int stall_days = 5,
		int past_threshold_days = 7,
		[FromQuery] DateOnly? as_of = null)
	{
		var thresholds = BuildThresholds(lookback_days, stall_days, past_threshold_days, trend_days);

		var snapshotDate = await ResolveSnapshotDate(as_of);
		if (snapshotDate is null)
		{
			return Ok(Paginate(Array.Empty<StallTrendPoint>(), page, page_size, as_of ?? DateOnly.FromDateTime(DateTime.UtcNow)));
		}

		var histories = await LoadHistories(
			snapshotDate.Value,
			thresholds.RequiredHistoryDays,
			thresholds.IncidentTrendDays);
		var trend = SingleFeedStallDetector.Trend(histories, snapshotDate.Value, thresholds);

		var points = trend
			.Select(p => new StallTrendPoint
			{
				Date = p.Date,
				OpenCount = p.OpenCount,
				PastThresholdCount = p.PastThresholdCount,
			})
			.ToList();

		return Ok(Paginate(points, page, page_size, snapshotDate.Value));
	}

	#region Utilities

	private static SingleFeedStallThresholds BuildThresholds(int lookbackDays, int stallDays, int pastThresholdDays, int? trendDays)
	{
		var thresholds = new SingleFeedStallThresholds
		{
			LookbackDays = Math.Clamp(lookbackDays, 1, 730),
			StallDays = Math.Clamp(stallDays, 1, 365),
			PastThresholdDays = Math.Clamp(pastThresholdDays, 1, 365),
		};

		return trendDays is null
			? thresholds
			: thresholds with { TrendDays = Math.Clamp(trendDays.Value, 1, 365) };
	}

	/// <summary>
	/// The day to evaluate against: the caller's <c>as_of</c>, else the latest day in the ingestion
	/// table. Returns <c>null</c> when the table is empty.
	/// </summary>
	private async Task<DateOnly?> ResolveSnapshotDate(DateOnly? asOf)
	{
		if (asOf is not null)
		{
			return asOf;
		}

		var row = await QuerySingle(IngestionHistoryQuery.SnapshotDateSql(Fq(Tables.OpportunityIngestion)));
		return row?.GetValueOrDefault("snapshot_date") is DateTime snapshot
			? DateOnly.FromDateTime(snapshot)
			: null;
	}

	/// <param name="snapshotDate">The day the analysis runs against; the window ends here.</param>
	/// <param name="historyDays">Days of publishing history to load for detection.</param>
	/// <param name="trendDays">
	/// Trailing days for which the daily <c>updated</c> counts are also loaded, to fill the per-incident
	/// trend column.
	/// </param>
	private async Task<List<FeedIngestionHistory>> LoadHistories(DateOnly snapshotDate, int historyDays, int trendDays)
	{
		var rows = await Query(
			IngestionHistoryQuery.HistorySql(Fq(Tables.OpportunityIngestion)),
			IngestionHistoryQuery.HistoryParameters(
				snapshotDate.AddDays(-historyDays),
				snapshotDate,
				snapshotDate.AddDays(-(trendDays - 1))));

		return await rows.Select(IngestionHistoryQuery.ParseHistory).ToListAsync();
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
	/// Hydrates a detected stall into the dashboard payload. <paramref name="metadata"/> is null when the
	/// feed appears in the ingestion table but has no <c>feeds</c> row; the incident is still reported,
	/// with the descriptive fields left empty.
	/// </summary>
	private static StallIncident ToIncident(SingleFeedStall stall, FeedMetadata? metadata)
	{
		var feed = metadata ?? new FeedMetadata(stall.FeedId, null, null, null, null);

		return new StallIncident
		{
			MonitorId = SingleFeedStallDetector.MonitorId,
			PublisherId = feed.PublisherId,
			PublisherName = feed.PublisherName ?? "",
			FeedId = stall.FeedId,
			FeedName = feed.FeedName,
			FeedType = feed.FeedType,
			FeedUrl = feed.FeedUrl,
			FirstDetected = stall.LastPublished,
			// Under the current model an incident opens the day the feed goes quiet and stays open until
			// it publishes again, so days open and consecutive silent days always coincide. They would
			// diverge once incidents are tracked and resolved independently of the raw signal.
			DaysOpen = stall.ConsecutiveDays,
			ConsecutiveDays = stall.ConsecutiveDays,
			PastThreshold = stall.PastThreshold,
			Status = "open",
			LastContacted = null,
			Trend = stall.Trend,
			Detail = new StallIncidentDetail { LastModified = stall.LastPublished },
			QualityScore = feed.QualityScore,
		};
	}

	#endregion
}
