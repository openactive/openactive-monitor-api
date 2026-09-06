using MonitorApi.Models;

namespace MonitorApi.Services.Admin;

/// <summary>
/// Coverage and error counts behind <c>/admin/summary</c>. Table names arrive already fully qualified
/// by the caller's <c>Fq</c>.
/// </summary>
internal static class AdminSummaryQuery
{
	/// <summary>
	/// Size of the monitored estate as at the most recent <c>feed_ingestion</c> run, which also dates the
	/// summary.
	/// </summary>
	public static string CoverageSql(string feedIngestionTable) =>
		$"""
		SELECT number_of_datasets, number_of_feeds, ingestion_date
		FROM {feedIngestionTable}
		ORDER BY ingestion_date DESC
		LIMIT 1
		""";

	/// <summary>
	/// Datasets with at least one feed that failed ingestion today. Bounded at midnight UTC rather than
	/// over a window, so this is "went wrong in today's run", not a running total.
	/// </summary>
	public static string DatasetsWithErrorsTodaySql(string opportunityIngestionTable) =>
		$"""
		SELECT COUNT(DISTINCT dataset_id) AS error_dataset_count
		FROM {opportunityIngestionTable}
		WHERE status = 'ERROR'
		  AND ingestion_date >= TIMESTAMP(CURRENT_DATE())
		""";

	/// <summary>
	/// Reads the coverage row. Falls back to zero counts and <paramref name="fallbackGeneratedAt"/> when
	/// the table is empty or a column is <c>NULL</c>.
	/// </summary>
	public static AdminCoverage ParseCoverage(Dictionary<string, object>? row, DateTime fallbackGeneratedAt) =>
		new(
			BigQueryValueParser.AsLong(row?.GetValueOrDefault("number_of_datasets")) ?? 0,
			BigQueryValueParser.AsLong(row?.GetValueOrDefault("number_of_feeds")) ?? 0,
			row?.GetValueOrDefault("ingestion_date") is DateTime ingestedAt
				? DateTime.SpecifyKind(ingestedAt, DateTimeKind.Utc)
				: fallbackGeneratedAt);

	public static long ParseErrorDatasetCount(Dictionary<string, object>? row) =>
		BigQueryValueParser.AsLong(row?.GetValueOrDefault("error_dataset_count")) ?? 0;
}

/// <summary>
/// The monitored estate as at one <c>feed_ingestion</c> run.
/// </summary>
/// <param name="Datasets">Datasets covered by that run.</param>
/// <param name="Feeds">Feeds covered by that run.</param>
/// <param name="GeneratedAt">When the run happened — the moment the summary's figures describe.</param>
internal sealed record AdminCoverage(long Datasets, long Feeds, DateTime GeneratedAt);
