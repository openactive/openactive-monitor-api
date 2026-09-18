using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MonitorApi.Models.Admin;
using MonitorApi.Services.Admin;

namespace MonitorApi.Controllers.Admin;

/// <summary>
/// Forward-supply monitors for the admin dashboard: datasets whose <c>total_future_opportunities</c> is
/// draining away even though their feeds keep ingesting successfully. Every endpoint requires the admin
/// token as the <c>token</c> query parameter.
/// </summary>
/// <remarks>
/// The gap <see cref="FeedStallsController"/>, <see cref="DatasetStallsController"/> and
/// <see cref="FeedErrorsController"/> leave between them. Those three report feeds that stopped or
/// failed; this one reports feeds that are working perfectly and still have less to offer every day.
/// It reads only <c>COMPLETE</c> ingestion runs, so it never re-reports a publisher whose outage those
/// monitors already own.
/// </remarks>
public class DatasetSupplyController(IOptions<BigQueryOptions> bigQueryOptions, IOptions<ApiOptions> apiOptions)
	: MonitorControllerBase(bigQueryOptions, apiOptions)
{
	/// <summary>
	/// Feed kinds this monitor does not report on, excluded before any rule is applied.
	/// </summary>
	/// <remarks>
	/// <c>Slot</c> is here because a slot's forward count is a function of how far ahead the publisher
	/// chooses to open bookings, not of how much it has to offer: a feed that publishes a rolling window
	/// of slots shows a decline every time that window shortens, which says nothing about supply drying
	/// up. The session and facility feeds carry the signal this monitor is about.
	///
	/// Read by <see cref="SummaryController"/> too, so the dashboard tile counts exactly what the
	/// incidents endpoint returns rather than quietly including the kinds it leaves out.
	/// </remarks>
	public static IReadOnlyList<string> IgnoredKinds { get; } = ["Slot"];

	/// <summary>
	/// Dataset Future Decline Incidents
	/// </summary>
	/// <remarks>
	/// Datasets losing forward supply over the last <c>window_days</c> days, ordered by the largest loss
	/// first. The signal is <c>total_future_opportunities</c> — how much a publisher still has on offer —
	/// so an incident here means consumers are running out of things to book, whether or not anything
	/// looks broken.
	///
	/// A feed raises an incident when it completed at least three runs inside the window, carried at
	/// least <c>min_future_opportunities</c> at the first of them, and then either:
	///
	/// - fell at every single observation in the window, however gently, or
	/// - lost at least <c>drop_percent</c> of its supply between two consecutive observations.
	///
	/// The two rules are independent and either is enough: the first catches slow erosion that no
	/// percentage threshold would ever see, the second a cliff. <c>detail.reason</c> says which fired,
	/// and is <c>both</c> where the dataset's feeds between them did each.
	///
	/// The dataset is the incident, and <c>detail.feeds</c> names the feeds responsible with their own
	/// figures. Its totals cover only those feeds, not the dataset's healthy ones, so the loss reported
	/// and the percentage beside it always describe the same thing.
	///
	/// Comparisons are between consecutive completed runs rather than consecutive calendar days. A day a
	/// feed failed or was not polled is simply absent, so an outage neither reads as a fall to zero here
	/// nor breaks a run of falls either side of it.
	///
	/// <c>Slot</c> feeds are excluded entirely. A slot count measures how far ahead a publisher has opened
	/// bookings rather than how much it has to offer, so it falls whenever that booking window shortens
	/// and would report a decline that means nothing.
	///
	/// <c>updated</c> and <c>actual_deletes</c> are reported per feed as context and never raise anything:
	/// a drop matched by deletes is a publisher withdrawing opportunities, one without them is supply
	/// quietly expiring, and both are worth seeing.
	///
	/// <c>status</c> is always <c>open</c> and <c>last_contacted</c> is always <c>null</c>: outreach
	/// state needs an incident-tracking store, which does not exist yet.
	///
	/// Results are cached until the next daily refresh, varying by all query parameters.
	/// </remarks>
	/// <param name="page">One-based page number. Default <c>1</c>.</param>
	/// <param name="page_size">Rows per page. Default <c>500</c>, capped at <c>1000</c>.</param>
	/// <param name="window_days">Trailing days the decline is measured over. Default <c>5</c>.</param>
	/// <param name="drop_percent">Percentage lost between two consecutive runs that raises an incident on its own. Default <c>10</c>.</param>
	/// <param name="qualify_window_days">Longer window the decline must also show up over. Default <c>10</c>; never treated as shorter than <c>window_days</c>.</param>
	/// <param name="qualify_drop_percent">Percentage that must have been lost across <c>qualify_window_days</c>, unless the feed's publishing delta is negative. Default <c>10</c>.</param>
	/// <param name="past_threshold_drop_percent">Net percentage lost across the window that sets <c>past_threshold</c>. Default <c>25</c>; never treated as looser than <c>drop_percent</c>.</param>
	/// <param name="min_future_opportunities">Forward supply a feed must have had at the start of the window to be worth reporting. Default <c>50</c>.</param>
	/// <param name="as_of">Evaluate as at this date instead of the latest day in the ingestion table. ISO <c>yyyy-MM-dd</c>.</param>
	[HttpGet("dataset-future-decline-incidents")]
	[ProducesResponseType(typeof(AdminPage<DatasetFutureDeclineIncident>), StatusCodes.Status200OK)]
	public async Task<ActionResult<AdminPage<DatasetFutureDeclineIncident>>> DatasetFutureDeclineIncidents(
		int page = 1,
		int page_size = DefaultPageSize,
		int window_days = 5,
		int drop_percent = 10,
		int qualify_window_days = 10,
		int qualify_drop_percent = 10,
		int past_threshold_drop_percent = 25,
		int min_future_opportunities = 50,
		[FromQuery] DateOnly? as_of = null)
	{
		var thresholds = BuildThresholds(
			window_days, drop_percent, qualify_window_days, qualify_drop_percent,
			past_threshold_drop_percent, min_future_opportunities, trendDays: null);

		var snapshotDate = await ResolveSnapshotDate(as_of);
		if (snapshotDate is null)
		{
			return Ok(Paginate(
				Array.Empty<DatasetFutureDeclineIncident>(),
				page,
				page_size,
				as_of ?? DateOnly.FromDateTime(DateTime.UtcNow)));
		}

		var histories = await LoadFutureSupply(snapshotDate.Value, thresholds.IncidentHistoryDays, IgnoredKinds);
		var declines = DatasetFutureDeclineDetector.Detect(histories, snapshotDate.Value, thresholds);

		var datasets = await LoadDatasetMetadata(declines.Select(d => d.DatasetId).ToList());
		var feeds = await LoadFeedMetadata(declines.SelectMany(d => d.Feeds).Select(f => f.FeedId).ToList());

		var incidents = declines
			.Select(decline => ToIncident(
				decline, datasets.GetValueOrDefault(decline.DatasetId), feeds, snapshotDate.Value, thresholds))
			.ToList();

		return Ok(Paginate(incidents, page, page_size, snapshotDate.Value));
	}

	/// <summary>
	/// Dataset Future Decline Trend
	/// </summary>
	/// <remarks>
	/// Counts of datasets losing forward supply on each of the last <c>trend_days</c> days, oldest first.
	/// Each day is evaluated independently against the same rules as
	/// <see cref="DatasetFutureDeclineIncidents(int, int, int, int, int, int, int, int, DateOnly?)"/>, so a point
	/// shows what that endpoint would have reported on that day. <c>past_threshold_count</c> is always a
	/// subset of <c>open_count</c>. Counts are of <em>datasets</em>, so a publisher with six draining
	/// feeds is one, not six. <c>Slot</c> feeds are excluded here too, so the two endpoints agree.
	///
	/// Results are cached until the next daily refresh, varying by all query parameters.
	/// </remarks>
	/// <param name="page">One-based page number. Default <c>1</c>.</param>
	/// <param name="page_size">Rows per page. Default <c>500</c>, capped at <c>1000</c>.</param>
	/// <param name="trend_days">Days of history to return. Default <c>30</c>.</param>
	/// <param name="window_days">Trailing days the decline is measured over at each point. Default <c>5</c>.</param>
	/// <param name="drop_percent">Percentage lost between two consecutive runs that raises an incident on its own. Default <c>10</c>.</param>
	/// <param name="qualify_window_days">Longer window the decline must also show up over. Default <c>10</c>; never treated as shorter than <c>window_days</c>.</param>
	/// <param name="qualify_drop_percent">Percentage that must have been lost across <c>qualify_window_days</c>, unless the feed's publishing delta is negative. Default <c>10</c>.</param>
	/// <param name="past_threshold_drop_percent">Net percentage lost across the window counted into <c>past_threshold_count</c>. Default <c>25</c>.</param>
	/// <param name="min_future_opportunities">Forward supply a feed must have had at the start of the window to be worth reporting. Default <c>50</c>.</param>
	/// <param name="as_of">Evaluate as at this date instead of the latest day in the ingestion table. ISO <c>yyyy-MM-dd</c>.</param>
	[HttpGet("dataset-future-decline-trend")]
	[ProducesResponseType(typeof(AdminPage<DatasetFutureDeclineTrendPoint>), StatusCodes.Status200OK)]
	public async Task<ActionResult<AdminPage<DatasetFutureDeclineTrendPoint>>> DatasetFutureDeclineTrend(
		int page = 1,
		int page_size = DefaultPageSize,
		int trend_days = 30,
		int window_days = 5,
		int drop_percent = 10,
		int qualify_window_days = 10,
		int qualify_drop_percent = 10,
		int past_threshold_drop_percent = 25,
		int min_future_opportunities = 50,
		[FromQuery] DateOnly? as_of = null)
	{
		var thresholds = BuildThresholds(
			window_days, drop_percent, qualify_window_days, qualify_drop_percent,
			past_threshold_drop_percent, min_future_opportunities, trend_days);

		var snapshotDate = await ResolveSnapshotDate(as_of);
		if (snapshotDate is null)
		{
			return Ok(Paginate(
				Array.Empty<DatasetFutureDeclineTrendPoint>(),
				page,
				page_size,
				as_of ?? DateOnly.FromDateTime(DateTime.UtcNow)));
		}

		var histories = await LoadFutureSupply(snapshotDate.Value, thresholds.RequiredHistoryDays, IgnoredKinds);
		var trend = DatasetFutureDeclineDetector.Trend(histories, snapshotDate.Value, thresholds);

		var points = trend
			.Select(p => new DatasetFutureDeclineTrendPoint
			{
				Date = p.Date,
				OpenCount = p.OpenCount,
				PastThresholdCount = p.PastThresholdCount,
			})
			.ToList();

		return Ok(Paginate(points, page, page_size, snapshotDate.Value));
	}

	#region Utilities

	private static DatasetFutureDeclineThresholds BuildThresholds(
		int windowDays,
		int dropPercent,
		int qualifyWindowDays,
		int qualifyDropPercent,
		int pastThresholdDropPercent,
		int minFutureOpportunities,
		int? trendDays)
	{
		var thresholds = new DatasetFutureDeclineThresholds
		{
			WindowDays = Math.Clamp(windowDays, 1, 90),
			DropPercent = Math.Clamp(dropPercent, 1, 100),
			QualifyWindowDays = Math.Clamp(qualifyWindowDays, 1, 365),
			QualifyDropPercent = Math.Clamp(qualifyDropPercent, 1, 100),
			PastThresholdDropPercent = Math.Clamp(pastThresholdDropPercent, 1, 100),
			MinFutureOpportunities = Math.Clamp(minFutureOpportunities, 0, 1_000_000_000),
		};

		return trendDays is null
			? thresholds
			: thresholds with { TrendDays = Math.Clamp(trendDays.Value, 1, 365) };
	}

	/// <summary>
	/// Hydrates a detected decline into the dashboard payload. <paramref name="metadata"/> is null when
	/// the dataset appears in the ingestion table but has no <c>feeds</c> row; the incident is still
	/// reported, with the descriptive fields left empty, as is each feed missing from
	/// <paramref name="feeds"/>.
	/// </summary>
	private static DatasetFutureDeclineIncident ToIncident(
		DatasetFutureDecline decline,
		DatasetMetadata? metadata,
		IReadOnlyDictionary<string, FeedMetadata> feeds,
		DateOnly snapshotDate,
		DatasetFutureDeclineThresholds thresholds)
	{
		var dataset = metadata ?? new DatasetMetadata(decline.DatasetId, null, null);

		return new DatasetFutureDeclineIncident
		{
			MonitorId = DatasetFutureDeclineDetector.MonitorId,
			PublisherId = dataset.PublisherId,
			PublisherName = dataset.PublisherName ?? "",
			DatasetUrl = decline.DatasetId,
			DatasetName = dataset.Name,
			FeedCount = decline.Feeds.Count,
			FirstDetected = decline.DeclineStart,
			// As on the stall monitors, an incident opens the day the signal turns and stays open while it
			// holds, so days open and consecutive declining days always coincide. They would diverge once
			// incidents are tracked and resolved independently of the raw signal.
			DaysOpen = decline.ConsecutiveDays,
			ConsecutiveDays = decline.ConsecutiveDays,
			PastThreshold = decline.PastThreshold,
			Status = "open",
			LastContacted = null,
			Trend = decline.Trend,
			Detail = new DatasetFutureDeclineIncidentDetail
			{
				Reason = decline.Reason,
				WindowDays = thresholds.WindowDays,
				StartTotal = decline.StartTotal,
				CurrentTotal = decline.CurrentTotal,
				Drop = decline.Drop,
				DropPercent = decline.DropPercent,
				QualifyWindowDays = thresholds.EffectiveQualifyWindowDays,
				QualifyStartTotal = decline.QualifyStartTotal,
				QualifyDropPercent = decline.QualifyDropPercent,
				Feeds = decline.Feeds
					.Select(feed => new DatasetFutureDeclineFeed
					{
						FeedId = feed.FeedId,
						FeedName = (feeds.GetValueOrDefault(feed.FeedId)
							?? new FeedMetadata(feed.FeedId, null, null, null, null)).FeedName,
						Reason = feed.Reason,
						StartFuture = feed.StartFuture,
						CurrentFuture = feed.CurrentFuture,
						Drop = feed.Drop,
						DropPercent = feed.DropPercent,
						QualifyStartFuture = feed.QualifyStartFuture,
						QualifyDropPercent = feed.QualifyDropPercent,
						ConsecutiveDecliningDays = snapshotDate.DayNumber - feed.DeclineStart.DayNumber,
						LargestDailyDropPercent = feed.LargestDailyDropPercent,
						UpdatedInWindow = feed.UpdatedInWindow,
						DeletesInWindow = feed.DeletesInWindow,
						DeltaInWindow = feed.DeltaInWindow,
					})
					.ToList(),
			},
		};
	}

	#endregion
}
