// Everything the schema-drift surface is made of, in one file: the per-feed custom-property
// assessment it reads, the SQL that loads it, and the pure rules that decide which feeds are reported
// and reduce them to estate-level figures — the same one-file convention as FeedQualityMonitor.cs, and
// the same split between *deciding* and *fetching*: CustomPropertiesSummariser references no BigQuery
// and no ASP.NET, so it is unit tested without credentials.
//
// Like FeedQualityMonitor.cs this is not a monitor: custom_properties is a snapshot with no history,
// so nothing here detects an incident, opens one, or tracks one over time.

using System.Collections;
using Google.Cloud.BigQuery.V2;
using MonitorApi.Models;
using MonitorApi.Models.Admin;

namespace MonitorApi.Services.Admin;

/// <summary>
/// One feed's custom-property assessment, as stored in <c>custom_properties</c>: the row the endpoint
/// returns and the summariser reduces.
/// </summary>
/// <remarks>
/// Every field but the two identifiers is nullable, because every column but those two is. A
/// <c>null</c> means the assessment did not report the value, never that it reported zero.
/// </remarks>
/// <param name="FeedId">Identifier of the feed, matching <c>feeds.id</c>.</param>
/// <param name="DatasetUrl">Dataset the feed belongs to, matching <c>feeds.dataset_url</c>.</param>
/// <param name="DatasetName">Stored dataset name, or <c>null</c>.</param>
/// <param name="PublisherName">Stored publisher name, or <c>null</c>.</param>
/// <param name="FeedUrl">URL the feed is published at.</param>
/// <param name="FeedType">Opportunity kind the feed publishes, e.g. <c>SessionSeries</c>.</param>
/// <param name="IsRegular">Whether the feed publishes on a regular schedule; <c>null</c> when not determined.</param>
/// <param name="SampledItems">Opportunity rows sampled for the assessment.</param>
/// <param name="NumCustomProperties">Distinct custom property names the feed uses.</param>
/// <param name="NumCustomPropertyUsages">Distinct (entity type, property) pairs the feed uses.</param>
/// <param name="CustomProperties">One entry per (property, entity type), most widely present first.</param>
public sealed record FeedCustomProperties(
	string FeedId,
	string DatasetUrl,
	string? DatasetName,
	string? PublisherName,
	string? FeedUrl,
	string? FeedType,
	bool? IsRegular,
	long? SampledItems,
	long? NumCustomProperties,
	long? NumCustomPropertyUsages,
	IReadOnlyList<CustomPropertyUsage> CustomProperties);

/// <summary>
/// One custom property as used on one entity type within one feed.
/// </summary>
/// <param name="Property">Property key as published, e.g. <c>beta:formattedDescription</c>.</param>
/// <param name="Namespace">Prefix before the <c>:</c>, or <c>null</c> when unprefixed or a full URI.</param>
/// <param name="EntityType"><c>@type</c> of the object carrying the property.</param>
/// <param name="PresencePct">Percentage of sampled instances of that type carrying the property, 0–100.</param>
public sealed record CustomPropertyUsage(
	string? Property,
	string? Namespace,
	string? EntityType,
	double? PresencePct);

/// <summary>
/// Decides which feeds the schema-drift endpoint reports, and reduces the assessed estate to the
/// figures the dashboard shows above the table.
/// </summary>
/// <remarks>
/// Pure, over plain records: no BigQuery and no ASP.NET, so every rule is unit tested against
/// hand-written assessments with no credentials.
///
/// The rules that are not obvious from the field names:
///
/// <list type="bullet">
/// <item><description>
/// A feed is reported only when its <c>num_custom_properties</c> is greater than zero. A <c>null</c>
/// count is not evidence of drift, so that feed is not reported — but it still counts as assessed.
/// </description></item>
/// <item><description>
/// Every figure but <c>feeds_assessed</c> and <c>datasets_assessed</c> describes the reported feeds
/// only, so <c>feeds_with_custom_properties</c> always equals the number of rows being paged.
/// </description></item>
/// <item><description>
/// Property names are compared ordinally, since they are JSON keys and <c>beta:foo</c> and
/// <c>beta:Foo</c> are different keys. Dataset and publisher identity is compared case-insensitively,
/// as on <c>/admin/feed-quality</c>.
/// </description></item>
/// <item><description>
/// A feed is identified by <c>(dataset_url, feed_id)</c>, not <c>feed_id</c> alone: the same feed
/// served under two dataset URLs is assessed, and counted, once per dataset.
/// </description></item>
/// <item><description>
/// Every breakdown is totally ordered — feed count descending, then value ascending — so the same
/// input always produces the same response.
/// </description></item>
/// </list>
/// </remarks>
public static class CustomPropertiesSummariser
{
	/// <summary>Value stood in for an entity type the assessment did not record.</summary>
	public const string UnknownValue = "unknown";

	/// <summary>
	/// Whether a feed is reported: it uses at least one custom property. A feed whose count is
	/// <c>null</c> is not.
	/// </summary>
	public static bool UsesCustomProperties(FeedCustomProperties feed) => feed.NumCustomProperties > 0;

	/// <summary>The feeds the endpoint reports, in the order given.</summary>
	public static IReadOnlyList<FeedCustomProperties> Reported(IEnumerable<FeedCustomProperties> assessed) =>
		[.. assessed.Where(UsesCustomProperties)];

	/// <summary>
	/// Reduces every assessed feed — reported or not — to one summary. Takes the whole filtered set,
	/// not a page, so the figures do not move as pages are walked.
	/// </summary>
	public static CustomPropertiesSummary Summarise(IEnumerable<FeedCustomProperties> assessed)
	{
		var all = assessed as IReadOnlyList<FeedCustomProperties> ?? assessed.ToList();
		var reported = Reported(all);

		var usages = reported
			.SelectMany(f => f.CustomProperties.Select(u => (Feed: f, Usage: u)))
			.Where(x => !string.IsNullOrWhiteSpace(x.Usage.Property))
			.ToList();

		return new CustomPropertiesSummary
		{
			FeedsAssessed = all.Count,
			DatasetsAssessed = Distinct(all, f => f.DatasetUrl),

			FeedsWithCustomProperties = reported.Count,
			DatasetsWithCustomProperties = Distinct(reported, f => f.DatasetUrl),
			PublishersWithCustomProperties = Distinct(reported, f => f.PublisherName),
			FeedShare = all.Count > 0 ? (double)reported.Count / all.Count : null,

			DistinctCustomProperties = usages.Select(x => x.Usage.Property!).Distinct(StringComparer.Ordinal).Count(),
			TotalCustomPropertyUsages = reported.Sum(f => f.NumCustomPropertyUsages ?? 0),

			NamespaceBreakdown = NamespaceBreakdown(usages),
			EntityTypeBreakdown = EntityTypeBreakdown(usages),
			PropertyBreakdown = PropertyBreakdown(usages),
		};
	}

	#region Rules

	/// <summary>
	/// Distinct non-blank values of a key. Blanks are dropped rather than counted as one shared value:
	/// two feeds with no publisher name are not two feeds from the same publisher.
	/// </summary>
	private static int Distinct(IEnumerable<FeedCustomProperties> rows, Func<FeedCustomProperties, string?> key) =>
		rows
			.Select(key)
			.Where(v => !string.IsNullOrWhiteSpace(v))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Count();

	/// <summary>
	/// Distinct feeds, identified by <c>(dataset_url, feed_id)</c>. <c>feed_id</c> alone is not unique
	/// in <c>custom_properties</c>: a publisher serving the same feeds under two dataset URLs has one
	/// assessment per dataset, and they are two rows.
	/// </summary>
	private static int CountFeeds(IEnumerable<FeedCustomProperties> feeds) =>
		feeds
			.Select(f => (f.DatasetUrl, f.FeedId))
			.Distinct()
			.Count();

	/// <summary>
	/// Properties, feeds and datasets per namespace. Unprefixed properties (and full URIs) are grouped
	/// under a <c>null</c> namespace, listed last among equal feed counts.
	/// </summary>
	private static IReadOnlyList<CustomPropertyNamespaceBreakdown> NamespaceBreakdown(
		IReadOnlyList<(FeedCustomProperties Feed, CustomPropertyUsage Usage)> usages) =>
		[.. usages
			.GroupBy(x => string.IsNullOrWhiteSpace(x.Usage.Namespace) ? null : x.Usage.Namespace.Trim())
			.Select(g => new CustomPropertyNamespaceBreakdown
			{
				Namespace = g.Key,
				PropertyCount = g.Select(x => x.Usage.Property!).Distinct(StringComparer.Ordinal).Count(),
				FeedCount = CountFeeds(g.Select(x => x.Feed)),
				DatasetCount = Distinct(g.Select(x => x.Feed), f => f.DatasetUrl),
			})
			.OrderByDescending(b => b.FeedCount)
			.ThenBy(b => b.Namespace is null)
			.ThenBy(b => b.Namespace, StringComparer.Ordinal)];

	/// <summary>
	/// Properties, feeds and datasets per entity type. A missing type is <see cref="UnknownValue"/>
	/// rather than dropped.
	/// </summary>
	private static IReadOnlyList<CustomPropertyEntityTypeBreakdown> EntityTypeBreakdown(
		IReadOnlyList<(FeedCustomProperties Feed, CustomPropertyUsage Usage)> usages) =>
		[.. usages
			.GroupBy(x => string.IsNullOrWhiteSpace(x.Usage.EntityType) ? UnknownValue : x.Usage.EntityType.Trim(), StringComparer.Ordinal)
			.Select(g => new CustomPropertyEntityTypeBreakdown
			{
				EntityType = g.Key,
				PropertyCount = g.Select(x => x.Usage.Property!).Distinct(StringComparer.Ordinal).Count(),
				FeedCount = CountFeeds(g.Select(x => x.Feed)),
				DatasetCount = Distinct(g.Select(x => x.Feed), f => f.DatasetUrl),
			})
			.OrderByDescending(b => b.FeedCount)
			.ThenBy(b => b.EntityType, StringComparer.Ordinal)];

	/// <summary>
	/// Every distinct custom property with the feeds and datasets using it and the entity types it was
	/// seen on — the most widespread drift first.
	/// </summary>
	private static IReadOnlyList<CustomPropertyBreakdown> PropertyBreakdown(
		IReadOnlyList<(FeedCustomProperties Feed, CustomPropertyUsage Usage)> usages) =>
		[.. usages
			.GroupBy(x => x.Usage.Property!, StringComparer.Ordinal)
			.Select(g => new CustomPropertyBreakdown
			{
				Property = g.Key,
				// The namespace is derived from the key, so every usage agrees; MIN keeps it deterministic
				// if the assessor ever disagreed with itself.
				Namespace = g
					.Select(x => x.Usage.Namespace)
					.Where(n => !string.IsNullOrWhiteSpace(n))
					.Order(StringComparer.Ordinal)
					.FirstOrDefault(),
				EntityTypes = [.. g
					.Select(x => x.Usage.EntityType)
					.Where(t => !string.IsNullOrWhiteSpace(t))
					.Select(t => t!.Trim())
					.Distinct(StringComparer.Ordinal)
					.Order(StringComparer.Ordinal)],
				FeedCount = CountFeeds(g.Select(x => x.Feed)),
				DatasetCount = Distinct(g.Select(x => x.Feed), f => f.DatasetUrl),
			})
			.OrderByDescending(b => b.FeedCount)
			.ThenBy(b => b.Property, StringComparer.Ordinal)];

	#endregion
}

/// <summary>
/// SQL and row parsing for <c>custom_properties</c>. Table names arrive already fully qualified by the
/// caller's <c>Fq</c>; every filter value goes through a <see cref="BigQueryParameter"/>.
/// </summary>
internal static class CustomPropertiesQuery
{
	/// <summary>
	/// The day the assessments describe: the latest <c>last_assessed</c> in the table. <c>null</c> when
	/// the table is empty.
	/// </summary>
	public static string SnapshotDateSql(string customPropertiesTable) =>
		$"""
		SELECT MAX(DATE(last_assessed)) AS snapshot_date
		FROM {customPropertiesTable}
		""";

	/// <summary>
	/// Every assessed feed matching the filters, including those with no custom properties — deciding
	/// which are reported belongs to <see cref="CustomPropertiesSummariser"/>, and the summary needs
	/// the assessed total as its denominator.
	/// </summary>
	/// <remarks>
	/// The nested array is narrowed to the four fields the endpoint returns, so <c>property_kind</c>,
	/// <c>occurrences</c> and <c>entity_instances</c> are never read, and ordered most widely present
	/// first with a total tiebreak. Rows are ordered most custom properties first, then by dataset and
	/// feed, so the order is total and paging is stable.
	/// </remarks>
	/// <param name="customPropertiesTable">Fully qualified <c>custom_properties</c>.</param>
	/// <param name="filterByDatasetUrl">Whether to restrict to <c>@dataset_urls</c>.</param>
	/// <param name="filterByPublisher">Whether to restrict to <c>@publishers</c>.</param>
	public static string FeedCustomPropertiesSql(
		string customPropertiesTable,
		bool filterByDatasetUrl,
		bool filterByPublisher)
	{
		var conditions = new List<string>();

		if (filterByDatasetUrl)
		{
			conditions.Add("c.dataset_url IN UNNEST(@dataset_urls)");
		}

		if (filterByPublisher)
		{
			conditions.Add("c.publisher_name IN UNNEST(@publishers)");
		}

		var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

		return $"""
			SELECT c.feed_id,
			       c.dataset_url,
			       c.dataset_name,
			       c.publisher_name,
			       c.feed_url,
			       c.feed_type,
			       c.is_regular,
			       c.sampled_items,
			       c.num_custom_properties,
			       c.num_custom_property_usages,
			       ARRAY(
			         SELECT AS STRUCT p.property, p.namespace, p.entity_type, p.presence_pct
			         FROM UNNEST(c.custom_properties) AS p
			         ORDER BY p.presence_pct DESC NULLS LAST, p.property, p.entity_type
			       ) AS custom_properties
			FROM {customPropertiesTable} AS c
			{where}
			ORDER BY c.num_custom_properties DESC NULLS LAST, c.dataset_url, c.feed_id
			""";
	}

	/// <summary>
	/// The filter values, as array parameters. Blank entries are dropped and the rest de-duplicated: a
	/// BigQuery <c>ARRAY</c> parameter cannot carry NULL elements. Only the parameters the SQL actually
	/// references are bound.
	/// </summary>
	public static IReadOnlyList<BigQueryParameter> FeedCustomPropertiesParameters(
		IReadOnlyCollection<string> datasetUrls,
		IReadOnlyCollection<string> publishers)
	{
		var parameters = new List<BigQueryParameter>();

		if (datasetUrls.Count > 0)
		{
			parameters.Add(StringArray("dataset_urls", datasetUrls));
		}

		if (publishers.Count > 0)
		{
			parameters.Add(StringArray("publishers", publishers));
		}

		return parameters;
	}

	private static BigQueryParameter StringArray(string name, IEnumerable<string> values) =>
		new(name, BigQueryDbType.Array, values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal).ToList())
		{
			ArrayElementType = BigQueryDbType.String,
		};

	/// <summary>
	/// Reads one assessment. <c>feed_id</c> and <c>dataset_url</c> are cast directly because the columns
	/// are <c>REQUIRED</c>; everything else goes through <see cref="BigQueryValueParser"/> and
	/// <c>GetValueOrDefault</c>, since a NULL column is absent from the row dictionary.
	/// </summary>
	public static FeedCustomProperties ParseFeedCustomProperties(Dictionary<string, object> row) =>
		new(
			(string)row["feed_id"],
			(string)row["dataset_url"],
			row.GetValueOrDefault("dataset_name") as string,
			row.GetValueOrDefault("publisher_name") as string,
			row.GetValueOrDefault("feed_url") as string,
			row.GetValueOrDefault("feed_type") as string,
			BigQueryValueParser.AsBool(row.GetValueOrDefault("is_regular")),
			BigQueryValueParser.AsLong(row.GetValueOrDefault("sampled_items")),
			BigQueryValueParser.AsLong(row.GetValueOrDefault("num_custom_properties")),
			BigQueryValueParser.AsLong(row.GetValueOrDefault("num_custom_property_usages")),
			ParseUsages(row.GetValueOrDefault("custom_properties")));

	/// <summary>
	/// Reads the snapshot date from <see cref="SnapshotDateSql"/>, or <c>null</c> for an empty table.
	/// </summary>
	public static DateOnly? ParseSnapshotDate(Dictionary<string, object>? row) =>
		row?.GetValueOrDefault("snapshot_date") is DateTime snapshot
			? DateOnly.FromDateTime(snapshot)
			: null;

	/// <summary>
	/// Reads the <c>REPEATED RECORD</c>, which the client hands back as a sequence of dictionaries keyed
	/// by field name. Anything else — including a missing column — is an empty list.
	/// </summary>
	private static IReadOnlyList<CustomPropertyUsage> ParseUsages(object? cell)
	{
		if (cell is not IEnumerable items || cell is string)
		{
			return [];
		}

		var usages = new List<CustomPropertyUsage>();
		foreach (var item in items)
		{
			if (item is not IDictionary<string, object> entry)
			{
				continue;
			}

			usages.Add(new CustomPropertyUsage(
				Field(entry, "property") as string,
				Field(entry, "namespace") as string,
				Field(entry, "entity_type") as string,
				BigQueryValueParser.AsDouble(Field(entry, "presence_pct"))));
		}

		return usages;
	}

	private static object? Field(IDictionary<string, object> entry, string name) =>
		entry.TryGetValue(name, out var value) ? value : null;
}
