namespace MonitorApi.Models.Admin;

/// <summary>
/// One feed's schema drift as the admin dashboard sees it — a row of <c>custom_properties</c> for a
/// feed publishing at least one property outside the OpenActive vocabulary, with the two display
/// fields the rest of the admin surface uses.
/// </summary>
/// <remarks>
/// Not an incident: <c>custom_properties</c> is a snapshot with no history, so this carries no
/// <c>monitor_id</c>, <c>first_detected</c>, <c>days_open</c>, <c>past_threshold</c> or <c>trend</c> —
/// absent rather than null, as on <see cref="FeedQualityRow"/>.
///
/// Every measured field is nullable because every column but the two identifiers is. A <c>null</c>
/// means the assessment did not report the value, never that it reported zero.
/// </remarks>
public sealed class FeedCustomPropertiesRow
{
	/// <summary>
	/// Identifier of the feed, matching <c>feeds.id</c>. Unique only together with <see cref="DatasetUrl"/>:
	/// the same feed served under two dataset URLs appears once for each.
	/// </summary>
	public required string FeedId { get; init; }

	/// <summary>URL the feed is published at, or <c>null</c> when the assessment did not record one.</summary>
	public required string? FeedUrl { get; init; }

	/// <summary>Opportunity kind the feed publishes, e.g. <c>SessionSeries</c> or <c>FacilityUse</c>.</summary>
	public required string? FeedType { get; init; }

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

	/// <summary>Publisher name as stored with the assessment, or empty when it recorded none.</summary>
	public required string PublisherName { get; init; }

	/// <summary>Opportunity items sampled for the assessment — the population every <c>presence_pct</c> is drawn from.</summary>
	public required long? SampledItems { get; init; }

	/// <summary>Distinct custom property names the feed uses. Always greater than zero on this endpoint.</summary>
	public required long? NumCustomProperties { get; init; }

	/// <summary>
	/// Distinct (entity type, property) pairs the feed uses — the length of <see cref="CustomProperties"/>.
	/// At least <see cref="NumCustomProperties"/>, and higher where one property appears on several types.
	/// </summary>
	public required long? NumCustomPropertyUsages { get; init; }

	/// <summary>One entry per (property, entity type), most widely present first.</summary>
	public required IReadOnlyList<FeedCustomProperty> CustomProperties { get; init; }
}

/// <summary>One custom property as used on one entity type within the feed.</summary>
public sealed class FeedCustomProperty
{
	/// <summary>Property key as published, e.g. <c>beta:formattedDescription</c>.</summary>
	public required string? Property { get; init; }

	/// <summary>Prefix before the <c>:</c>, e.g. <c>beta</c>. <c>null</c> when the key is unprefixed or a full URI.</summary>
	public required string? Namespace { get; init; }

	/// <summary><c>@type</c> of the object carrying the property, e.g. <c>SessionSeries</c> or <c>Place</c>.</summary>
	public required string? EntityType { get; init; }

	/// <summary>Percentage of the sampled instances of <see cref="EntityType"/> carrying the property, 0–100.</summary>
	public required double? PresencePct { get; init; }
}
