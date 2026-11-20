namespace MonitorApi.Models.Admin;

/// <summary>
/// Future opportunities for one OpenActive activity-list concept in one local authority district.
/// </summary>
/// <remarks>
/// Rows are sparse: a district with no matching opportunities for an activity has no row rather than
/// a row saying zero. The dashboard fills the gaps against its own list of districts.
/// </remarks>
public sealed class ActivityCoverageRow
{
	/// <summary>ONS local authority district code, e.g. <c>E08000003</c>.</summary>
	public required string DistrictCode { get; init; }

	/// <summary>District name, e.g. <c>Manchester</c>.</summary>
	public required string? DistrictName { get; init; }

	/// <summary>ONS region code. <c>null</c> outside England, where there are no regions.</summary>
	public required string? RegionCode { get; init; }

	/// <summary>Region name. <c>null</c> outside England.</summary>
	public required string? RegionName { get; init; }

	/// <summary>ONS country code, e.g. <c>E92000001</c>.</summary>
	public required string? CountryCode { get; init; }

	/// <summary>Country name, e.g. <c>England</c>.</summary>
	public required string? CountryName { get; init; }

	/// <summary>
	/// The concept's canonical <c>prefLabel</c>, e.g. <c>Swimming</c>. Never the label as published:
	/// "Lane Swimming", "public swimming" and "Swimming" all arrive here as <c>Swimming</c>.
	/// </summary>
	public required string Activity { get; init; }

	/// <summary>The concept's <c>@id</c> in the OpenActive activity list. A stable key for the dashboard.</summary>
	public required string ActivityId { get; init; }

	/// <summary>
	/// Future opportunities in the district whose published activity resolved to this concept. Always
	/// greater than zero.
	/// </summary>
	public required long OpportunityCount { get; init; }
}
