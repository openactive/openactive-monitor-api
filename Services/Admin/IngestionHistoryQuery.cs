using Google.Cloud.BigQuery.V2;
using MonitorApi.Models.Admin;
using MonitorApi.Models;

namespace MonitorApi.Services.Admin;

/// <summary>
/// SQL and row parsing for the <c>opportunity_ingestion</c> history that the feed-health monitors run
/// on, plus the feed metadata used to hydrate their output.
/// </summary>
/// <remarks>
/// Shared rather than inlined into one controller because every feed-health monitor (single-feed
/// stalls, dataset-wide stalls, ingestion errors, …) needs the same per-feed publishing history.
/// Table names are passed in already fully qualified by the caller's <c>Fq</c>.
/// </remarks>
internal static class IngestionHistoryQuery
{
	/// <summary>
	/// Per-feed publishing history over a date window. Multiple ingestion runs on the same day are
	/// collapsed into one day, and a day counts as published only when the feed reported at least one
	/// updated item that day.
	/// </summary>
	/// <remarks>
	/// Returns two aggregates per feed at different scopes, so the wide detection window does not have
	/// to carry a value per day: <c>published_days</c> spans the whole window and drives detection, while
	/// <c>recent</c> spans only <c>@trend_start</c> onwards and carries the daily <c>updated</c> counts
	/// used for the per-incident trend column.
	///
	/// A feed id can appear against more than one <c>dataset_id</c>, because a publisher that moves its
	/// dataset to a new hostname keeps its feed ids: the same feed then has rows under the old name
	/// before the move and the new name after it. Every row is the same feed's history and is kept, and
	/// the feed is attributed to the dataset of its most recent ingestion — the name the dataset has now.
	/// </remarks>
	/// <param name="ingestionTable">Fully qualified <c>opportunity_ingestion</c> table name.</param>
	/// <param name="ignoreFirstIngestionDate">
	/// When set, drops rows on the earliest <c>ingestion_date</c> in the table so the initial bulk data
	/// load is not counted as a day the feeds published.
	/// </param>
	public static string HistorySql(string ingestionTable, bool ignoreFirstIngestionDate = false)
	{
		var firstDateFilter = ignoreFirstIngestionDate
			? $"AND DATE(ingestion_date) > (SELECT MIN(DATE(ingestion_date)) FROM {ingestionTable})"
			: "";

		return
			$"""
			WITH daily AS (
			  SELECT feed_id,
			         DATE(ingestion_date) AS ingestion_day,
			         SUM(updated) AS updated,
			         -- The dataset the feed was ingested under that day, taken from the day's last run.
			         ARRAY_AGG(dataset_id ORDER BY ingestion_date DESC, dataset_id LIMIT 1)[OFFSET(0)] AS dataset_id
			  FROM {ingestionTable}
			  WHERE DATE(ingestion_date) BETWEEN @window_start AND @window_end
			        AND feed_id IS NOT NULL
			        AND dataset_id IS NOT NULL
			        {firstDateFilter}
			  GROUP BY feed_id, ingestion_day
			)
			SELECT feed_id,
			       -- The dataset of the feed's most recent ingestion, so a feed whose publisher moved
			       -- hostname is reported under the dataset it belongs to now. ARRAY_AGG rather than
			       -- ANY_VALUE because ANY_VALUE would attribute such a feed differently from one query to
			       -- the next, moving it in and out of the dataset-wide stall exclusion and making the same
			       -- day's incident count differ between endpoints.
			       ARRAY_AGG(dataset_id ORDER BY ingestion_day DESC LIMIT 1)[OFFSET(0)] AS dataset_id,
			       ARRAY_AGG(IF(updated > 0, FORMAT_DATE('%F', ingestion_day), NULL) IGNORE NULLS
			                 ORDER BY ingestion_day) AS published_days,
			       ARRAY_AGG(IF(ingestion_day >= @trend_start,
			                    STRUCT(FORMAT_DATE('%F', ingestion_day) AS day, IFNULL(updated, 0) AS updated),
			                    NULL) IGNORE NULLS
			                 ORDER BY ingestion_day) AS recent
			FROM daily
			GROUP BY feed_id
			""";
	}

	public static IReadOnlyList<BigQueryParameter> HistoryParameters(
		DateOnly windowStart,
		DateOnly windowEnd,
		DateOnly trendStart) =>
	[
		new BigQueryParameter("window_start", BigQueryDbType.Date, windowStart.ToDateTime(TimeOnly.MinValue)),
		new BigQueryParameter("window_end", BigQueryDbType.Date, windowEnd.ToDateTime(TimeOnly.MinValue)),
		new BigQueryParameter("trend_start", BigQueryDbType.Date, trendStart.ToDateTime(TimeOnly.MinValue)),
	];

	/// <summary>Latest day present in the ingestion table — the snapshot date the monitors report against.</summary>
	public static string SnapshotDateSql(string ingestionTable) =>
		$"SELECT MAX(DATE(ingestion_date)) AS snapshot_date FROM {ingestionTable}";

	/// <summary>Descriptive fields and quality score for a specific set of feeds.</summary>
	public static string FeedMetadataSql(string feedsTable, string feedQualityTable) =>
		$"""
		SELECT f.id AS feed_id,
		       f.url AS feed_url,
		       f.type AS feed_type,
		       f.publisher_name,
		       q.score AS quality_score
		FROM {feedsTable} AS f
		LEFT JOIN {feedQualityTable} AS q ON q.feed_id = f.id
		WHERE f.id IN UNNEST(@feed_ids)
		""";

	public static IReadOnlyList<BigQueryParameter> FeedMetadataParameters(IReadOnlyCollection<string> feedIds) =>
	[
		new BigQueryParameter("feed_ids", BigQueryDbType.Array, feedIds.ToList())
		{
			ArrayElementType = BigQueryDbType.String,
		},
	];

	public static FeedIngestionHistory ParseHistory(Dictionary<string, object> row) =>
		new(
			(string)row["feed_id"],
			(string)row["dataset_id"],
			ParseDays(row.GetValueOrDefault("published_days")),
			ParseRecentUpdated(row.GetValueOrDefault("recent")));

	/// <summary>
	/// Parses the repeated <c>STRUCT&lt;day STRING, updated INT64&gt;</c> into daily updated counts. The
	/// BigQuery client surfaces a repeated struct as <c>Dictionary&lt;string, object&gt;[]</c>.
	/// </summary>
	private static IReadOnlyDictionary<DateOnly, long> ParseRecentUpdated(object? cell)
	{
		var updatedByDay = new Dictionary<DateOnly, long>();

		if (cell is not System.Collections.IEnumerable rows || cell is string)
		{
			return updatedByDay;
		}

		foreach (var item in rows)
		{
			if (item is not IDictionary<string, object> fields)
			{
				continue;
			}

			var day = ParseDay(fields.TryGetValue("day", out var rawDay) ? rawDay : null);
			if (day is null)
			{
				continue;
			}

			updatedByDay[day.Value] =
				BigQueryValueParser.AsLong(fields.TryGetValue("updated", out var rawUpdated) ? rawUpdated : null) ?? 0;
		}

		return updatedByDay;
	}

	public static FeedMetadata ParseFeedMetadata(Dictionary<string, object> row) =>
		new(
			(string)row["feed_id"],
			row.GetValueOrDefault("feed_url") as string,
			row.GetValueOrDefault("feed_type") as string,
			row.GetValueOrDefault("publisher_name") as string,
			BigQueryValueParser.AsDouble(row.GetValueOrDefault("quality_score")));

	/// <summary>
	/// Parses the <c>ARRAY&lt;STRING&gt;</c> of <c>yyyy-MM-dd</c> days. Kept tolerant of the concrete
	/// collection type the BigQuery client hands back for a repeated column.
	/// </summary>
	private static IReadOnlyList<DateOnly> ParseDays(object? cell)
	{
		if (cell is null)
		{
			return [];
		}

		// A single string must not be treated as a char sequence.
		IEnumerable<object?> items = cell is string single
			? [single]
			: cell is System.Collections.IEnumerable sequence
				? sequence.Cast<object?>()
				: [cell];

		var days = new List<DateOnly>();

		foreach (var item in items)
		{
			if (ParseDay(item) is { } day)
			{
				days.Add(day);
			}
		}

		days.Sort();
		return days;
	}

	private static DateOnly? ParseDay(object? value) => value switch
	{
		DateTime dateTime => DateOnly.FromDateTime(dateTime),
		DateOnly day => day,
		not null when value.ToString() is { Length: > 0 } text &&
			DateOnly.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
		_ => null,
	};
}
