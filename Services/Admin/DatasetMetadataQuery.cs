using Google.Cloud.BigQuery.V2;
using MonitorApi.Models.Admin;

namespace MonitorApi.Services.Admin;

/// <summary>
/// SQL and row parsing for dataset identity — the <c>dataset_url</c>-keyed counterpart of the feed
/// lookup in <see cref="IngestionHistoryQuery"/>.
/// </summary>
/// <remarks>
/// Kept out of any one monitor's file because it is monitor-agnostic: every dataset-scoped monitor
/// needs the same publisher name, and putting it in the first such monitor would make the next one
/// depend on it. Table names are passed in already fully qualified by the caller's <c>Fq</c>.
/// </remarks>
internal static class DatasetMetadataQuery
{
	/// <summary>
	/// Publisher name and dataset name for a specific set of datasets.
	/// </summary>
	/// <remarks>
	/// A dataset has many rows in <c>feeds</c> <em>and</em> many in <c>feed_quality</c> — one per feed,
	/// each repeating the dataset name — so joining them raw would fan out. Each side is collapsed to
	/// one row per <c>dataset_url</c> first.
	///
	/// <c>MIN</c> rather than <c>ANY_VALUE</c>, for the reason
	/// <see cref="IngestionStatusQuery.StatusHistorySql"/> documents: where a dataset's feeds disagree
	/// on <c>publisher_name</c>, <c>ANY_VALUE</c> would pick a different one from one request to the
	/// next and the same incident would appear to change identity between the incidents endpoint and
	/// the summary tile.
	///
	/// Driven from <c>@dataset_urls</c> rather than from <c>feeds</c>, so a dataset that appears in
	/// <c>opportunities</c> but has no <c>feeds</c> row still comes back and its incident is reported
	/// with the descriptive fields left empty.
	/// </remarks>
	public static string DatasetMetadataSql(string feedsTable, string feedQualityTable) =>
		$"""
		WITH requested AS (
		  SELECT DISTINCT url AS dataset_url
		  FROM UNNEST(@dataset_urls) AS url
		),
		publishers AS (
		  SELECT dataset_url, MIN(publisher_name) AS publisher_name
		  FROM {feedsTable}
		  WHERE dataset_url IN UNNEST(@dataset_urls)
		  GROUP BY dataset_url
		),
		names AS (
		  SELECT dataset_url, MIN(dataset_name) AS dataset_name
		  FROM {feedQualityTable}
		  WHERE dataset_url IN UNNEST(@dataset_urls)
		  GROUP BY dataset_url
		)
		SELECT r.dataset_url,
		       p.publisher_name,
		       n.dataset_name
		FROM requested AS r
		LEFT JOIN publishers AS p USING (dataset_url)
		LEFT JOIN names AS n USING (dataset_url)
		""";

	/// <summary>
	/// The datasets to describe. Blank entries are dropped and the rest de-duplicated: a BigQuery
	/// <c>ARRAY</c> parameter cannot carry NULL elements.
	/// </summary>
	public static IReadOnlyList<BigQueryParameter> DatasetMetadataParameters(IReadOnlyCollection<string> datasetUrls) =>
	[
		new BigQueryParameter(
			"dataset_urls",
			BigQueryDbType.Array,
			datasetUrls
				.Where(url => !string.IsNullOrWhiteSpace(url))
				.Distinct(StringComparer.Ordinal)
				.ToList())
		{
			ArrayElementType = BigQueryDbType.String,
		},
	];

	public static DatasetMetadata ParseDatasetMetadata(Dictionary<string, object> row) =>
		new(
			(string)row["dataset_url"],
			row.GetValueOrDefault("dataset_name") as string,
			row.GetValueOrDefault("publisher_name") as string);
}
