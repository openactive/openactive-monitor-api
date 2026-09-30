namespace MonitorApi.Models.Admin;

/// <summary>
/// Estate-level schema-drift figures: how many feeds publish custom properties, how many distinct
/// properties there are, and where they come from.
/// </summary>
/// <remarks>
/// Describes <b>every row the request's filters matched</b>, not the page returned, so it does not
/// change as the caller walks the pages. It is reduced from the very rows being paged, so it can
/// never disagree with them.
///
/// <see cref="FeedsAssessed"/> and <see cref="DatasetsAssessed"/> are the one exception to "describes
/// the rows": they count every assessed feed the filters matched, including those with no custom
/// properties that the endpoint does not return, so the share of the estate drifting can be read.
/// </remarks>
public sealed class CustomPropertiesSummary
{
	/// <summary>Feeds assessed, with or without custom properties.</summary>
	public required int FeedsAssessed { get; init; }

	/// <summary>Distinct datasets those assessed feeds belong to.</summary>
	public required int DatasetsAssessed { get; init; }

	/// <summary>Feeds using at least one custom property — the number of rows behind this summary, equal to <c>meta.total</c>.</summary>
	public required int FeedsWithCustomProperties { get; init; }

	/// <summary>Distinct datasets with at least one such feed.</summary>
	public required int DatasetsWithCustomProperties { get; init; }

	/// <summary>Distinct named publishers behind those datasets. A feed with no publisher name counts towards none.</summary>
	public required int PublishersWithCustomProperties { get; init; }

	/// <summary>
	/// <see cref="FeedsWithCustomProperties"/> as a fraction of <see cref="FeedsAssessed"/>, 0–1.
	/// <c>null</c> when nothing was assessed.
	/// </summary>
	public required double? FeedShare { get; init; }

	/// <summary>Distinct custom property keys across those feeds, compared exactly (case-sensitive).</summary>
	public required int DistinctCustomProperties { get; init; }

	/// <summary>
	/// Sum of every feed's <c>num_custom_property_usages</c>: (feed, entity type, property) triples.
	/// A feed reporting no figure contributes nothing.
	/// </summary>
	public required long TotalCustomPropertyUsages { get; init; }

	/// <summary>Properties, feeds and datasets per namespace prefix, e.g. <c>beta</c>.</summary>
	public required IReadOnlyList<CustomPropertyNamespaceBreakdown> NamespaceBreakdown { get; init; }

	/// <summary>Properties, feeds and datasets per entity type the properties were found on.</summary>
	public required IReadOnlyList<CustomPropertyEntityTypeBreakdown> EntityTypeBreakdown { get; init; }

	/// <summary>
	/// Every distinct custom property, most widespread first — the candidates for adoption into the
	/// vocabulary, or for a conversation with the publishers using them.
	/// </summary>
	public required IReadOnlyList<CustomPropertyBreakdown> PropertyBreakdown { get; init; }
}

/// <summary>How much custom-property use one namespace prefix accounts for.</summary>
public sealed class CustomPropertyNamespaceBreakdown
{
	/// <summary>The prefix, e.g. <c>beta</c>. <c>null</c> groups unprefixed keys and full URIs.</summary>
	public required string? Namespace { get; init; }

	/// <summary>Distinct properties in the namespace.</summary>
	public required int PropertyCount { get; init; }

	/// <summary>Feeds using at least one of them.</summary>
	public required int FeedCount { get; init; }

	/// <summary>Distinct datasets with at least one such feed.</summary>
	public required int DatasetCount { get; init; }
}

/// <summary>How much custom-property use one entity type accounts for.</summary>
public sealed class CustomPropertyEntityTypeBreakdown
{
	/// <summary>The <c>@type</c>, e.g. <c>SessionSeries</c>. <c>unknown</c> where the assessment recorded none.</summary>
	public required string EntityType { get; init; }

	/// <summary>Distinct custom properties found on the type.</summary>
	public required int PropertyCount { get; init; }

	/// <summary>Feeds using a custom property on the type.</summary>
	public required int FeedCount { get; init; }

	/// <summary>Distinct datasets with at least one such feed.</summary>
	public required int DatasetCount { get; init; }
}

/// <summary>One custom property across the estate.</summary>
public sealed class CustomPropertyBreakdown
{
	/// <summary>Property key as published, e.g. <c>beta:formattedDescription</c>.</summary>
	public required string Property { get; init; }

	/// <summary>Its namespace prefix, or <c>null</c> when unprefixed or a full URI.</summary>
	public required string? Namespace { get; init; }

	/// <summary>Entity types the property was found on, alphabetically.</summary>
	public required IReadOnlyList<string> EntityTypes { get; init; }

	/// <summary>Feeds using the property.</summary>
	public required int FeedCount { get; init; }

	/// <summary>Distinct datasets with at least one such feed.</summary>
	public required int DatasetCount { get; init; }
}
