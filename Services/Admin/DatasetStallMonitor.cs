// Everything the dataset_stall monitor is made of, in one file: its thresholds, the shape it reports,
// and the pure rules themselves — the same one-file-per-monitor convention as
// FeedIngestionErrorMonitor.cs and OrphanedChildrenMonitor.cs, and the same split between *deciding*
// and *fetching* enforced by type rather than by file: DatasetStallDetector references no BigQuery and
// no ASP.NET, so it is unit tested without credentials.
//
// There is no query here. This monitor detects on exactly the per-feed publishing history the
// single-feed stall monitor already loads (IngestionHistoryQuery.HistorySql), folded up to the
// dataset. Loading it twice in two shapes would risk the two monitors disagreeing about the same day.
namespace MonitorApi.Services.Admin;

/// <summary>
/// Tunable thresholds for the <c>dataset_stall</c> monitor. All values are in days.
/// </summary>
/// <remarks>
/// The defaults deliberately match <see cref="SingleFeedStallThresholds"/>. The two monitors partition
/// the same signal — a silent feed is reported by one or the other, never both — and that partition is
/// exact only while they agree on what "silent" means.
/// </remarks>
public sealed record DatasetStallThresholds
{
	/// <summary>
	/// How far back the dataset must have published at least once to be considered live enough to raise
	/// a stall for. A dataset silent for longer than this is treated as retired, not stalled.
	/// </summary>
	public int LookbackDays { get; init; } = 120;

	/// <summary>
	/// Consecutive days for which <em>every</em> feed in the dataset must have been silent before the
	/// dataset counts as stalled (an open incident).
	/// </summary>
	public int StallDays { get; init; } = 5;

	/// <summary>Consecutive silent days after which an open incident is flagged as past threshold.</summary>
	public int PastThresholdDays { get; init; } = 7;

	/// <summary>Days of history returned by the trend endpoint.</summary>
	public int TrendDays { get; init; } = 30;

	/// <summary>
	/// Length of the per-incident <c>trend</c> array — how many trailing days of dataset-wide
	/// <c>updated</c> totals each incident reports.
	/// </summary>
	public int IncidentTrendDays { get; init; } = 10;

	/// <summary>
	/// Days of ingestion history the queries must load to answer a request with these thresholds:
	/// the trend endpoint evaluates the lookback window at each of the last <see cref="TrendDays"/> days.
	/// </summary>
	public int RequiredHistoryDays => LookbackDays + Math.Max(TrendDays, IncidentTrendDays);

	/// <summary>
	/// Past threshold can never be looser than the stall threshold, otherwise
	/// <c>past_threshold_count</c> could exceed <c>open_count</c>.
	/// </summary>
	public int EffectivePastThresholdDays => Math.Max(PastThresholdDays, StallDays);
}

/// <summary>One feed's contribution to a dataset-wide stall.</summary>
/// <param name="FeedId">The feed identifier, matching <c>feeds.id</c>.</param>
/// <param name="LastPublished">
/// The feed's last publishing day at or before the evaluated day, or <c>null</c> when it never
/// published inside the loaded window.
/// </param>
/// <param name="ConsecutiveDays">
/// Days the feed itself has been silent, <c>null</c> alongside a <c>null</c>
/// <paramref name="LastPublished"/>. Always at least the dataset's own figure, since the dataset went
/// quiet when its last remaining feed did.
/// </param>
public sealed record DatasetStallFeedSilence(string FeedId, DateOnly? LastPublished, int? ConsecutiveDays);

/// <summary>
/// A detected dataset-wide stall, before dataset metadata is attached.
/// </summary>
public sealed record DatasetStall
{
	/// <summary>The dataset, matching <c>feeds.dataset_url</c>.</summary>
	public required string DatasetId { get; init; }

	/// <summary>Last day on which <em>any</em> feed in the dataset published — the day it went quiet.</summary>
	public required DateOnly LastPublished { get; init; }

	/// <summary>Consecutive days with no feed in the dataset publishing, as of the evaluated day.</summary>
	public required int ConsecutiveDays { get; init; }

	/// <summary>Whether <see cref="ConsecutiveDays"/> has reached the past-threshold limit.</summary>
	public required bool PastThreshold { get; init; }

	/// <summary>Feeds seen for the dataset in the loaded window — all of them silent.</summary>
	public required IReadOnlyList<DatasetStallFeedSilence> Feeds { get; init; }

	/// <summary>
	/// Dataset-wide daily <c>updated</c> totals over the trailing trend window, oldest first. A
	/// <c>null</c> entry means no feed of the dataset had an ingestion run that day; a zero means at
	/// least one was polled and the dataset published nothing.
	/// </summary>
	public required IReadOnlyList<long?> Trend { get; init; }
}

/// <summary>
/// One day of the dataset stall trend. Named unlike its siblings — <c>SingleFeedStallTrendPoint</c>,
/// <c>FeedIngestionErrorTrendPoint</c> — because the wire model this maps to is the one that wants the
/// obvious name in the OpenAPI document: <see cref="Models.Admin.DatasetStallTrendPoint"/>.
/// </summary>
public sealed record DatasetStallDayCounts(DateOnly Date, int OpenCount, int PastThresholdCount);

/// <summary>
/// Pure detection logic for the <c>dataset_stall</c> monitor: a dataset in which <em>every</em> feed
/// has stopped publishing new or updated items, so everything downstream of it is frozen.
/// </summary>
/// <remarks>
/// Deliberately free of BigQuery and ASP.NET types so it can be unit tested against hand-written
/// histories — see <c>MonitorApi.Admin.Tests/Stalls/DatasetStallDetectorTests.cs</c>.
///
/// The exact counterpart of <see cref="SingleFeedStallDetector"/>, which excludes these datasets so
/// that a whole publisher going dark is reported once rather than as a handful of unrelated feed
/// stalls. At equal thresholds the two monitors partition the silence between them: a silent feed is
/// reported by one of them or by the other, never by both.
///
/// A dataset's publishing day is any day one of its feeds published, so the dataset goes quiet only
/// when its last remaining feed does, and it is silent for as long as its most recently active feed has
/// been. Days on which no ingestion run happened are absent from the history and therefore extend a
/// silence rather than break it.
///
/// Two datasets are deliberately <em>not</em> incidents: one that never published inside the loaded
/// window — there is nothing to say it ever worked — and one silent for longer than
/// <see cref="DatasetStallThresholds.LookbackDays"/>, which is retired rather than stalled.
/// </remarks>
public static class DatasetStallDetector
{
	/// <summary>Monitor identifier echoed on every incident.</summary>
	public const string MonitorId = "dataset_stall";

	/// <summary>
	/// Detects the dataset-wide stalls that are open on <paramref name="asOf"/>, ordered
	/// longest-running first.
	/// </summary>
	public static IReadOnlyList<DatasetStall> Detect(
		IEnumerable<FeedIngestionHistory> feeds,
		DateOnly asOf,
		DatasetStallThresholds thresholds)
	{
		var incidents = new List<DatasetStall>();

		foreach (var dataset in GroupByDataset(feeds))
		{
			var lastPublished = LastPublished(dataset, asOf);
			if (!IsStalled(lastPublished, asOf, thresholds))
			{
				continue;
			}

			incidents.Add(new DatasetStall
			{
				DatasetId = dataset.Key,
				LastPublished = lastPublished!.Value,
				ConsecutiveDays = asOf.DayNumber - lastPublished.Value.DayNumber,
				PastThreshold = asOf.DayNumber - lastPublished.Value.DayNumber >= thresholds.EffectivePastThresholdDays,
				Feeds = SilencesFor(dataset.Value, asOf),
				Trend = TrendFor(dataset.Value, asOf, thresholds),
			});
		}

		return incidents
			.OrderByDescending(i => i.ConsecutiveDays)
			.ThenBy(i => i.DatasetId, StringComparer.Ordinal)
			.ToList();
	}

	/// <summary>
	/// Counts open and past-threshold dataset stalls on each of the
	/// <see cref="DatasetStallThresholds.TrendDays"/> days ending at <paramref name="asOf"/>, oldest
	/// first. Each day is evaluated independently, so the series reflects what the incidents endpoint
	/// would have reported on that day.
	/// </summary>
	public static IReadOnlyList<DatasetStallDayCounts> Trend(
		IEnumerable<FeedIngestionHistory> feeds,
		DateOnly asOf,
		DatasetStallThresholds thresholds)
	{
		// Grouped once and re-evaluated per day, rather than regrouping inside the loop: the grouping is
		// the same every day, only the day the histories are read at changes.
		var datasets = GroupByDataset(feeds);
		var points = new List<DatasetStallDayCounts>(thresholds.TrendDays);

		for (var offset = thresholds.TrendDays - 1; offset >= 0; offset--)
		{
			var day = asOf.AddDays(-offset);

			var open = 0;
			var pastThreshold = 0;

			foreach (var dataset in datasets)
			{
				var lastPublished = LastPublished(dataset, day);
				if (!IsStalled(lastPublished, day, thresholds))
				{
					continue;
				}

				open++;
				if (day.DayNumber - lastPublished!.Value.DayNumber >= thresholds.EffectivePastThresholdDays)
				{
					pastThreshold++;
				}
			}

			points.Add(new DatasetStallDayCounts(day, open, pastThreshold));
		}

		return points;
	}

	/// <summary>
	/// The feeds of each dataset, in a stable order. Keyed by <c>dataset_id</c>, which the ingestion
	/// query has already resolved to the dataset of the feed's most recent run, so a feed whose
	/// publisher moved hostname is grouped under the name the dataset has now.
	/// </summary>
	private static List<KeyValuePair<string, List<FeedIngestionHistory>>> GroupByDataset(
		IEnumerable<FeedIngestionHistory> feeds)
	{
		var byDataset = new Dictionary<string, List<FeedIngestionHistory>>(StringComparer.Ordinal);

		foreach (var feed in feeds)
		{
			if (!byDataset.TryGetValue(feed.DatasetId, out var members))
			{
				byDataset[feed.DatasetId] = members = [];
			}

			members.Add(feed);
		}

		return [.. byDataset];
	}

	/// <summary>
	/// The dataset's last publishing day at or before <paramref name="asOf"/> — the latest across its
	/// feeds — or <c>null</c> when no feed of it ever published inside the loaded window.
	/// </summary>
	private static DateOnly? LastPublished(KeyValuePair<string, List<FeedIngestionHistory>> dataset, DateOnly asOf)
	{
		DateOnly? latest = null;

		foreach (var feed in dataset.Value)
		{
			var lastPublished = feed.LastPublishedOnOrBefore(asOf);
			if (lastPublished is not null && (latest is null || lastPublished > latest))
			{
				latest = lastPublished;
			}
		}

		return latest;
	}

	/// <summary>
	/// Whether a dataset that last published on <paramref name="lastPublished"/> is a stall on
	/// <paramref name="asOf"/>: silent for long enough to open an incident, but not so long that it is
	/// retired, and having published at all.
	/// </summary>
	private static bool IsStalled(DateOnly? lastPublished, DateOnly asOf, DatasetStallThresholds thresholds)
	{
		if (lastPublished is null)
		{
			return false;
		}

		var silentDays = asOf.DayNumber - lastPublished.Value.DayNumber;

		return silentDays >= thresholds.StallDays && silentDays <= thresholds.LookbackDays;
	}

	/// <summary>
	/// Per-feed silence for the dataset, most recently active feed first — the first entry is the feed
	/// that determines the dataset's own <c>last_published</c>. Feeds that never published inside the
	/// window sort last.
	/// </summary>
	private static IReadOnlyList<DatasetStallFeedSilence> SilencesFor(List<FeedIngestionHistory> feeds, DateOnly asOf) =>
		feeds
			.Select(feed =>
			{
				var lastPublished = feed.LastPublishedOnOrBefore(asOf);

				return new DatasetStallFeedSilence(
					feed.FeedId,
					lastPublished,
					lastPublished is null ? null : asOf.DayNumber - lastPublished.Value.DayNumber);
			})
			.OrderBy(feed => feed.ConsecutiveDays ?? int.MaxValue)
			.ThenBy(feed => feed.FeedId, StringComparer.Ordinal)
			.ToList();

	/// <summary>
	/// The dataset's daily <c>updated</c> totals over the trailing trend window, oldest first — every
	/// feed's raw ingestion numbers added up, so the dashboard can see the whole dataset drop off.
	/// </summary>
	/// <remarks>
	/// Always exactly <see cref="DatasetStallThresholds.IncidentTrendDays"/> entries ending at
	/// <paramref name="asOf"/>, so entry <c>i</c> is the same day for every incident in a response and a
	/// chart can align them. Days are not filtered by whether the incident was open: the pre-stall
	/// activity is the point of the column. A <c>null</c> entry means no feed of the dataset recorded an
	/// ingestion run that day; a zero means at least one was polled and nothing was published.
	/// </remarks>
	private static IReadOnlyList<long?> TrendFor(
		List<FeedIngestionHistory> feeds,
		DateOnly asOf,
		DatasetStallThresholds thresholds)
	{
		var trend = new List<long?>(thresholds.IncidentTrendDays);

		for (var offset = thresholds.IncidentTrendDays - 1; offset >= 0; offset--)
		{
			var day = asOf.AddDays(-offset);
			long? total = null;

			foreach (var feed in feeds)
			{
				if (feed.RecentUpdated is not null && feed.RecentUpdated.TryGetValue(day, out var updated))
				{
					total = (total ?? 0) + updated;
				}
			}

			trend.Add(total);
		}

		return trend;
	}
}
