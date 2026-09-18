namespace MonitorApi.Models.Admin;

/// <summary>
/// One Active Places site paired with an OpenActive venue that covers it, as the admin dashboard sees
/// it — a row of the published <c>site_oa_mapping.csv</c>.
/// </summary>
/// <remarks>
/// A <em>pair</em>, not a site: a site reached by two publishers appears once per venue, and a venue
/// near two sites likewise. Count distinct <c>site_id</c> values, not rows, to reproduce the coverage
/// figures — or read them from <c>/admin/active-places-coverage</c>, which is where they are published.
///
/// Not an incident and not a monitor row: nothing here is detected, opened or tracked, so it carries no
/// <c>first_detected</c>, <c>days_open</c>, <c>consecutive_days</c>, <c>past_threshold</c>,
/// <c>status</c> or <c>trend</c> — those fields are absent rather than null, as on
/// <see cref="FeedQualityRow"/>.
///
/// It also carries no <c>publisher_id</c>. Publisher identity elsewhere on this surface is a slug over
/// a <c>feeds</c> row; the names here come from the analysis job's own join and a row can hold several
/// of them, so minting slugs from them would invent identifiers the rest of the API cannot resolve.
///
/// The published file has columns this does not: the geography and classification duplicates
/// (<c>region_code</c>, <c>region_name</c>, <c>management_type_group</c>), the internal
/// <c>venue_id</c>, the unresolvable <c>ap_facility_types</c> codes, the venue point and dataset counts
/// (<c>oa_point_count</c>, <c>oa_dataset_count</c>) and <c>is_primary_for_site</c>.
/// </remarks>
public sealed class ActivePlacesSiteMappingRow
{
	/// <summary>Active Places site identifier. Repeats down the page: one row per venue matched to it.</summary>
	public required string SiteId { get; init; }

	/// <summary>Site name as Active Places publishes it — upper case, e.g. <c>HARPENDEN LEISURE CENTRE</c>.</summary>
	public required string? SiteName { get; init; }

	/// <summary>The site's postcode.</summary>
	public required string? Postcode { get; init; }

	/// <summary>ONS code of the site's local authority, e.g. <c>E07000223</c>. The <c>local_authority</c> filter matches this.</summary>
	public required string? LocalAuthorityCode { get; init; }

	/// <summary>Name of that local authority, e.g. <c>Adur</c>.</summary>
	public required string? LocalAuthorityName { get; init; }

	/// <summary>Active Places ownership group: <c>Local Authority</c>, <c>Commercial</c>, <c>Education</c>, <c>Sports Club</c>, <c>Community Organisation</c>, <c>Others</c> or <c>Not Known</c>.</summary>
	public required string? OwnershipTypeGroup { get; init; }

	/// <summary>Latitude of the site, as recorded by Active Places. WGS 84.</summary>
	public required double? SiteLat { get; init; }

	/// <summary>Longitude of the site. WGS 84.</summary>
	public required double? SiteLng { get; init; }

	/// <summary>Facilities the site holds, per Active Places. Not the number of matched venues.</summary>
	public required int? ApFacilityCount { get; init; }

	/// <summary>
	/// Names the OpenActive venue is published under — one per clustered point that carried a name, so
	/// often empty and occasionally several. A name may contain newlines: some publishers put the whole
	/// address in the field.
	/// </summary>
	public required IReadOnlyList<string> OaLocationNames { get; init; }

	/// <summary>Latitude of the OpenActive venue: the centre of its 50m point cluster. WGS 84.</summary>
	public required double? OaLat { get; init; }

	/// <summary>Longitude of the OpenActive venue. WGS 84.</summary>
	public required double? OaLng { get; init; }

	/// <summary>Datasets publishing at that venue. Several when more than one publisher lists it.</summary>
	public required IReadOnlyList<string> OaDatasetUrls { get; init; }

	/// <summary>Publishers of those datasets, aligned to <c>oa_dataset_urls</c> in count, not in order.</summary>
	public required IReadOnlyList<string> OaPublisherNames { get; init; }

	/// <summary>
	/// Postcodes the venue's points carry — declared by the feed, or recovered by snapping a centroid
	/// point back to its postcode. Empty when no point carried one; more than one when the same postcode
	/// is published in different spacings.
	/// </summary>
	public required IReadOnlyList<string> OaPostalCodes { get; init; }

	/// <summary>Opportunity kinds published at the venue, e.g. <c>ScheduledSession</c>, <c>FacilityUse</c>. <c>Slot</c> is excluded from the analysis.</summary>
	public required IReadOnlyList<string> OaKinds { get; init; }

	/// <summary>Opportunity items published at the venue, across every dataset that lists it.</summary>
	public required long? OaOpportunityCount { get; init; }

	/// <summary>
	/// The raw <c>location</c> JSON of each point in the venue's cluster, byte for byte as published.
	/// The key back to the data: <c>WHERE TO_JSON_STRING(location) = '&lt;value&gt;'</c> against
	/// <c>opportunities</c> returns that point's items.
	/// </summary>
	public required IReadOnlyList<string> OaLocationJson { get; init; }

	/// <summary>
	/// Distance between site and venue in metres, measured in EPSG:27700. May exceed the 200m buffer on
	/// the <c>postcode</c> and <c>name</c> channels, which are capped at 1000m and 500m.
	/// </summary>
	public required double? DistanceMetres { get; init; }

	/// <summary>
	/// The channel that matched, weakest evidence last: <c>spatial_and_postcode</c>, <c>spatial</c>,
	/// <c>spatial_centroid_only</c>, <c>postcode</c>, <c>name</c>. See the endpoint documentation for
	/// what each one asserts.
	/// </summary>
	public required string? MatchMethod { get; init; }

	/// <summary>
	/// Name agreement between site and venue, 0–1, on a <c>name</c> match; <c>null</c> on every other
	/// channel, where names were never compared. The channel's threshold is 0.8.
	/// </summary>
	public required double? NameSimilarity { get; init; }

	/// <summary>Whether the venue is inside the 200m spatial buffer.</summary>
	public required bool SpatialMatch { get; init; }

	/// <summary>Whether site and venue share a postcode.</summary>
	public required bool PostcodeMatch { get; init; }

	/// <summary>
	/// Whether this is the strongest pair for the <em>venue</em>, ranked on channel then distance. Filter
	/// on it to get one row per venue.
	/// </summary>
	public required bool IsPrimaryForVenue { get; init; }

	/// <summary>
	/// Whether each side is the other's strongest pair. The most confident rows in the file, and the
	/// ones to start from when checking matches by hand.
	/// </summary>
	public required bool IsMutualBest { get; init; }
}
