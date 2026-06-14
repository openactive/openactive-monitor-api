using System.Text.Json;

namespace MonitorApi.Models;

/// <summary>Feed quality row returned by the <c>/feed-quality</c> endpoint.</summary>
public class FeedQualityRecord
{
	public string? DatasetName { get; init; }
	public string? DatasetUrl { get; init; }
	public string? FeedType { get; init; }
	public string? FeedUrl { get; init; }
	public string? Status { get; init; }
	public JsonElement? Warnings { get; init; }
	public JsonElement? Errors { get; init; }
	public double? LocationCompleteness { get; init; }
	public double? StartDateCompleteness { get; init; }
	public double? EndDateCompleteness { get; init; }
	public double? ActivitiesCompleteness { get; init; }
	public double? FacilitiesCompleteness { get; init; }
	public long? NumFutureOpportunityItems { get; init; }
	public string? FeedVersion { get; init; }
	public DateTime? LastAssessed { get; init; }
	public double? AgeRangeCompleteness { get; init; }
	public double? LevelCompleteness { get; init; }
	public double? AccessibilitySupportCompleteness { get; init; }
	public double? GenderRestrictionCompleteness { get; init; }

	public static FeedQualityRecord FromBigQueryRow(Dictionary<string, object> row) => new()
	{
		DatasetName = row.GetValueOrDefault("dataset_name") as string,
		DatasetUrl = row.GetValueOrDefault("dataset_url") as string,
		FeedType = row.GetValueOrDefault("feed_type") as string,
		FeedUrl = row.GetValueOrDefault("feed_url") as string,
		Status = row.GetValueOrDefault("status") as string,
		Warnings = BigQueryValueParser.ParseJson(row.GetValueOrDefault("warnings")),
		Errors = BigQueryValueParser.ParseJson(row.GetValueOrDefault("errors")),
		LocationCompleteness = BigQueryValueParser.AsDouble(row.GetValueOrDefault("location_completeness")),
		StartDateCompleteness = BigQueryValueParser.AsDouble(row.GetValueOrDefault("start_date_completeness")),
		EndDateCompleteness = BigQueryValueParser.AsDouble(row.GetValueOrDefault("end_date_completeness")),
		ActivitiesCompleteness = BigQueryValueParser.AsDouble(row.GetValueOrDefault("activities_completeness")),
		FacilitiesCompleteness = BigQueryValueParser.AsDouble(row.GetValueOrDefault("facilities_completeness")),
		NumFutureOpportunityItems = BigQueryValueParser.AsLong(row.GetValueOrDefault("num_future_opportunity_items")),
		FeedVersion = row.GetValueOrDefault("feed_version") as string,
		LastAssessed = row.GetValueOrDefault("last_assessed") as DateTime?,
		AgeRangeCompleteness = BigQueryValueParser.AsDouble(row.GetValueOrDefault("age_range_completeness")),
		LevelCompleteness = BigQueryValueParser.AsDouble(row.GetValueOrDefault("level_completeness")),
		AccessibilitySupportCompleteness = BigQueryValueParser.AsDouble(row.GetValueOrDefault("accessibility_support_completeness")),
		GenderRestrictionCompleteness = BigQueryValueParser.AsDouble(row.GetValueOrDefault("gender_restriction_completeness")),
	};
}
