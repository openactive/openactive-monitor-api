using Google.Cloud.BigQuery.V2;

// Everything the feed_ingestion_error monitor is made of, in one file: its thresholds, the ingestion
// status history it detects on, the SQL that loads that history, and the pure rules themselves.
//
// One file per monitor is the convention here. A file each for the thresholds, the input record, the
// output record, the rules and the query would bury the thing that actually matters — the rules — in a
// folder of near-empty files, and there are many more monitors to come. The split that does earn its
// keep is between *deciding* and *fetching*, and it is enforced by type rather than by file:
// FeedIngestionErrorDetector references no BigQuery and no ASP.NET types, so it is unit tested without
// credentials.
namespace MonitorApi.Services.Admin;

/// <summary>
/// Tunable thresholds for the <c>feed_ingestion_error</c> monitor. All values are in days.
/// </summary>
public sealed record FeedIngestionErrorThresholds
{
	/// <summary>
	/// How recently the feed must have completed an ingestion for its current failure to count as a
	/// regression worth reporting. A feed that has not completed once within this many days before the
	/// evaluated day is treated as permanently broken rather than newly failing, and is left out.
	/// </summary>
	public int SuccessLookbackDays { get; init; } = 15;

	/// <summary>
	/// Consecutive days of failure that open an incident. At the default of one, a single failing day
	/// is enough.
	/// </summary>
	public int ErrorDays { get; init; } = 1;

	/// <summary>Days of failure after which an open incident is flagged as past threshold.</summary>
	public int PastThresholdDays { get; init; } = 3;

	/// <summary>Days of history returned by the trend endpoint.</summary>
	public int TrendDays { get; init; } = 30;

	/// <summary>
	/// Length of the per-incident <c>trend</c> array — how many trailing days of failure flags each
	/// incident reports.
	/// </summary>
	public int IncidentTrendDays { get; init; } = 10;

	/// <summary>
	/// Days of history needed to evaluate a single day: enough to find the last completed ingestion,
	/// and enough to fill the per-incident trend.
	/// </summary>
	public int IncidentHistoryDays => Math.Max(SuccessLookbackDays, IncidentTrendDays);

	/// <summary>
	/// Days of history the queries must load to answer a trend request with these thresholds: the trend
	/// endpoint evaluates <see cref="IncidentHistoryDays"/> at each of the last <see cref="TrendDays"/>
	/// days.
	/// </summary>
	public int RequiredHistoryDays => TrendDays - 1 + IncidentHistoryDays;

	/// <summary>
	/// Past threshold can never be looser than the failure threshold, otherwise
	/// <c>past_threshold_count</c> could exceed <c>open_count</c>.
	/// </summary>
	public int EffectivePastThresholdDays => Math.Max(PastThresholdDays, ErrorDays);
}

/// <summary>
/// One feed's ingestion outcome on one day, collapsed from that day's runs.
/// </summary>
/// <param name="Day">The ingestion day.</param>
/// <param name="Status">
/// The day's outcome, lower-cased: <see cref="Complete"/>, <see cref="Error"/>, or whatever else the
/// pipeline recorded (<c>warning</c> today). Where a feed was polled more than once in a day, success
/// wins — a day with both a completed and a failed run reads as <see cref="Complete"/>, because the
/// feed did deliver.
/// </param>
/// <param name="ErrorCode">
/// The failing run's <c>error_code</c> — an HTTP status (<c>500</c>, <c>404</c>, <c>403</c>) or a
/// pipeline code (<c>BATCH_FAILED</c>, <c>CONNECTION_ERROR</c>, <c>MISSING_ITEMS</c>). <c>null</c> on
/// days that did not fail, and on failures recorded before the column existed.
/// </param>
/// <param name="ErrorMessage">The failing run's message, from <c>warning_message</c>. Same nullability as <paramref name="ErrorCode"/>.</param>
public sealed record FeedIngestionDay(DateOnly Day, string Status, string? ErrorCode = null, string? ErrorMessage = null)
{
	/// <summary>Status of a day on which the feed's ingestion completed.</summary>
	public const string Complete = "complete";

	/// <summary>Status of a day on which the feed's ingestion failed and none of its runs completed.</summary>
	public const string Error = "error";

	public bool Completed => string.Equals(Status, Complete, StringComparison.OrdinalIgnoreCase);

	public bool Errored => string.Equals(Status, Error, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One feed's ingestion status history inside the analysis window, as read from
/// <c>opportunity_ingestion</c>.
/// </summary>
/// <param name="FeedId">The feed identifier, matching <c>feeds.id</c>.</param>
/// <param name="DatasetId">The dataset the feed belongs to, matching <c>feeds.dataset_url</c>.</param>
/// <param name="Days">
/// Ascending, one entry per day on which an ingestion run happened. Days with no run at all are absent
/// — nothing is known about them, which is not the same as a day that failed.
/// </param>
/// <remarks>
/// Separate from <see cref="FeedIngestionHistory"/>, which carries the <c>updated</c> counts the stall
/// monitors detect on: the error monitors need the per-day status and failure detail instead, and
/// loading either shape for the other would mean carrying columns nothing reads.
/// </remarks>
public sealed record FeedStatusHistory(
	string FeedId,
	string DatasetId,
	IReadOnlyList<FeedIngestionDay> Days);

/// <summary>
/// A detected feed ingestion error, before feed metadata is attached.
/// </summary>
public sealed record FeedIngestionError
{
	public required string FeedId { get; init; }

	public required string DatasetId { get; init; }

	/// <summary>Last day the feed completed an ingestion — the day before it started failing.</summary>
	public required DateOnly LastCompleted { get; init; }

	/// <summary>Days since <see cref="LastCompleted"/>, as of the evaluated day.</summary>
	public required int ConsecutiveDays { get; init; }

	/// <summary>Whether <see cref="ConsecutiveDays"/> has reached the past-threshold limit.</summary>
	public required bool PastThreshold { get; init; }

	/// <summary>
	/// The evaluated day's <c>error_code</c>, or <c>null</c> for a failure recorded before that column
	/// existed.
	/// </summary>
	public required string? ErrorCode { get; init; }

	/// <summary>The evaluated day's failure message, with the same nullability as <see cref="ErrorCode"/>.</summary>
	public required string? ErrorMessage { get; init; }

	/// <summary>
	/// One flag per day over the trailing trend window, oldest first: <c>1</c> on a day the feed's
	/// ingestion failed, <c>0</c> on any other day — whether it completed, warned, or was never polled.
	/// </summary>
	public required IReadOnlyList<int> Trend { get; init; }
}

/// <summary>One day of the feed ingestion error trend.</summary>
public sealed record FeedIngestionErrorTrendPoint(DateOnly Date, int OpenCount, int PastThresholdCount);

/// <summary>
/// Pure detection logic for the <c>feed_ingestion_error</c> monitor: a feed whose ingestion is failing
/// now but was completing recently.
/// </summary>
/// <remarks>
/// Deliberately free of BigQuery and ASP.NET types so it can be unit tested against hand-written
/// histories — see <c>MonitorApi.Admin.Tests/Errors/FeedIngestionErrorDetectorTests.cs</c>.
///
/// Two exclusions keep the monitor to failures somebody can act on today:
///
/// <list type="bullet">
/// <item>
/// Feeds that have not completed an ingestion within
/// <see cref="FeedIngestionErrorThresholds.SuccessLookbackDays"/> of the evaluated day are treated as
/// permanently broken, not newly failing. Without this, the long tail of feeds that have never worked
/// would swamp the list.
/// </item>
/// <item>
/// Failures whose <c>error_code</c> is in <see cref="AuthErrorCodes"/> are left out: an authorisation
/// failure is a credentials problem rather than a broken feed, and gets its own monitor.
/// </item>
/// </list>
///
/// Unlike <see cref="SingleFeedStallDetector"/> there is no dataset-wide exclusion. A publisher whose
/// whole estate errors on the same day is exactly what this monitor should surface, and the shared
/// <c>error_code</c> across its feeds is the evidence for it.
/// </remarks>
public static class FeedIngestionErrorDetector
{
	/// <summary>Monitor identifier echoed on every incident.</summary>
	public const string MonitorId = "feed_ingestion_error";

	/// <summary>
	/// Failure codes that mean "we are not allowed in" rather than "the feed is broken". These are
	/// excluded here and reported by their own monitor, so a publisher is not chased about a feed that
	/// is working and simply needs a credential.
	/// </summary>
	public static readonly IReadOnlySet<string> AuthErrorCodes =
		new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "401", "403" };

	/// <summary>
	/// Detects the ingestion errors that are open on <paramref name="asOf"/>, ordered longest-running
	/// first.
	/// </summary>
	public static IReadOnlyList<FeedIngestionError> Detect(
		IEnumerable<FeedStatusHistory> feeds,
		DateOnly asOf,
		FeedIngestionErrorThresholds thresholds)
	{
		var incidents = new List<FeedIngestionError>();

		foreach (var feed in feeds.Select(Prepare))
		{
			var incident = Evaluate(feed, asOf, thresholds);
			if (incident is null)
			{
				continue;
			}

			incidents.Add(incident with { Trend = TrendFor(feed, asOf, thresholds) });
		}

		return incidents
			.OrderByDescending(i => i.ConsecutiveDays)
			.ThenBy(i => i.FeedId, StringComparer.Ordinal)
			.ToList();
	}

	/// <summary>
	/// Counts open and past-threshold errors on each of the
	/// <see cref="FeedIngestionErrorThresholds.TrendDays"/> days ending at <paramref name="asOf"/>,
	/// oldest first. Each day is evaluated independently, so the series reflects what the incidents
	/// endpoint would have reported on that day.
	/// </summary>
	/// <remarks>
	/// <c>error_code</c> was added to <c>opportunity_ingestion</c> only recently, so on days before it
	/// was populated the <see cref="AuthErrorCodes"/> exclusion cannot apply and those days count
	/// authorisation failures as ingestion errors. Expect a step down in the series on the first day
	/// carrying codes.
	/// </remarks>
	public static IReadOnlyList<FeedIngestionErrorTrendPoint> Trend(
		IEnumerable<FeedStatusHistory> feeds,
		DateOnly asOf,
		FeedIngestionErrorThresholds thresholds)
	{
		var prepared = feeds.Select(Prepare).ToList();
		var points = new List<FeedIngestionErrorTrendPoint>(thresholds.TrendDays);

		for (var offset = thresholds.TrendDays - 1; offset >= 0; offset--)
		{
			var day = asOf.AddDays(-offset);

			var open = 0;
			var pastThreshold = 0;

			foreach (var feed in prepared)
			{
				var incident = Evaluate(feed, day, thresholds);
				if (incident is null)
				{
					continue;
				}

				open++;
				if (incident.PastThreshold)
				{
					pastThreshold++;
				}
			}

			points.Add(new FeedIngestionErrorTrendPoint(day, open, pastThreshold));
		}

		return points;
	}

	/// <summary>
	/// Returns the incident for a feed on a given day, or <c>null</c> when the feed's ingestion did not
	/// fail that day, has been failing for too short or too long, or failed for a reason this monitor
	/// does not report.
	/// </summary>
	private static FeedIngestionError? Evaluate(
		PreparedFeed feed,
		DateOnly asOf,
		FeedIngestionErrorThresholds thresholds)
	{
		if (!feed.ByDay.TryGetValue(asOf, out var day) || !day.Errored)
		{
			// No run that day, or a run that did not fail. Note that a day carrying both a failed and a
			// completed run has already collapsed to "complete": the feed did deliver.
			return null;
		}

		if (day.ErrorCode is { } code && AuthErrorCodes.Contains(code))
		{
			return null;
		}

		var lastCompleted = LastCompletedOnOrBefore(feed.CompletedDays, asOf);
		if (lastCompleted is null)
		{
			// Never completed inside the loaded window — nothing to say it ever worked.
			return null;
		}

		// At least one, because the evaluated day failed and so cannot itself be the last completed day.
		var failingDays = asOf.DayNumber - lastCompleted.Value.DayNumber;

		if (failingDays < thresholds.ErrorDays || failingDays > thresholds.SuccessLookbackDays)
		{
			return null;
		}

		return new FeedIngestionError
		{
			FeedId = feed.History.FeedId,
			DatasetId = feed.History.DatasetId,
			LastCompleted = lastCompleted.Value,
			ConsecutiveDays = failingDays,
			PastThreshold = failingDays >= thresholds.EffectivePastThresholdDays,
			ErrorCode = day.ErrorCode,
			ErrorMessage = day.ErrorMessage,
			Trend = [],
		};
	}

	/// <summary>
	/// The feed's failing days over the trailing trend window, oldest first — the run of healthy days
	/// before the failure started, so the dashboard can see how abrupt it was.
	/// </summary>
	/// <remarks>
	/// Always exactly <see cref="FeedIngestionErrorThresholds.IncidentTrendDays"/> entries ending at
	/// <paramref name="asOf"/>, so entry <c>i</c> is the same day for every incident in a response and a
	/// chart can align them. A day with no ingestion run counts as <c>0</c> alongside the days that
	/// succeeded: the series answers "did this feed fail on that day", and an absent run did not.
	/// </remarks>
	private static IReadOnlyList<int> TrendFor(
		PreparedFeed feed,
		DateOnly asOf,
		FeedIngestionErrorThresholds thresholds)
	{
		var trend = new List<int>(thresholds.IncidentTrendDays);

		for (var offset = thresholds.IncidentTrendDays - 1; offset >= 0; offset--)
		{
			var day = asOf.AddDays(-offset);
			trend.Add(feed.ByDay.TryGetValue(day, out var entry) && entry.Errored ? 1 : 0);
		}

		return trend;
	}

	/// <summary>
	/// A feed's history indexed for the repeated day-by-day evaluation the trend endpoint does, so the
	/// lookups are not re-derived once per day per feed.
	/// </summary>
	private sealed record PreparedFeed(
		FeedStatusHistory History,
		IReadOnlyDictionary<DateOnly, FeedIngestionDay> ByDay,
		IReadOnlyList<DateOnly> CompletedDays);

	private static PreparedFeed Prepare(FeedStatusHistory feed)
	{
		var byDay = new Dictionary<DateOnly, FeedIngestionDay>(feed.Days.Count);
		var completed = new List<DateOnly>();

		foreach (var day in feed.Days)
		{
			// A duplicate day in the input keeps its first entry; the query already collapses a day's runs
			// into one, so this only guards against a hand-built history.
			if (!byDay.TryAdd(day.Day, day))
			{
				continue;
			}

			if (day.Completed)
			{
				completed.Add(day.Day);
			}
		}

		completed.Sort();
		return new PreparedFeed(feed, byDay, completed);
	}

	/// <summary>
	/// Most recent completed day at or before <paramref name="asOf"/>, or <c>null</c> if there is none.
	/// <paramref name="days"/> must be sorted ascending.
	/// </summary>
	private static DateOnly? LastCompletedOnOrBefore(IReadOnlyList<DateOnly> days, DateOnly asOf)
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

/// <summary>
/// SQL and row parsing for the per-feed <c>opportunity_ingestion</c> status history that
/// <see cref="FeedIngestionErrorDetector"/> runs on.
/// </summary>
/// <remarks>
/// The sibling of <see cref="IngestionHistoryQuery"/>, which reads the same table for the <c>updated</c>
/// counts the stall monitors detect on. Kept apart because the two shapes share no columns beyond the
/// keys. Table names are passed in already fully qualified by the caller's <c>Fq</c>.
/// </remarks>
internal static class IngestionStatusQuery
{
	/// <summary>
	/// Per-feed daily ingestion status over a date window, one row per feed carrying an array of days.
	/// </summary>
	/// <remarks>
	/// Multiple runs on the same day collapse into one entry, and <em>success wins</em>: a day with both
	/// a completed and a failed run reads as <c>complete</c>, because the feed did deliver. Where a day
	/// has failed runs only, its <c>error_code</c> and message come from the latest of them, so the two
	/// always describe the same run.
	/// </remarks>
	/// <param name="ingestionTable">Fully qualified <c>opportunity_ingestion</c> table name.</param>
	public static string StatusHistorySql(string ingestionTable) =>
		$"""
		WITH daily AS (
		  SELECT feed_id,
		         -- MIN rather than ANY_VALUE: a handful of feed ids appear under two dataset_ids (the same
		         -- publisher on an old and a new hostname), and ANY_VALUE would pick a different one from
		         -- one query to the next.
		         MIN(dataset_id) AS dataset_id,
		         DATE(ingestion_date) AS ingestion_day,
		         IF(LOGICAL_OR(status = 'COMPLETE'), 'complete',
		            IF(LOGICAL_OR(status = 'ERROR'), 'error', LOWER(ANY_VALUE(status)))) AS day_status,
		         ANY_VALUE(IF(status = 'ERROR', error_code, NULL) HAVING MAX ingestion_date) AS error_code,
		         ANY_VALUE(IF(status = 'ERROR', warning_message, NULL) HAVING MAX ingestion_date) AS error_message
		  FROM {ingestionTable}
		  WHERE DATE(ingestion_date) BETWEEN @window_start AND @window_end
		        AND feed_id IS NOT NULL
		        AND dataset_id IS NOT NULL
		  GROUP BY feed_id, ingestion_day
		)
		SELECT feed_id,
		       MIN(dataset_id) AS dataset_id,
		       ARRAY_AGG(STRUCT(FORMAT_DATE('%F', ingestion_day) AS day,
		                        day_status AS status,
		                        error_code,
		                        error_message)
		                 ORDER BY ingestion_day) AS days
		FROM daily
		GROUP BY feed_id
		""";

	public static IReadOnlyList<BigQueryParameter> StatusHistoryParameters(DateOnly windowStart, DateOnly windowEnd) =>
	[
		new BigQueryParameter("window_start", BigQueryDbType.Date, windowStart.ToDateTime(TimeOnly.MinValue)),
		new BigQueryParameter("window_end", BigQueryDbType.Date, windowEnd.ToDateTime(TimeOnly.MinValue)),
	];

	public static FeedStatusHistory ParseStatusHistory(Dictionary<string, object> row) =>
		new(
			(string)row["feed_id"],
			(string)row["dataset_id"],
			ParseDays(row.GetValueOrDefault("days")));

	/// <summary>
	/// Parses the repeated <c>STRUCT&lt;day STRING, status STRING, error_code STRING, error_message
	/// STRING&gt;</c> into ascending day entries. The BigQuery client surfaces a repeated struct as
	/// <c>Dictionary&lt;string, object&gt;[]</c>, and a <c>NULL</c> field is absent from that dictionary
	/// rather than present as null.
	/// </summary>
	private static IReadOnlyList<FeedIngestionDay> ParseDays(object? cell)
	{
		var days = new List<FeedIngestionDay>();

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
			var status = Field(fields, "status") as string;

			if (day is null || string.IsNullOrEmpty(status))
			{
				continue;
			}

			days.Add(new FeedIngestionDay(
				day.Value,
				status,
				Field(fields, "error_code") as string,
				Field(fields, "error_message") as string));
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
