using System.Text.Json;

namespace MonitorApi.Models;

public class OpportunityRecord
{
	public string? PublisherName { get; init; }
	public string? FeedId { get; init; }
	public string? Id { get; init; }
	public string? Kind { get; init; }
	public DateTime? StartDate { get; init; }
	public DateTime? EndDate { get; init; }
	public DateOnly? LastUpdated { get; init; }
	public JsonElement? Location { get; init; }
	public string? DistrictName { get; init; }
	public string? DistrictCode { get; init; }
	public string? RegionName { get; init; }
	public string? RegionCode { get; init; }
	public string? CountryName { get; init; }
	public string? CountryCode { get; init; }
	public JsonElement? Activity { get; init; }
	public JsonElement? Facility { get; init; }
	public JsonElement? JsonData { get; init; }
	public string? AgeRange { get; init; }
	public string? Level { get; init; }
	public JsonElement? AccessibilitySupport { get; init; }
	public string? GenderRestriction { get; init; }
	public string? OrganizationName { get; init; }

	public static OpportunityRecord FromBigQueryRow(Dictionary<string, object> row) => new()
	{
		PublisherName = row.GetValueOrDefault("publisher_name") as string,
		FeedId = row.GetValueOrDefault("feed_id") as string,
		Id = row.GetValueOrDefault("id") as string,
		Kind = row.GetValueOrDefault("kind") as string,
		StartDate = row.GetValueOrDefault("startDate") as DateTime?,
		EndDate = row.GetValueOrDefault("endDate") as DateTime?,
		LastUpdated = row.TryGetValue("last_updated", out var lu) && lu is DateTime d ? DateOnly.FromDateTime(d) : null,
		Location = BigQueryValueParser.ParseJson(row.GetValueOrDefault("location")),
		DistrictName = row.GetValueOrDefault("district_name") as string,
		DistrictCode = row.GetValueOrDefault("district_code") as string,
		RegionName = row.GetValueOrDefault("region_name") as string,
		RegionCode = row.GetValueOrDefault("region_code") as string,
		CountryName = row.GetValueOrDefault("country_name") as string,
		CountryCode = row.GetValueOrDefault("country_code") as string,
		Activity = BigQueryValueParser.ParseJson(row.GetValueOrDefault("activity")),
		Facility = BigQueryValueParser.ParseJson(row.GetValueOrDefault("facility")),
		JsonData = BigQueryValueParser.ParseJson(row.GetValueOrDefault("json_data")),
		AgeRange = row.GetValueOrDefault("ageRange") as string,
		Level = row.GetValueOrDefault("level") as string,
		AccessibilitySupport = BigQueryValueParser.ParseJson(row.GetValueOrDefault("accessibilitySupport")),
		GenderRestriction = row.GetValueOrDefault("genderRestriction") as string,
		OrganizationName = row.GetValueOrDefault("organization_name") as string,
	};
}
