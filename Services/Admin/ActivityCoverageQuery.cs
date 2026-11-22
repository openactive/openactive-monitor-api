using System.Text.Json;
using Google.Cloud.BigQuery.V2;
using MonitorApi.Models;

namespace MonitorApi.Services.Admin;

/// <summary>
/// SQL and row parsing for <c>/admin/activity-coverage</c> and its <c>summary</c> and
/// <c>top-activities</c> companions.
/// </summary>
/// <remarks>
/// Reads <c>active_opportunities_summary</c>, which is small (a few thousand rows) and already limited
/// to future opportunities by the pipeline that builds it. Rows come back <em>unaggregated</em>. Two
/// different labels on one row can resolve to the same vocabulary concept, and only C# knows that, so
/// summing per label in SQL would double count. See <see cref="ActivityCoverage.Build"/>.
/// </remarks>
internal static class ActivityCoverageQuery
{
	/// <summary>
	/// Every activity row that carries at least one label, with the location filter applied when one is
	/// given. The district, region and country conditions are OR'd into one clause, as on the analytics
	/// API. With <paramref name="includeUnlabelled"/>, rows with no label come back too, with an empty
	/// label list; the summary needs them for its totals.
	/// </summary>
	/// <remarks>
	/// <c>opportunity_count_narrow</c> is the figure the analytics <c>/opportunities</c> endpoint
	/// exposes. On activity rows it always equals <c>opportunity_count</c>, because only Slot rows
	/// differ. Labels come back as a JSON string rather than a BigQuery <c>ARRAY</c>, so parsing does
	/// not depend on how the client library represents repeated fields.
	/// </remarks>
	public static string Sql(
		string summaryTable,
		bool filterDistrict,
		bool filterRegion,
		bool filterCountry,
		bool includeUnlabelled = false)
	{
		var location = new List<string>();
		if (filterDistrict)
		{
			location.Add("district_code IN UNNEST(@districts)");
		}
		if (filterRegion)
		{
			location.Add("region_code IN UNNEST(@regions)");
		}
		if (filterCountry)
		{
			location.Add("country_code IN UNNEST(@countries)");
		}

		var locationClause = location.Count > 0 ? $"AND ({string.Join(" OR ", location)})" : "";
		var labelledClause = includeUnlabelled ? "" : "AND ARRAY_LENGTH(JSON_EXTRACT_ARRAY(activity_or_facility)) > 0";

		return $"""
			SELECT
			  district_code,
			  district_name,
			  region_code,
			  region_name,
			  country_code,
			  country_name,
			  opportunity_count_narrow AS opportunity_count,
			  TO_JSON_STRING(ARRAY(
			    SELECT DISTINCT TRIM(JSON_VALUE(a))
			    FROM UNNEST(JSON_EXTRACT_ARRAY(activity_or_facility)) AS a
			    WHERE JSON_VALUE(a) IS NOT NULL AND TRIM(JSON_VALUE(a)) != ''
			  )) AS labels
			FROM {summaryTable}
			WHERE is_activity = TRUE
			  AND district_code IS NOT NULL
			  {labelledClause}
			  {locationClause}
			""";
	}

	/// <summary>
	/// The location filters, as array parameters. Only the parameters the SQL actually references are
	/// bound.
	/// </summary>
	public static IReadOnlyList<BigQueryParameter> Parameters(
		IReadOnlyCollection<string> districts,
		IReadOnlyCollection<string> regions,
		IReadOnlyCollection<string> countries)
	{
		var parameters = new List<BigQueryParameter>();

		if (districts.Count > 0)
		{
			parameters.Add(StringArray("districts", districts));
		}
		if (regions.Count > 0)
		{
			parameters.Add(StringArray("regions", regions));
		}
		if (countries.Count > 0)
		{
			parameters.Add(StringArray("countries", countries));
		}

		return parameters;
	}

	/// <summary>
	/// Reads one summary row. <c>district_code</c> is guaranteed by the SQL. Everything else goes
	/// through <c>GetValueOrDefault</c>, since a NULL column is absent from the row dictionary.
	/// </summary>
	public static ActivityCoverageSourceRow Parse(Dictionary<string, object> row) =>
		new(
			(string)row["district_code"],
			row.GetValueOrDefault("district_name") as string,
			row.GetValueOrDefault("region_code") as string,
			row.GetValueOrDefault("region_name") as string,
			row.GetValueOrDefault("country_code") as string,
			row.GetValueOrDefault("country_name") as string,
			BigQueryValueParser.AsLong(row.GetValueOrDefault("opportunity_count")) ?? 0,
			ParseLabels(row.GetValueOrDefault("labels") as string));

	private static IReadOnlyList<string> ParseLabels(string? json) =>
		string.IsNullOrWhiteSpace(json)
			? []
			: JsonSerializer.Deserialize<List<string?>>(json)?.OfType<string>().ToList() ?? [];

	private static BigQueryParameter StringArray(string name, IEnumerable<string> values) =>
		new(name, BigQueryDbType.Array, values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal).ToList())
		{
			ArrayElementType = BigQueryDbType.String,
		};
}
