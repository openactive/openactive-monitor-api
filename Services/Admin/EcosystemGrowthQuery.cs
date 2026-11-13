using System.Globalization;
using Google.Cloud.BigQuery.V2;
using MonitorApi.Models;

namespace MonitorApi.Services.Admin;

/// <summary>
/// SQL and row parsing for the per-dataset, per-month figures that <see cref="EcosystemGrowthCalculator"/>
/// runs on. Table names are passed in already fully qualified by the caller's <c>Fq</c>.
/// </summary>
/// <remarks>
/// Reads two tables. <c>feed_ingestion</c> says which datasets the catalogues listed on each crawl, so it
/// is the crawl list. <c>opportunity_ingestion</c> says what each dataset's feeds did, so it gives future
/// items and publishing. The second is a subset of the first: a dataset whose feeds yield nothing is
/// listed but has no feed rows.
///
/// Unlike <see cref="IngestionHistoryQuery"/>, <see cref="IngestionStatusQuery"/> and
/// <see cref="FutureSupplyQuery"/>, which follow a <em>feed</em> to the dataset of its latest run, this
/// counts <em>dataset URLs</em> as the crawler saw them. A publisher that moves hostname is one dataset
/// removed and another added, which is what the catalogue shows.
/// </remarks>
internal static class EcosystemGrowthQuery
{
	/// <summary>
	/// One row per <c>(month, dataset_id)</c> across the window, carrying both of the month's coverage
	/// figures. A month with data but no dataset at all still comes back, once, with <c>dataset_id</c>
	/// <c>NULL</c>, so its coverage is not lost.
	/// </summary>
	/// <remarks>
	/// <c>in_catalogue</c> marks a dataset listed by <c>feed_ingestion</c>. <c>has_feeds</c> marks one with
	/// any <c>opportunity_ingestion</c> row, and only then are <c>changes</c>, <c>max_future</c> and
	/// <c>all_runs_failed</c> set.
	///
	/// Every feed row counts, whatever its kind or status: a crawl that failed is still a crawl that was
	/// tried, and <c>Slot</c> feeds are often all a facility-booking dataset publishes. <c>NULL</c> counters
	/// and levels read as zero. Same-day duplicate runs need no collapsing: they cannot turn a zero
	/// <c>changes</c> sum non-zero, and <c>MAX</c> is unaffected by repeats.
	/// </remarks>
	/// <param name="feedIngestionTable">Fully qualified <c>feed_ingestion</c> table name.</param>
	/// <param name="opportunityIngestionTable">Fully qualified <c>opportunity_ingestion</c> table name.</param>
	public static string DatasetMonthsSql(string feedIngestionTable, string opportunityIngestionTable) =>
		$"""
		WITH crawls AS (
		  SELECT DATE(ingestion_date) AS crawl_day, datasets
		  FROM {feedIngestionTable}
		  WHERE DATE(ingestion_date) BETWEEN @window_start AND @window_end
		),
		crawl_days AS (
		  SELECT DATE_TRUNC(crawl_day, MONTH) AS month, COUNT(DISTINCT crawl_day) AS days_with_data
		  FROM crawls
		  GROUP BY month
		),
		catalogue AS (
		  SELECT DISTINCT DATE_TRUNC(crawl_day, MONTH) AS month, JSON_VALUE(url) AS dataset_id
		  FROM crawls, UNNEST(JSON_QUERY_ARRAY(datasets)) AS url
		  WHERE JSON_VALUE(url) IS NOT NULL AND JSON_VALUE(url) != ''
		),
		feed_rows AS (
		  SELECT DATE_TRUNC(DATE(ingestion_date), MONTH) AS month,
		         DATE(ingestion_date) AS ingestion_day,
		         dataset_id,
		         updated,
		         deleted,
		         total_future_opportunities,
		         status
		  FROM {opportunityIngestionTable}
		  WHERE DATE(ingestion_date) BETWEEN @window_start AND @window_end
		),
		feed_days AS (
		  SELECT month, COUNT(DISTINCT ingestion_day) AS days_with_feed_data
		  FROM feed_rows
		  GROUP BY month
		),
		activity AS (
		  SELECT month,
		         dataset_id,
		         SUM(IFNULL(updated, 0)) + SUM(IFNULL(deleted, 0)) AS changes,
		         MAX(IFNULL(total_future_opportunities, 0)) AS max_future,
		         LOGICAL_AND(IFNULL(status, '') = 'ERROR') AS all_runs_failed
		  FROM feed_rows
		  WHERE dataset_id IS NOT NULL
		  GROUP BY month, dataset_id
		),
		months AS (
		  SELECT month FROM crawl_days
		  UNION DISTINCT
		  SELECT month FROM feed_days
		),
		datasets AS (
		  SELECT month, dataset_id FROM catalogue
		  UNION DISTINCT
		  SELECT month, dataset_id FROM activity
		)
		SELECT FORMAT_DATE('%F', m.month) AS month,
		       IFNULL(cd.days_with_data, 0) AS days_with_data,
		       IFNULL(fd.days_with_feed_data, 0) AS days_with_feed_data,
		       d.dataset_id,
		       c.dataset_id IS NOT NULL AS in_catalogue,
		       a.dataset_id IS NOT NULL AS has_feeds,
		       a.changes,
		       a.max_future,
		       a.all_runs_failed
		FROM months AS m
		LEFT JOIN crawl_days AS cd ON cd.month = m.month
		LEFT JOIN feed_days AS fd ON fd.month = m.month
		LEFT JOIN datasets AS d ON d.month = m.month
		LEFT JOIN catalogue AS c ON c.month = m.month AND c.dataset_id = d.dataset_id
		LEFT JOIN activity AS a ON a.month = m.month AND a.dataset_id = d.dataset_id
		ORDER BY month, dataset_id
		""";

	public static IReadOnlyList<BigQueryParameter> DatasetMonthsParameters(DateOnly windowStart, DateOnly windowEnd) =>
	[
		new BigQueryParameter("window_start", BigQueryDbType.Date, windowStart.ToDateTime(TimeOnly.MinValue)),
		new BigQueryParameter("window_end", BigQueryDbType.Date, windowEnd.ToDateTime(TimeOnly.MinValue)),
	];

	/// <summary>
	/// Parses one row into the month's coverage and, unless the row is the coverage-only placeholder, the
	/// dataset's catalogue listing and feed activity in that month. Either can be <c>null</c> for a given
	/// dataset.
	/// </summary>
	public static (MonthCoverage Coverage, CatalogueListing? Listing, DatasetMonthActivity? Activity) ParseDatasetMonth(
		Dictionary<string, object> row)
	{
		var month = DateOnly.ParseExact((string)row["month"], "yyyy-MM-dd", CultureInfo.InvariantCulture);
		var coverage = new MonthCoverage(
			month,
			(int)(BigQueryValueParser.AsLong(row.GetValueOrDefault("days_with_data")) ?? 0),
			(int)(BigQueryValueParser.AsLong(row.GetValueOrDefault("days_with_feed_data")) ?? 0));

		if (row.GetValueOrDefault("dataset_id") is not string datasetId || datasetId.Length == 0)
		{
			return (coverage, null, null);
		}

		var listing = BigQueryValueParser.AsBool(row.GetValueOrDefault("in_catalogue")) == true
			? new CatalogueListing(month, datasetId)
			: null;

		var activity = BigQueryValueParser.AsBool(row.GetValueOrDefault("has_feeds")) == true
			? new DatasetMonthActivity(
				month,
				datasetId,
				BigQueryValueParser.AsLong(row.GetValueOrDefault("changes")) ?? 0,
				BigQueryValueParser.AsLong(row.GetValueOrDefault("max_future")) ?? 0,
				BigQueryValueParser.AsBool(row.GetValueOrDefault("all_runs_failed")) ?? false)
			: null;

		return (coverage, listing, activity);
	}
}
