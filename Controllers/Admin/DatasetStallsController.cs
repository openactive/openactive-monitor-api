using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MonitorApi.Models.Admin;
using MonitorApi.Services.Admin;

namespace MonitorApi.Controllers.Admin;

/// <summary>
/// Dataset-wide stall monitors for the admin dashboard: datasets in which every feed has stopped
/// publishing, leaving all of their downstream data frozen. Every endpoint requires the admin token as
/// the <c>token</c> query parameter.
/// </summary>
/// <remarks>
/// The dataset-scoped counterpart of <see cref="FeedStallsController"/>, kept on its own controller
/// because it reports a different entity: one incident per dataset rather than per feed. The two read
/// the same <c>opportunity_ingestion</c> history through <see cref="MonitorControllerBase"/> and, at
/// equal thresholds, partition the silence between them — a silent feed is reported by one monitor or
/// the other, never by both.
/// </remarks>
public class DatasetStallsController(IOptions<BigQueryOptions> bigQueryOptions, IOptions<ApiOptions> apiOptions)
	: MonitorControllerBase(bigQueryOptions, apiOptions)
{
	/// <summary>
	/// Dataset Stall Incidents
	/// </summary>
	/// <remarks>
	/// Datasets in which <em>every</em> feed has been silent for <c>stall_days</c> or more consecutive
	/// days, ordered longest-running first. Nothing new or updated is reaching consumers from the
	/// publisher at all, so the whole dataset is frozen rather than one part of it.
	///
	/// A dataset counts as publishing on any day one of its feeds reported at least one updated item, so
	/// it goes quiet only when its last remaining feed does, and it has been silent for as long as its
	/// most recently active feed has. Days on which no ingestion run happened are not evidence of
	/// publishing, so they extend a silence rather than break it.
	///
	/// Two datasets are deliberately not incidents:
	///
	/// - One that never published inside the lookback window — there is nothing to say it ever worked.
	/// - One silent for longer than <c>lookback_days</c>, which is retired rather than stalled.
	///
	/// This monitor and <c>/admin/single-feed-stall-incidents</c> divide the same signal between them:
	/// that one excludes datasets whose feeds have all gone quiet, precisely so they are reported here
	/// once instead of as a handful of unrelated feed stalls. At the default thresholds no feed appears
	/// in both, and <c>detail.feeds</c> lists the feeds this incident accounts for.
	///
	/// <c>status</c> is always <c>open</c> and <c>last_contacted</c> is always <c>null</c>: outreach
	/// state needs an incident-tracking store, which does not exist yet.
	///
	/// Results are cached until the next daily refresh, varying by all query parameters.
	/// </remarks>
	/// <param name="page">One-based page number. Default <c>1</c>.</param>
	/// <param name="page_size">Rows per page. Default <c>500</c>, capped at <c>1000</c>.</param>
	/// <param name="lookback_days">How recently the dataset must have published to count as live rather than retired. Default <c>120</c>.</param>
	/// <param name="stall_days">Consecutive days with no feed publishing that open an incident. Default <c>5</c>.</param>
	/// <param name="past_threshold_days">Consecutive silent days that set <c>past_threshold</c>. Default <c>7</c>; never treated as looser than <c>stall_days</c>.</param>
	/// <param name="as_of">Evaluate as at this date instead of the latest day in the ingestion table. ISO <c>yyyy-MM-dd</c>.</param>
	[HttpGet("dataset-stall-incidents")]
	[ProducesResponseType(typeof(AdminPage<DatasetStallIncident>), StatusCodes.Status200OK)]
	public async Task<ActionResult<AdminPage<DatasetStallIncident>>> DatasetStallIncidents(
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
			return Ok(Paginate(
				Array.Empty<DatasetStallIncident>(), page, page_size, as_of ?? DateOnly.FromDateTime(DateTime.UtcNow)));
		}

		var histories = await LoadHistories(
			snapshotDate.Value,
			thresholds.LookbackDays + thresholds.IncidentTrendDays,
			thresholds.IncidentTrendDays,
			ignoreFirstIngestionDate: true);
		var stalls = DatasetStallDetector.Detect(histories, snapshotDate.Value, thresholds);

		var datasets = await LoadDatasetMetadata(stalls.Select(s => s.DatasetId).ToList());
		var feeds = await LoadFeedMetadata(stalls.SelectMany(s => s.Feeds).Select(f => f.FeedId).ToList());

		var incidents = stalls
			.Select(stall => ToIncident(stall, datasets.GetValueOrDefault(stall.DatasetId), feeds))
			.ToList();

		return Ok(Paginate(incidents, page, page_size, snapshotDate.Value));
	}

	/// <summary>
	/// Dataset Stall Trend
	/// </summary>
	/// <remarks>
	/// Open dataset-wide stall counts for each of the last <c>trend_days</c> days, oldest first. Each day
	/// is evaluated independently against the same rules as
	/// <see cref="DatasetStallIncidents(int, int, int, int, int, DateOnly?)"/>, so a point shows what
	/// that endpoint would have reported on that day. <c>past_threshold_count</c> is always a subset of
	/// <c>open_count</c>.
	///
	/// Results are cached until the next daily refresh, varying by all query parameters.
	/// </remarks>
	/// <param name="page">One-based page number. Default <c>1</c>.</param>
	/// <param name="page_size">Rows per page. Default <c>500</c>, capped at <c>1000</c>.</param>
	/// <param name="trend_days">Days of history to return. Default <c>30</c>.</param>
	/// <param name="lookback_days">How recently the dataset must have published to count as live rather than retired. Default <c>120</c>.</param>
	/// <param name="stall_days">Consecutive days with no feed publishing that open an incident. Default <c>5</c>.</param>
	/// <param name="past_threshold_days">Consecutive silent days counted into <c>past_threshold_count</c>. Default <c>7</c>.</param>
	/// <param name="as_of">Evaluate as at this date instead of the latest day in the ingestion table. ISO <c>yyyy-MM-dd</c>.</param>
	[HttpGet("dataset-stall-trend")]
	[ProducesResponseType(typeof(AdminPage<DatasetStallTrendPoint>), StatusCodes.Status200OK)]
	public async Task<ActionResult<AdminPage<DatasetStallTrendPoint>>> DatasetStallTrend(
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
			return Ok(Paginate(
				Array.Empty<DatasetStallTrendPoint>(), page, page_size, as_of ?? DateOnly.FromDateTime(DateTime.UtcNow)));
		}

		var histories = await LoadHistories(
			snapshotDate.Value,
			thresholds.RequiredHistoryDays,
			thresholds.IncidentTrendDays,
			ignoreFirstIngestionDate: true);
		var trend = DatasetStallDetector.Trend(histories, snapshotDate.Value, thresholds);

		var points = trend
			.Select(p => new DatasetStallTrendPoint
			{
				Date = p.Date,
				OpenCount = p.OpenCount,
				PastThresholdCount = p.PastThresholdCount,
			})
			.ToList();

		return Ok(Paginate(points, page, page_size, snapshotDate.Value));
	}

	#region Utilities

	private static DatasetStallThresholds BuildThresholds(int lookbackDays, int stallDays, int pastThresholdDays, int? trendDays)
	{
		var thresholds = new DatasetStallThresholds
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
	/// Hydrates a detected dataset stall into the dashboard payload. <paramref name="metadata"/> is null
	/// when the dataset appears in the ingestion table but has no <c>feeds</c> row; the incident is still
	/// reported, with the descriptive fields left empty, as is each feed missing from
	/// <paramref name="feeds"/>.
	/// </summary>
	private static DatasetStallIncident ToIncident(
		DatasetStall stall,
		DatasetMetadata? metadata,
		IReadOnlyDictionary<string, FeedMetadata> feeds)
	{
		var dataset = metadata ?? new DatasetMetadata(stall.DatasetId, null, null);

		return new DatasetStallIncident
		{
			MonitorId = DatasetStallDetector.MonitorId,
			PublisherId = dataset.PublisherId,
			PublisherName = dataset.PublisherName ?? "",
			DatasetUrl = stall.DatasetId,
			DatasetName = dataset.Name,
			FeedCount = stall.Feeds.Count,
			FirstDetected = stall.LastPublished,
			// As on the single-feed monitor, an incident opens the day the dataset goes quiet and stays
			// open until something publishes again, so days open and consecutive silent days always
			// coincide. They would diverge once incidents are tracked and resolved independently of the
			// raw signal.
			DaysOpen = stall.ConsecutiveDays,
			ConsecutiveDays = stall.ConsecutiveDays,
			PastThreshold = stall.PastThreshold,
			Status = "open",
			LastContacted = null,
			Trend = stall.Trend,
			Detail = new DatasetStallIncidentDetail
			{
				LastModified = stall.LastPublished,
				Feeds = stall.Feeds
					.Select(feed => new DatasetStallFeed
					{
						FeedId = feed.FeedId,
						FeedName = (feeds.GetValueOrDefault(feed.FeedId)
							?? new FeedMetadata(feed.FeedId, null, null, null, null)).FeedName,
						LastPublished = feed.LastPublished,
						ConsecutiveDays = feed.ConsecutiveDays,
					})
					.ToList(),
			},
		};
	}

	#endregion
}
