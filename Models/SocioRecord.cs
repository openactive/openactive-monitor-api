using System.Text.Json.Serialization;

namespace MonitorApi.Models;

/// <summary>
/// Socio-economic context for an area, keyed by ONS geography code (<c>area_code</c>).
/// Population is available for all areas; deprivation (IMD 2025) and Active Lives metrics are England-only.
/// </summary>
public class SocioRecord
{
	[JsonPropertyName("area_code")]
	public required string AreaCode { get; init; }

	[JsonPropertyName("area_name")]
	public string? AreaName { get; init; }

	[JsonPropertyName("total_population")]
	public long? TotalPopulation { get; init; }

	[JsonPropertyName("imd25_average_score")]
	public double? Imd25AverageScore { get; init; }

	[JsonPropertyName("imd25_rank_of_average_score")]
	public double? Imd25RankOfAverageScore { get; init; }

	[JsonPropertyName("imd25_pct_lsoas_in_most_deprived_10pct")]
	public double? Imd25PctLsoasInMostDeprived10pct { get; init; }

	[JsonPropertyName("imd25_extent")]
	public double? Imd25Extent { get; init; }

	[JsonPropertyName("imd25_local_concentration")]
	public double? Imd25LocalConcentration { get; init; }

	[JsonPropertyName("als_respondents")]
	public double? AlsRespondents { get; init; }

	[JsonPropertyName("als_active_pop")]
	public double? AlsActivePop { get; init; }

	[JsonPropertyName("als_fairly_active_pop")]
	public double? AlsFairlyActivePop { get; init; }

	[JsonPropertyName("als_inactive_pop")]
	public double? AlsInactivePop { get; init; }

	[JsonPropertyName("als_survey_adult_population")]
	public double? AlsSurveyAdultPopulation { get; init; }

	[JsonPropertyName("als_active_rate")]
	public double? AlsActiveRate { get; init; }

	[JsonPropertyName("als_fairly_active_rate")]
	public double? AlsFairlyActiveRate { get; init; }

	[JsonPropertyName("als_inactive_rate")]
	public double? AlsInactiveRate { get; init; }

	[JsonPropertyName("als_active_rate_change_12m")]
	public double? AlsActiveRateChange12m { get; init; }

	[JsonPropertyName("als_inactive_rate_change_12m")]
	public double? AlsInactiveRateChange12m { get; init; }

	public static SocioRecord FromBigQueryRow(Dictionary<string, object> row) => new()
	{
		AreaCode = (string)row["area_code"],
		AreaName = row.GetValueOrDefault("area_name") as string,
		TotalPopulation = BigQueryValueParser.AsLong(row.GetValueOrDefault("total_population")),
		Imd25AverageScore = BigQueryValueParser.AsDouble(row.GetValueOrDefault("imd25_average_score")),
		Imd25RankOfAverageScore = BigQueryValueParser.AsDouble(row.GetValueOrDefault("imd25_rank_of_average_score")),
		Imd25PctLsoasInMostDeprived10pct = BigQueryValueParser.AsDouble(row.GetValueOrDefault("imd25_pct_lsoas_in_most_deprived_10pct")),
		Imd25Extent = BigQueryValueParser.AsDouble(row.GetValueOrDefault("imd25_extent")),
		Imd25LocalConcentration = BigQueryValueParser.AsDouble(row.GetValueOrDefault("imd25_local_concentration")),
		AlsRespondents = BigQueryValueParser.AsDouble(row.GetValueOrDefault("als_respondents")),
		AlsActivePop = BigQueryValueParser.AsDouble(row.GetValueOrDefault("als_active_pop")),
		AlsFairlyActivePop = BigQueryValueParser.AsDouble(row.GetValueOrDefault("als_fairly_active_pop")),
		AlsInactivePop = BigQueryValueParser.AsDouble(row.GetValueOrDefault("als_inactive_pop")),
		AlsSurveyAdultPopulation = BigQueryValueParser.AsDouble(row.GetValueOrDefault("als_survey_adult_population")),
		AlsActiveRate = BigQueryValueParser.AsDouble(row.GetValueOrDefault("als_active_rate")),
		AlsFairlyActiveRate = BigQueryValueParser.AsDouble(row.GetValueOrDefault("als_fairly_active_rate")),
		AlsInactiveRate = BigQueryValueParser.AsDouble(row.GetValueOrDefault("als_inactive_rate")),
		AlsActiveRateChange12m = BigQueryValueParser.AsDouble(row.GetValueOrDefault("als_active_rate_change_12m")),
		AlsInactiveRateChange12m = BigQueryValueParser.AsDouble(row.GetValueOrDefault("als_inactive_rate_change_12m")),
	};
}
