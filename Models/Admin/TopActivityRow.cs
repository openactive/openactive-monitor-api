namespace MonitorApi.Models.Admin;

/// <summary>One OpenActive activity-list concept in the national ranking by future opportunities.</summary>
public sealed class TopActivityRow
{
	/// <summary>1-based position in the national ranking. Continues across pages.</summary>
	public required int Rank { get; init; }

	/// <summary>The concept's canonical <c>prefLabel</c>, e.g. <c>Swimming</c>.</summary>
	public required string Activity { get; init; }

	/// <summary>The concept's <c>@id</c> in the OpenActive activity list.</summary>
	public required string ActivityId { get; init; }

	/// <summary>
	/// Future opportunities nationally whose published activity resolved to this concept. Equals the sum
	/// of the concept's rows on <c>/admin/activity-coverage</c> at the same <c>min_similarity</c>.
	/// </summary>
	public required long OpportunityCount { get; init; }

	/// <summary>Local authority districts with at least one of those opportunities.</summary>
	public required int DistrictCount { get; init; }
}
