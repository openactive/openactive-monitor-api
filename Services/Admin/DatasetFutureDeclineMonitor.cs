using Google.Cloud.BigQuery.V2;
using MonitorApi.Models;

namespace MonitorApi.Services.Admin;

/// <summary>
/// SQL and row parsing for the per-feed <c>total_future_opportunities</c> history that
/// <see cref="DatasetFutureDeclineDetector"/> runs on.
/// </summary>
/// <remarks>
/// The third shape read out of <c>opportunity_ingestion</c>, beside <see cref="IngestionHistoryQuery"/>
/// (what each feed published) and <see cref="IngestionStatusQuery"/> (how each run ended). Table names
/// are passed in already fully qualified by the caller's <c>Fq</c>.
/// </remarks>
internal static class FutureSupplyQuery
{
	/// <summary>
	/// Per-feed daily forward-supply history over a date window, one row per feed carrying an array of
	/// days.
	/// </summary>
	/// <remarks>
	/// Only <c>COMPLETE</c> runs are read. A feed that stopped or failed is already reported by the stall
	/// and ingestion-error monitors, and its rows carry no trustworthy supply figure — including them
	/// would turn one publisher's outage into a second, duplicate incident here.
	///
	/// <c>total_future_opportunities</c> is a level rather than a counter, so multiple runs on the same
	/// day collapse to the day's <em>last</em> completed run rather than to a sum; <c>updated</c> and
	/// <c>actual_deletes</c> are per-run increments and do sum. That also absorbs the duplicated
	/// ingestion day the table currently holds.
	///
	/// Kinds the caller excludes are dropped here rather than after loading, so an excluded feed has no
	/// history at all and cannot be evaluated by accident. A row whose <c>kind</c> is <c>NULL</c> is kept:
	/// an unknown kind is not evidence of an excluded one.
	/// </remarks>
	/// <param name="ingestionTable">Fully qualified <c>opportunity_ingestion</c> table name.</param>
	public static string FutureSupplySql(string ingestionTable) =>
		$"""
		WITH daily AS (
		  SELECT feed_id,
		         DATE(ingestion_date) AS ingestion_day,
		         -- The dataset the feed was ingested under that day, taken from the day's last run.
		         -- ARRAY_AGG rather than ANY_VALUE for the reason IngestionHistoryQuery gives: a feed id
		         -- can appear under two dataset_ids, and ANY_VALUE would pick a different one from one
		         -- query to the next.
		         ARRAY_AGG(dataset_id ORDER BY ingestion_date DESC, dataset_id LIMIT 1)[OFFSET(0)] AS dataset_id,
		         -- A level, not a counter: the day's last completed run is the day's figure.
		         ARRAY_AGG(total_future_opportunities
		                   ORDER BY ingestion_date DESC, total_future_opportunities DESC
		                   LIMIT 1)[OFFSET(0)] AS future_opportunities,
		         SUM(updated) AS updated,
		         SUM(actual_deletes) AS deletes
		  FROM {ingestionTable}
		  WHERE DATE(ingestion_date) BETWEEN @window_start AND @window_end
		        AND status = 'COMPLETE'
		        AND feed_id IS NOT NULL
		        AND dataset_id IS NOT NULL
		        AND total_future_opportunities IS NOT NULL
		        AND (kind IS NULL OR kind NOT IN UNNEST(@ignored_kinds))
		  GROUP BY feed_id, ingestion_day
		)
		SELECT feed_id,
		       -- The dataset of the feed's most recent run, so a feed whose publisher moved hostname is
		       -- reported under the dataset it belongs to now.
		       ARRAY_AGG(dataset_id ORDER BY ingestion_day DESC LIMIT 1)[OFFSET(0)] AS dataset_id,
		       ARRAY_AGG(STRUCT(FORMAT_DATE('%F', ingestion_day) AS day,
		                        future_opportunities,
		                        IFNULL(updated, 0) AS updated,
		                        IFNULL(deletes, 0) AS deletes)
		                 ORDER BY ingestion_day) AS days
		FROM daily
		GROUP BY feed_id
		""";

	/// <summary>Parameters for <see cref="FutureSupplySql"/>.</summary>
	/// <remarks>
	/// Blanks are dropped from <c>ignoredKinds</c> and duplicates collapsed, because a BigQuery array
	/// parameter cannot carry nulls; an empty list excludes nothing.
	/// </remarks>
	public static IReadOnlyList<BigQueryParameter> FutureSupplyParameters(
		DateOnly windowStart,
		DateOnly windowEnd,
		IEnumerable<string> ignoredKinds) =>
	[
		new BigQueryParameter("window_start", BigQueryDbType.Date, windowStart.ToDateTime(TimeOnly.MinValue)),
		new BigQueryParameter("window_end", BigQueryDbType.Date, windowEnd.ToDateTime(TimeOnly.MinValue)),
		new BigQueryParameter("ignored_kinds", BigQueryDbType.Array, ignoredKinds
			.Where(kind => !string.IsNullOrWhiteSpace(kind))
			.Distinct(StringComparer.Ordinal)
			.ToList())
		{
			ArrayElementType = BigQueryDbType.String,
		},
	];

	public static FeedFutureSupplyHistory ParseFutureSupply(Dictionary<string, object> row) =>
		new(
			(string)row["feed_id"],
			(string)row["dataset_id"],
			ParseDays(row.GetValueOrDefault("days")));

	/// <summary>
	/// Parses the repeated <c>STRUCT&lt;day STRING, future_opportunities INT64, updated INT64, deletes
	/// INT64&gt;</c> into ascending day entries. The BigQuery client surfaces a repeated struct as
	/// <c>Dictionary&lt;string, object&gt;[]</c>, and a <c>NULL</c> field is absent from that dictionary
	/// rather than present as null.
	/// </summary>
	private static IReadOnlyList<FeedFutureSupplyDay> ParseDays(object? cell)
	{
		var days = new List<FeedFutureSupplyDay>();

		// A single string must not be treated as a char sequence.
		if (cell is not System.Collections.IEnumerable rows || cell is string)
		{
			return days;
		}

		foreach (var item in rows)
		{
			if (item is not IDictionary<string, object> fields)
			{
				continue;
			}

			var day = ParseDay(Field(fields, "day"));
			var future = BigQueryValueParser.AsLong(Field(fields, "future_opportunities"));

			if (day is null || future is null)
			{
				continue;
			}

			days.Add(new FeedFutureSupplyDay(
				day.Value,
				future.Value,
				BigQueryValueParser.AsLong(Field(fields, "updated")) ?? 0,
				BigQueryValueParser.AsLong(Field(fields, "deletes")) ?? 0));
		}

		days.Sort((a, b) => a.Day.CompareTo(b.Day));
		return days;
	}

	private static object? Field(IDictionary<string, object> fields, string name) =>
		fields.TryGetValue(name, out var value) ? value : null;

	private static DateOnly? ParseDay(object? value) => value switch
	{
		DateTime dateTime => DateOnly.FromDateTime(dateTime),
		DateOnly day => day,
		not null when value.ToString() is { Length: > 0 } text &&
			DateOnly.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
		_ => null,
	};
}

/// <summary>One day of a feed's forward supply, as read from a completed ingestion run.</summary>
/// <param name="Day">The ingestion day.</param>
/// <param name="FutureOpportunities">
/// <c>total_future_opportunities</c> after the day's last completed run — a level, so it is comparable
/// across days rather than added up.
/// </param>
/// <param name="Updated">Items the feed reported as new or changed that day. Context only.</param>
/// <param name="Deletes">Items the feed actually removed that day. Context only.</param>
public sealed record FeedFutureSupplyDay(DateOnly Day, long FutureOpportunities, long Updated, long Deletes);

/// <summary>
/// One feed's forward-supply history inside the analysis window, as read from
/// <c>opportunity_ingestion</c>.
/// </summary>
/// <param name="FeedId">The feed identifier, matching <c>feeds.id</c>.</param>
/// <param name="DatasetId">The dataset the feed belongs to, matching <c>feeds.dataset_url</c>.</param>
/// <param name="Days">
/// Ascending, distinct days on which the feed completed an ingestion run. Days on which it failed, or
/// on which no run happened at all, are absent — the difference matters, because a gap must not read as
/// a drop to zero.
/// </param>
public sealed record FeedFutureSupplyHistory(
	string FeedId,
	string DatasetId,
	IReadOnlyList<FeedFutureSupplyDay> Days)
{
	/// <summary>
	/// The observed days falling inside <paramref name="from"/>..<paramref name="to"/> inclusive,
	/// ascending.
	/// </summary>
	/// <remarks>
	/// Lives on the history rather than in the detector because the trend endpoint asks it once per feed
	/// per day of the series, and because both the window rules and the per-incident trend column must
	/// read the same days the same way.
	/// </remarks>
	public List<FeedFutureSupplyDay> PointsIn(DateOnly from, DateOnly to)
	{
		var points = new List<FeedFutureSupplyDay>();

		foreach (var day in Days)
		{
			if (day.Day >= from && day.Day <= to)
			{
				points.Add(day);
			}
		}

		return points;
	}

	/// <summary>The feed's figure on an exact day, or <c>null</c> when it completed no run then.</summary>
	public FeedFutureSupplyDay? On(DateOnly day)
	{
		foreach (var entry in Days)
		{
			if (entry.Day == day)
			{
				return entry;
			}
		}

		return null;
	}
}

/// <summary>
/// Tunable thresholds for the <c>dataset_future_decline</c> monitor.
/// </summary>
/// <remarks>
/// The two triggers are deliberately independent. <see cref="DropPercent"/> catches a cliff — a feed
/// that loses a quarter of its forward supply overnight — while the monotonic rule catches the erosion
/// that a percentage threshold never sees, a feed shedding two percent a day for a week. Either one
/// raises an incident.
/// </remarks>
public sealed record DatasetFutureDeclineThresholds
{
	/// <summary>
	/// Trailing days the decline is measured over, ending at the evaluated day. Only days on which the
	/// feed completed a run count as observations inside it.
	/// </summary>
	public int WindowDays { get; init; } = 5;

	/// <summary>
	/// Percentage of a feed's forward supply that must vanish between two consecutive observations to
	/// count as a sharp drop, whatever the rest of the window did.
	/// </summary>
	public int DropPercent { get; init; } = 10;

	/// <summary>
	/// Net percentage decline across the window at which an open incident is flagged as past threshold.
	/// </summary>
	public int PastThresholdDropPercent { get; init; } = 25;

	/// <summary>
	/// Forward supply a feed must have had at the start of the window before its decline is worth
	/// reporting, so a feed falling from nine opportunities to six is not an incident.
	/// </summary>
	public int MinFutureOpportunities { get; init; } = 50;

	/// <summary>
	/// Longer window the five-day verdict has to survive: a feed is only reported if, across this span,
	/// it has either lost <see cref="QualifyDropPercent"/> of its supply or removed more than it added.
	/// </summary>
	public int QualifyWindowDays { get; init; } = 10;

	/// <summary>
	/// Percentage a feed must have lost across the qualifying window, unless its publishing delta over the
	/// detection window is negative instead.
	/// </summary>
	public int QualifyDropPercent { get; init; } = 10;

	/// <summary>Days of history returned by the trend endpoint.</summary>
	public int TrendDays { get; init; } = 30;

	/// <summary>
	/// Length of the per-incident <c>trend</c> array — how many trailing days of dataset-wide forward
	/// supply each incident reports.
	/// </summary>
	public int IncidentTrendDays { get; init; } = 10;

	/// <summary>
	/// Observations a feed needs inside the window before either rule is applied. Two points are one
	/// step, which says nothing about a trend; three is the fewest that can establish one. Never below
	/// two, so a one-day window — which can hold a single observation and therefore no step at all —
	/// reports nothing rather than flagging every feed it sees.
	/// </summary>
	public int MinObservations => Math.Max(2, Math.Min(3, WindowDays));

	/// <summary>
	/// Past threshold can never be looser than the trigger, otherwise <c>past_threshold_count</c> could
	/// exceed <c>open_count</c>.
	/// </summary>
	public int EffectivePastThresholdPercent => Math.Max(PastThresholdDropPercent, DropPercent);

	/// <summary>
	/// The qualifying window can never be shorter than the detection window, so the days it judges are
	/// always a superset of the days the decline was found in and there is always a figure to compare.
	/// </summary>
	public int EffectiveQualifyWindowDays => Math.Max(QualifyWindowDays, WindowDays);

	/// <summary>
	/// Days of history the incidents endpoint must load: enough for the qualifying window, and enough to
	/// fill the per-incident trend column.
	/// </summary>
	public int IncidentHistoryDays => Math.Max(EffectiveQualifyWindowDays, IncidentTrendDays);

	/// <summary>
	/// Days of history the trend endpoint must load: the window is re-evaluated at each of the last
	/// <see cref="TrendDays"/> days, so the oldest of them needs a whole window behind it.
	/// </summary>
	public int RequiredHistoryDays => TrendDays - 1 + IncidentHistoryDays;
}

/// <summary>Why a feed or dataset was reported.</summary>
public static class FutureDeclineReasons
{
	/// <summary>Every observation in the window was lower than the one before it.</summary>
	public const string MonotonicDecline = "monotonic_decline";

	/// <summary>One step in the window lost at least the trigger percentage.</summary>
	public const string SharpDrop = "sharp_drop";

	/// <summary>Both rules fired.</summary>
	public const string Both = "both";

	/// <summary>Combines two reasons, collapsing a disagreement to <see cref="Both"/>.</summary>
	public static string Combine(string left, string right) => left == right ? left : Both;
}

/// <summary>One feed's contribution to a dataset's falling forward supply.</summary>
/// <param name="FeedId">The feed identifier, matching <c>feeds.id</c>.</param>
/// <param name="Reason">Which rule fired — see <see cref="FutureDeclineReasons"/>.</param>
/// <param name="DeclineStart">
/// The day the reported decline began: the first day of the unbroken run of falls that ends the window,
/// or, when the window does not end in a fall, the day before the sharp drop that raised it.
/// </param>
/// <param name="StartFuture">Forward supply at the first observation in the window.</param>
/// <param name="CurrentFuture">Forward supply at the last observation in the window.</param>
/// <param name="QualifyStartFuture">
/// Forward supply at the first observation in the longer qualifying window, which the decline is
/// measured back to when deciding whether it is worth reporting at all.
/// </param>
/// <param name="LargestDailyDropPercent">
/// The steepest single step down in the window, as a percentage of the figure it fell from. Zero when
/// nothing fell.
/// </param>
/// <param name="UpdatedInWindow">Items the feed published across the window. Context only.</param>
/// <param name="DeletesInWindow">Items the feed removed across the window. Context only.</param>
public sealed record FeedFutureDecline(
	string FeedId,
	string Reason,
	DateOnly DeclineStart,
	long StartFuture,
	long CurrentFuture,
	long QualifyStartFuture,
	double LargestDailyDropPercent,
	long UpdatedInWindow,
	long DeletesInWindow)
{
	/// <summary>Forward opportunities lost across the window.</summary>
	public long Drop => StartFuture - CurrentFuture;

	/// <summary>Those losses as a percentage of <see cref="StartFuture"/>.</summary>
	public double DropPercent => Percent(Drop, StartFuture);

	/// <summary>The same losses measured back to the start of the qualifying window.</summary>
	public double QualifyDropPercent => Percent(QualifyStartFuture - CurrentFuture, QualifyStartFuture);

	/// <summary>
	/// Items published minus items removed across the detection window. Negative means the feed took away
	/// more than it added, which is the other way a decline qualifies as worth reporting.
	/// </summary>
	public long DeltaInWindow => UpdatedInWindow - DeletesInWindow;

	/// <summary>A drop as a percentage of what it fell from, rounded for the wire and never negative.</summary>
	internal static double Percent(long drop, long from) =>
		from <= 0 ? 0 : Math.Round(Math.Max(0, drop) * 100.0 / from, 2);
}

/// <summary>
/// A dataset whose forward supply is draining, before dataset metadata is attached.
/// </summary>
public sealed record DatasetFutureDecline
{
	/// <summary>The dataset, matching <c>feeds.dataset_url</c>.</summary>
	public required string DatasetId { get; init; }

	/// <summary>Which rules fired across the contributing feeds — see <see cref="FutureDeclineReasons"/>.</summary>
	public required string Reason { get; init; }

	/// <summary>The earliest day any contributing feed's decline began.</summary>
	public required DateOnly DeclineStart { get; init; }

	/// <summary>Days from <see cref="DeclineStart"/> to the evaluated day.</summary>
	public required int ConsecutiveDays { get; init; }

	/// <summary>Whether the net decline has reached the escalation threshold.</summary>
	public required bool PastThreshold { get; init; }

	/// <summary>Forward supply of the contributing feeds at the start of the window.</summary>
	public required long StartTotal { get; init; }

	/// <summary>Forward supply of the same feeds at the end of it.</summary>
	public required long CurrentTotal { get; init; }

	/// <summary>The same feeds' supply at the start of the longer qualifying window.</summary>
	public required long QualifyStartTotal { get; init; }

	/// <summary>The feeds that raised the incident, largest loss first.</summary>
	public required IReadOnlyList<FeedFutureDecline> Feeds { get; init; }

	/// <summary>
	/// Daily forward-supply totals across the contributing feeds over the trailing trend window, oldest
	/// first. A <c>null</c> entry means no contributing feed completed a run that day.
	/// </summary>
	public required IReadOnlyList<long?> Trend { get; init; }

	/// <summary>Forward opportunities the dataset has lost across the window.</summary>
	public long Drop => StartTotal - CurrentTotal;

	/// <summary>Those losses as a percentage of <see cref="StartTotal"/>.</summary>
	public double DropPercent => FeedFutureDecline.Percent(Drop, StartTotal);

	/// <summary>The same losses measured back to the start of the qualifying window.</summary>
	public double QualifyDropPercent =>
		FeedFutureDecline.Percent(QualifyStartTotal - CurrentTotal, QualifyStartTotal);
}

/// <summary>
/// One day of the future-decline trend. Named for the wire model it maps to,
/// <see cref="Models.Admin.DatasetFutureDeclineTrendPoint"/>.
/// </summary>
public sealed record DatasetFutureDeclineDayCounts(DateOnly Date, int OpenCount, int PastThresholdCount);

/// <summary>
/// Pure detection logic for the <c>dataset_future_decline</c> monitor: datasets whose
/// <c>total_future_opportunities</c> is draining away while their feeds keep ingesting successfully.
/// </summary>
/// <remarks>
/// Deliberately free of BigQuery and ASP.NET types so it can be unit tested against hand-written
/// histories — see <c>MonitorApi.Admin.Tests/Supply/DatasetFutureDeclineDetectorTests.cs</c>.
///
/// The gap the other monitors leave. A feed that stops publishing is a stall and a feed whose run fails
/// is an ingestion error; a feed that completes every day while its forward supply erodes is neither,
/// and is invisible until a consumer notices there is nothing left to book. Only <c>COMPLETE</c> runs
/// are read, so this monitor never re-reports a publisher the stall or error monitors already have.
///
/// Feeds are judged one at a time and the findings rolled up, because the question the dashboard asks
/// is which feeds are responsible; the dataset is the unit an operator contacts, so the incident is
/// dataset-scoped and names its feeds in <c>detail.feeds</c>. A dataset's figures cover only the feeds
/// that raised it, so the loss reported and the percentage beside it always describe the same thing.
///
/// Comparisons are between consecutive <em>observations</em>, not consecutive calendar days. A day with
/// no completed run is absent from the history, and treating that absence as a fall to zero would
/// invent an incident out of an outage the stall monitor already owns.
/// </remarks>
public static class DatasetFutureDeclineDetector
{
	/// <summary>Monitor identifier echoed on every incident.</summary>
	public const string MonitorId = "dataset_future_decline";

	/// <summary>
	/// Detects the datasets whose forward supply is falling as of <paramref name="asOf"/>, ordered
	/// largest loss first.
	/// </summary>
	public static IReadOnlyList<DatasetFutureDecline> Detect(
		IEnumerable<FeedFutureSupplyHistory> feeds,
		DateOnly asOf,
		DatasetFutureDeclineThresholds thresholds)
	{
		var incidents = new List<DatasetFutureDecline>();

		foreach (var dataset in GroupByDataset(feeds))
		{
			var declines = new List<FeedFutureDecline>();
			var contributors = new List<FeedFutureSupplyHistory>();

			foreach (var feed in dataset.Value)
			{
				if (Evaluate(feed, asOf, thresholds) is { } decline)
				{
					declines.Add(decline);
					contributors.Add(feed);
				}
			}

			if (declines.Count == 0)
			{
				continue;
			}

			declines = [.. declines
				.OrderByDescending(d => d.Drop)
				.ThenByDescending(d => d.DropPercent)
				.ThenBy(d => d.FeedId, StringComparer.Ordinal)];

			var startTotal = declines.Sum(d => d.StartFuture);
			var currentTotal = declines.Sum(d => d.CurrentFuture);
			var qualifyStartTotal = declines.Sum(d => d.QualifyStartFuture);
			var declineStart = declines.Min(d => d.DeclineStart);

			incidents.Add(new DatasetFutureDecline
			{
				DatasetId = dataset.Key,
				Reason = declines.Select(d => d.Reason).Aggregate(FutureDeclineReasons.Combine),
				DeclineStart = declineStart,
				ConsecutiveDays = asOf.DayNumber - declineStart.DayNumber,
				PastThreshold = FeedFutureDecline.Percent(startTotal - currentTotal, startTotal)
					>= thresholds.EffectivePastThresholdPercent,
				StartTotal = startTotal,
				CurrentTotal = currentTotal,
				QualifyStartTotal = qualifyStartTotal,
				Feeds = declines,
				Trend = TrendFor(contributors, asOf, thresholds),
			});
		}

		return incidents
			.OrderByDescending(i => i.Drop)
			.ThenByDescending(i => i.DropPercent)
			.ThenBy(i => i.DatasetId, StringComparer.Ordinal)
			.ToList();
	}

	/// <summary>
	/// Counts open and past-threshold declines on each of the
	/// <see cref="DatasetFutureDeclineThresholds.TrendDays"/> days ending at <paramref name="asOf"/>,
	/// oldest first. Each day is evaluated independently, so the series reflects what the incidents
	/// endpoint would have reported on that day.
	/// </summary>
	public static IReadOnlyList<DatasetFutureDeclineDayCounts> Trend(
		IEnumerable<FeedFutureSupplyHistory> feeds,
		DateOnly asOf,
		DatasetFutureDeclineThresholds thresholds)
	{
		// Grouped once and re-evaluated per day, rather than regrouping inside the loop: the grouping is
		// the same every day, only the day the histories are read at changes.
		var datasets = GroupByDataset(feeds);
		var points = new List<DatasetFutureDeclineDayCounts>(thresholds.TrendDays);

		for (var offset = thresholds.TrendDays - 1; offset >= 0; offset--)
		{
			var day = asOf.AddDays(-offset);

			var open = 0;
			var pastThreshold = 0;

			foreach (var dataset in datasets)
			{
				long startTotal = 0;
				long currentTotal = 0;
				var flagged = false;

				foreach (var feed in dataset.Value)
				{
					if (Evaluate(feed, day, thresholds) is not { } decline)
					{
						continue;
					}

					flagged = true;
					startTotal += decline.StartFuture;
					currentTotal += decline.CurrentFuture;
				}

				if (!flagged)
				{
					continue;
				}

				open++;
				if (FeedFutureDecline.Percent(startTotal - currentTotal, startTotal)
					>= thresholds.EffectivePastThresholdPercent)
				{
					pastThreshold++;
				}
			}

			points.Add(new DatasetFutureDeclineDayCounts(day, open, pastThreshold));
		}

		return points;
	}

	/// <summary>
	/// Applies both rules to one feed's window, returning <c>null</c> when neither fires or when the
	/// window holds too little to judge.
	/// </summary>
	/// <remarks>
	/// Kept separate from <see cref="Detect"/> so the dataset rollup and the trend series ask exactly the
	/// same question of a feed, and so a feed that raises nothing costs no allocation beyond its points.
	/// </remarks>
	public static FeedFutureDecline? Evaluate(
		FeedFutureSupplyHistory feed,
		DateOnly asOf,
		DatasetFutureDeclineThresholds thresholds)
	{
		var points = feed.PointsIn(asOf.AddDays(-(thresholds.WindowDays - 1)), asOf);

		if (points.Count < thresholds.MinObservations)
		{
			return null;
		}

		var start = points[0].FutureOpportunities;
		if (start < thresholds.MinFutureOpportunities)
		{
			return null;
		}

		var monotonic = true;
		var largestDropPercent = 0.0;
		var largestDropIndex = -1;

		for (var i = 1; i < points.Count; i++)
		{
			var previous = points[i - 1].FutureOpportunities;
			var current = points[i].FutureOpportunities;

			if (current >= previous)
			{
				monotonic = false;
				continue;
			}

			var dropPercent = FeedFutureDecline.Percent(previous - current, previous);
			if (dropPercent > largestDropPercent)
			{
				largestDropPercent = dropPercent;
				largestDropIndex = i - 1;
			}
		}

		var sharp = largestDropPercent >= thresholds.DropPercent;

		if (!monotonic && !sharp)
		{
			return null;
		}

		var reason = monotonic && sharp
			? FutureDeclineReasons.Both
			: monotonic
				? FutureDeclineReasons.MonotonicDecline
				: FutureDeclineReasons.SharpDrop;

		// The qualifying gate. Detection over a short window is sensitive by design and finds plenty of
		// wobble; a decline only earns an operator's attention if it also shows up over the longer window
		// as a real loss of supply, or if the feed was removing more than it added while it happened.
		// The two conditions are independent: a steep fall qualifies on its own however the feed published,
		// and a feed shedding stock qualifies however shallow the curve looks over ten days.
		var latest = points[^1].FutureOpportunities;
		var updated = points.Sum(p => p.Updated);
		var deletes = points.Sum(p => p.Deletes);

		// A superset of the detection window, both ending at asOf, so this is never empty and its first
		// point is never later than the detection window's.
		var qualifyPoints = feed.PointsIn(asOf.AddDays(-(thresholds.EffectiveQualifyWindowDays - 1)), asOf);
		var qualifyStart = qualifyPoints[0].FutureOpportunities;

		var qualifiesOnDrop =
			FeedFutureDecline.Percent(qualifyStart - latest, qualifyStart) >= thresholds.QualifyDropPercent;
		var qualifiesOnDelta = updated - deletes < 0;

		if (!qualifiesOnDrop && !qualifiesOnDelta)
		{
			return null;
		}

		return new FeedFutureDecline(
			feed.FeedId,
			reason,
			DeclineStart(points, largestDropIndex),
			start,
			latest,
			qualifyStart,
			largestDropPercent,
			updated,
			deletes);
	}

	/// <summary>
	/// The day the reported decline began: the start of the unbroken run of falls that ends the window
	/// or, when the window does not end in a fall, the day the steepest drop fell from.
	/// </summary>
	/// <remarks>
	/// Dates the incident, so it has to be a day the decline can be traced to rather than simply the
	/// window's first day — otherwise every incident would claim to have started <c>window_days</c> ago
	/// whatever the data did.
	/// </remarks>
	private static DateOnly DeclineStart(List<FeedFutureSupplyDay> points, int largestDropIndex)
	{
		var index = points.Count - 1;

		while (index > 0 && points[index].FutureOpportunities < points[index - 1].FutureOpportunities)
		{
			index--;
		}

		// The window ends flat or rising, so the run above is empty and the sharp drop is what raised
		// this feed; date it from the figure that drop fell from.
		if (index == points.Count - 1 && largestDropIndex >= 0)
		{
			index = largestDropIndex;
		}

		return points[index].Day;
	}

	/// <summary>
	/// The feeds of each dataset, in a stable order. Keyed by <c>dataset_id</c>, which the query has
	/// already resolved to the dataset of the feed's most recent run.
	/// </summary>
	private static List<KeyValuePair<string, List<FeedFutureSupplyHistory>>> GroupByDataset(
		IEnumerable<FeedFutureSupplyHistory> feeds)
	{
		var byDataset = new Dictionary<string, List<FeedFutureSupplyHistory>>(StringComparer.Ordinal);

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
	/// The contributing feeds' daily forward-supply totals over the trailing trend window, oldest first —
	/// the falling line the incident is about.
	/// </summary>
	/// <remarks>
	/// Always exactly <see cref="DatasetFutureDeclineThresholds.IncidentTrendDays"/> entries ending at
	/// <paramref name="asOf"/>, so entry <c>i</c> is the same day for every incident in a response and a
	/// chart can align them. Days are not filtered by whether the incident was open: the supply the
	/// dataset used to carry is the point of the column. A <c>null</c> entry means no contributing feed
	/// completed a run that day, which is not the same as a genuine zero.
	/// </remarks>
	private static IReadOnlyList<long?> TrendFor(
		List<FeedFutureSupplyHistory> feeds,
		DateOnly asOf,
		DatasetFutureDeclineThresholds thresholds)
	{
		var trend = new List<long?>(thresholds.IncidentTrendDays);

		for (var offset = thresholds.IncidentTrendDays - 1; offset >= 0; offset--)
		{
			var day = asOf.AddDays(-offset);
			long? total = null;

			foreach (var feed in feeds)
			{
				if (feed.On(day) is { } entry)
				{
					total = (total ?? 0) + entry.FutureOpportunities;
				}
			}

			trend.Add(total);
		}

		return trend;
	}
}
