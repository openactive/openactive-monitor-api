using System.Text.Json;

namespace MonitorApi.Models.Admin;

/// <summary>
/// One feed's quality assessment as the admin dashboard sees it — a row of <c>feed_quality</c>, with
/// the publisher joined in and the two display fields the rest of the admin surface uses.
/// </summary>
/// <remarks>
/// Not an incident: nothing here is detected, opened or tracked. <c>feed_quality</c> holds current
/// state only, so this carries no <c>first_detected</c>, <c>days_open</c>, <c>consecutive_days</c>,
/// <c>past_threshold</c> or <c>trend</c> — those fields are absent rather than null, as on
/// <see cref="OrphanedChildrenIncident"/>, and re-adding them later as nullable would commit the API
/// to narrowing them, which is a breaking change.
///
/// Every measured field is nullable because every column but the two identifiers is. A <c>null</c>
/// means the assessment did not report the value, never that it reported zero.
/// </remarks>
public sealed class FeedQualityRow
{
	/// <summary>Identifier of the feed, matching <c>feeds.id</c> and the <c>feed_id</c> on feed-scoped incidents.</summary>
	public required string FeedId { get; init; }

	/// <summary>URL the feed is published at, or <c>null</c> when the assessment did not record one.</summary>
	public required string? FeedUrl { get; init; }

	/// <summary>Opportunity kind the feed publishes, e.g. <c>SessionSeries</c> or <c>FacilityUse</c>.</summary>
	public required string? FeedType { get; init; }

	/// <summary>Detected OpenActive specification version, e.g. <c>V1.1</c>, <c>V2.0</c>, <c>V2.1</c>.</summary>
	public required string? FeedVersion { get; init; }

	/// <summary>
	/// Whether the feed publishes on a regular schedule. <c>null</c> when the assessment did not
	/// determine it — not the same as <c>false</c>.
	/// </summary>
	public required bool? IsRegular { get; init; }

	/// <summary>The dataset the feed belongs to. The join key to <c>feeds</c> and to every dataset-scoped monitor.</summary>
	public required string DatasetUrl { get; init; }

	/// <summary>
	/// Display name for the dataset: its stored <c>dataset_name</c>, falling back to the host of
	/// <see cref="DatasetUrl"/> and then to the URL itself, so it is never empty.
	/// </summary>
	public required string DatasetName { get; init; }

	/// <summary>Publisher slug, e.g. <c>pub_freedom-leisure</c>. Derived from the name, not stored; <c>pub_unknown</c> when unnamed.</summary>
	public required string PublisherId { get; init; }

	/// <summary>Publisher name from <c>feeds</c>, or empty when the dataset has no <c>feeds</c> row.</summary>
	public required string PublisherName { get; init; }

	/// <summary>Assessment outcome: <c>OK</c>, <c>WARNING</c> or <c>ERROR</c>. <c>null</c> when not assessed.</summary>
	public required string? Status { get; init; }

	/// <summary>Quality grade: <c>None</c>, <c>Bronze</c>, <c>Silver</c> or <c>Gold</c>.</summary>
	public required string? Grade { get; init; }

	/// <summary>Normalised quality score from 0 to 100, based on required/recommended/optional property presence.</summary>
	public required double? Score { get; init; }

	/// <summary>Opportunity items the feed currently offers with a future start date.</summary>
	public required long? NumFutureOpportunityItems { get; init; }

	/// <summary>Percentage of the feed's items carrying each property, from 0 to 100.</summary>
	public required FeedQualityRowCompleteness Completeness { get; init; }

	/// <summary>
	/// Warnings the assessment raised, passed through exactly as stored. Shape is the assessor's, not
	/// this API's, and is not interpreted or counted here.
	/// </summary>
	public required JsonElement? Warnings { get; init; }

	/// <summary>Errors the assessment raised, passed through exactly as stored. See <see cref="Warnings"/>.</summary>
	public required JsonElement? Errors { get; init; }

	/// <summary>
	/// Required fields the feed omits, keyed by opportunity kind, passed through exactly as stored.
	/// See <see cref="Warnings"/>.
	/// </summary>
	public required JsonElement? MissingRequiredFields { get; init; }

	/// <summary>When this feed was assessed. UTC. Not necessarily <c>meta.snapshot_date</c>: that is the latest across all feeds.</summary>
	public required DateTime? LastAssessed { get; init; }
}

/// <summary>
/// One feed's completeness figures, each the percentage of its items carrying that property, from 0
/// to 100. <c>null</c> where the assessment did not measure it — not the same as <c>0</c>, which is a
/// real measurement saying no item carries it.
/// </summary>
public sealed class FeedQualityRowCompleteness
{
	public required double? Location { get; init; }

	public required double? StartDate { get; init; }

	public required double? EndDate { get; init; }

	public required double? Activities { get; init; }

	public required double? Facilities { get; init; }

	public required double? AgeRange { get; init; }

	public required double? Level { get; init; }

	public required double? AccessibilitySupport { get; init; }

	public required double? GenderRestriction { get; init; }
}
