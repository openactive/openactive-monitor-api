namespace MonitorApi.Services.Admin;

/// <summary>
/// Pure detection logic for the <c>single_feed_stall</c> monitor: a feed that was publishing recently
/// but has now been silent for several consecutive days.
/// </summary>
/// <remarks>
/// Deliberately free of BigQuery and ASP.NET types so it can be unit tested against hand-written
/// histories — see <c>MonitorApi.Tests/Admin/SingleFeedStallDetectorTests.cs</c>.
///
/// A day counts as a publishing day when the feed's ingestion rows for that day report at least one
/// updated item. Days with no ingestion run at all are simply absent from the history and therefore
/// extend a silence rather than break it.
///
/// Datasets whose feeds are <em>all</em> silent are excluded: that is a dataset-wide outage reported
/// by its own monitor, not a set of independent single-feed stalls.
/// </remarks>
public static class SingleFeedStallDetector
{
	/// <summary>Monitor identifier echoed on every incident.</summary>
	public const string MonitorId = "single_feed_stall";

	/// <summary>
	/// Detects the stalls that are open on <paramref name="asOf"/>, ordered longest-running first.
	/// </summary>
	public static IReadOnlyList<SingleFeedStall> Detect(
		IEnumerable<FeedIngestionHistory> feeds,
		DateOnly asOf,
		SingleFeedStallThresholds thresholds)
	{
		var histories = feeds as IReadOnlyCollection<FeedIngestionHistory> ?? feeds.ToList();
		var excluded = FullySilentDatasets(histories, asOf, thresholds);

		var incidents = new List<SingleFeedStall>();

		foreach (var feed in histories)
		{
			if (excluded.Contains(feed.DatasetId))
			{
				continue;
			}

			var stall = Evaluate(feed, asOf, thresholds);
			if (stall is null)
			{
				continue;
			}

			incidents.Add(stall with { Trend = TrendFor(feed, asOf, thresholds) });
		}

		return incidents
			.OrderByDescending(i => i.ConsecutiveDays)
			.ThenBy(i => i.FeedId, StringComparer.Ordinal)
			.ToList();
	}

	/// <summary>
	/// Counts open and past-threshold stalls on each of the <see cref="SingleFeedStallThresholds.TrendDays"/>
	/// days ending at <paramref name="asOf"/>, oldest first. Each day is evaluated independently, so the
	/// series reflects what the incidents endpoint would have reported on that day.
	/// </summary>
	public static IReadOnlyList<SingleFeedStallTrendPoint> Trend(
		IEnumerable<FeedIngestionHistory> feeds,
		DateOnly asOf,
		SingleFeedStallThresholds thresholds)
	{
		var histories = feeds as IReadOnlyCollection<FeedIngestionHistory> ?? feeds.ToList();
		var points = new List<SingleFeedStallTrendPoint>(thresholds.TrendDays);

		for (var offset = thresholds.TrendDays - 1; offset >= 0; offset--)
		{
			var day = asOf.AddDays(-offset);
			var excluded = FullySilentDatasets(histories, day, thresholds);

			var open = 0;
			var pastThreshold = 0;

			foreach (var feed in histories)
			{
				if (excluded.Contains(feed.DatasetId))
				{
					continue;
				}

				var stall = Evaluate(feed, day, thresholds);
				if (stall is null)
				{
					continue;
				}

				open++;
				if (stall.PastThreshold)
				{
					pastThreshold++;
				}
			}

			points.Add(new SingleFeedStallTrendPoint(day, open, pastThreshold));
		}

		return points;
	}

	/// <summary>
	/// Returns the stall for a feed on a given day, or <c>null</c> when the feed is publishing normally,
	/// has not been silent long enough, or has been silent so long it falls outside the lookback window.
	/// </summary>
	private static SingleFeedStall? Evaluate(FeedIngestionHistory feed, DateOnly asOf, SingleFeedStallThresholds thresholds)
	{
		var lastPublished = LastPublishOnOrBefore(feed.PublishedDays, asOf);
		if (lastPublished is null)
		{
			// Never published inside the loaded window — nothing to say it ever worked.
			return null;
		}

		var silentDays = asOf.DayNumber - lastPublished.Value.DayNumber;

		if (silentDays < thresholds.StallDays || silentDays > thresholds.LookbackDays)
		{
			return null;
		}

		return new SingleFeedStall
		{
			FeedId = feed.FeedId,
			DatasetId = feed.DatasetId,
			LastPublished = lastPublished.Value,
			ConsecutiveDays = silentDays,
			PastThreshold = silentDays >= thresholds.EffectivePastThresholdDays,
			Trend = [],
		};
	}

	/// <summary>
	/// Datasets in which no feed at all has published within the stall threshold. These are dataset-wide
	/// outages and are excluded from single-feed reporting.
	/// </summary>
	private static HashSet<string> FullySilentDatasets(
		IEnumerable<FeedIngestionHistory> feeds,
		DateOnly asOf,
		SingleFeedStallThresholds thresholds)
	{
		var silentByDataset = new Dictionary<string, bool>();

		foreach (var feed in feeds)
		{
			var lastPublished = LastPublishOnOrBefore(feed.PublishedDays, asOf);
			var silent = lastPublished is null || asOf.DayNumber - lastPublished.Value.DayNumber >= thresholds.StallDays;

			silentByDataset[feed.DatasetId] = silentByDataset.TryGetValue(feed.DatasetId, out var allSilent)
				? allSilent && silent
				: silent;
		}

		return silentByDataset.Where(kv => kv.Value).Select(kv => kv.Key).ToHashSet();
	}

	/// <summary>
	/// The feed's daily <c>updated</c> counts over the trailing trend window, oldest first — the raw
	/// ingestion numbers, so the dashboard can see the drop-off that led to the incident.
	/// </summary>
	/// <remarks>
	/// Always exactly <see cref="SingleFeedStallThresholds.IncidentTrendDays"/> entries ending at
	/// <paramref name="asOf"/>, so entry <c>i</c> is always the same day for every incident in a
	/// response and a chart can align them. Days are not filtered by whether the incident was open:
	/// the pre-stall activity is the point of the column. A <c>null</c> entry means no ingestion run was
	/// recorded that day; a zero means the feed was polled and published nothing.
	/// </remarks>
	private static IReadOnlyList<long?> TrendFor(
		FeedIngestionHistory feed,
		DateOnly asOf,
		SingleFeedStallThresholds thresholds)
	{
		var trend = new List<long?>(thresholds.IncidentTrendDays);

		for (var offset = thresholds.IncidentTrendDays - 1; offset >= 0; offset--)
		{
			var day = asOf.AddDays(-offset);
			trend.Add(feed.RecentUpdated is not null && feed.RecentUpdated.TryGetValue(day, out var updated)
				? updated
				: null);
		}

		return trend;
	}

	/// <summary>
	/// Most recent publishing day at or before <paramref name="asOf"/>, or <c>null</c> if there is none.
	/// <paramref name="days"/> must be sorted ascending.
	/// </summary>
	private static DateOnly? LastPublishOnOrBefore(IReadOnlyList<DateOnly> days, DateOnly asOf)
	{
		var low = 0;
		var high = days.Count - 1;
		DateOnly? found = null;

		while (low <= high)
		{
			var mid = low + ((high - low) / 2);
			if (days[mid] <= asOf)
			{
				found = days[mid];
				low = mid + 1;
			}
			else
			{
				high = mid - 1;
			}
		}

		return found;
	}
}
