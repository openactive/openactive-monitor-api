using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MonitorApi.Models.Admin;
using MonitorApi.Services.Admin;

namespace MonitorApi.Controllers.Admin;

/// <summary>
/// Active Places coverage for the admin dashboard: how much of Sport England's register of the built
/// sporting estate is visible in the OpenActive data, and which sites those are. Every endpoint
/// requires the admin token as the <c>token</c> query parameter.
/// </summary>
/// <remarks>
/// The one part of this API that does not read BigQuery. The coverage analysis runs in the
/// <c>openactive-monitor</c> jobs repository — it needs the Active Places export and an OS Code-Point
/// Open lookup, neither of which is in the warehouse — and publishes two files. These endpoints mirror
/// those files; they do not recompute anything, and nothing here can be derived from a SQL query.
///
/// Derives from <see cref="AdminControllerBase"/> rather than <c>MonitorControllerBase</c>, and needs
/// none of its loaders: there is no ingestion history to window and no <c>snapshot_date</c> to resolve
/// from <c>opportunity_ingestion</c>. Both endpoints take their <c>meta.snapshot_date</c> from the
/// report's own <c>run_date</c>, which is the only day either file describes.
///
/// The published files are fetched over HTTP and held until the next daily refresh, so the two
/// endpoints always answer from the same run of the analysis. Where they are fetched from is
/// configurable — see <see cref="ActivePlacesOptions"/>.
/// </remarks>
public class ActivePlacesController(
	IOptions<BigQueryOptions> bigQueryOptions,
	IOptions<ApiOptions> apiOptions,
	ActivePlacesSource source)
	: AdminControllerBase(bigQueryOptions, apiOptions)
{
	private readonly ActivePlacesSource source = source;

	/// <summary>
	/// Active Places Site Mappings
	/// </summary>
	/// <remarks>
	/// Every Active Places site the OpenActive data reaches, paired with the venue that reaches it —
	/// the published <c>site_oa_mapping.csv</c>, served as JSON. Ordered by site name, then by distance
	/// within a site.
	///
	/// The things worth knowing:
	///
	/// - **A row is a site/venue pair, not a site.** A site covered by three publishers appears three
	///	  times, and a venue near two sites appears once per site. <c>meta.total</c> counts pairs;
	///	  the distinct site count is <c>headline.sites_matched</c> on
	///	  <c>/admin/active-places-coverage</c>. Filter on <c>is_primary_for_venue</c> in the client for
	///	  one row per venue.
	/// - **Only matched sites are here.** The 20,506 Active Places sites no OpenActive venue reaches are
	///	  not in the file at all — this is the covered estate, not the register. Nor are the OpenActive
	///	  venues that match no site; those are summarised under <c>unmatched</c> on the coverage
	///	  endpoint.
	/// - **`match_method` says how strong the evidence is.** <c>spatial_and_postcode</c> (within 200m
	///	  and sharing a postcode) is the strongest; <c>spatial</c> is within 200m;
	///	  <c>spatial_centroid_only</c> is within 200m but the venue's coordinates are a postcode
	///	  centroid and the postcodes disagree, so the proximity is partly an artefact;
	///	  <c>postcode</c> shares a postcode at 200–1000m; <c>name</c> is the last resort, a name
	///	  agreement of 0.8 or better within 500m. Filter the weaker channels out with
	///	  <c>match_method</c> rather than by reading <c>distance_metres</c>.
	/// - **The multi-valued columns are arrays.** <c>oa_location_names</c>, <c>oa_dataset_urls</c>,
	///	  <c>oa_publisher_names</c>, <c>oa_postal_codes</c>, <c>oa_kinds</c> and <c>oa_location_json</c>
	///	  are pipe-joined in the CSV and split here. Any of them can be empty — a venue is a cluster of
	///	  published points, and not every point carries a name or a postcode.
	/// - **`oa_location_json` is the key back to the data.** Each entry is a point's <c>location</c>
	///	  JSON byte for byte as published, so <c>WHERE TO_JSON_STRING(location) = '&lt;value&gt;'</c>
	///	  against <c>opportunities</c> returns that point's items.
	/// - **Some columns of the CSV are not served**: <c>region_code</c>, <c>region_name</c>,
	///	  <c>management_type_group</c>, <c>ap_facility_types</c>, <c>venue_id</c>, <c>oa_point_count</c>,
	///	  <c>oa_dataset_count</c> and <c>is_primary_for_site</c>. Regional figures are on the coverage
	///	  endpoint, and the facility type codes have no published lookup to resolve them against.
	/// - **Filters combine as they do across the API.** Values within one parameter are OR'd and
	///	  different parameters are AND'd, and each accepts repeated (<c>?publisher=a&amp;publisher=b</c>)
	///	  or comma-separated (<c>?publisher=a,b</c>) values. <c>publisher</c> matches when *any* of the
	///	  row's publishers is one of the values.
	/// - **Everything is as published.** No value is recomputed, rescaled or rounded here, and a blank
	///	  cell becomes <c>null</c> or an empty array, never a zero.
	///
	/// This is not a monitor, and three things follow. It takes no date parameter of any kind — the
	/// analysis publishes one run at a time and a past run cannot be fetched. There is no sibling trend
	/// endpoint and no tile on <c>/admin/summary</c>, which lists incident monitors. And the row carries
	/// no <c>first_detected</c>, <c>days_open</c>, <c>consecutive_days</c>, <c>past_threshold</c>,
	/// <c>status</c> or <c>trend</c> — those fields are absent rather than null.
	///
	/// <c>meta.snapshot_date</c> is the analysis's <c>run_date</c>, read from the coverage report so
	/// that both endpoints date themselves identically.
	///
	/// Answers <c>502</c> when the published files cannot be fetched or read. Successful results are
	/// cached until the next daily refresh, varying by all query parameters.
	/// </remarks>
	/// <param name="page">One-based page number. Default <c>1</c>.</param>
	/// <param name="page_size">Rows per page. Default <c>500</c>, capped at <c>1000</c>.</param>
	/// <param name="site_id">One or more Active Places site identifiers, matched exactly. Accepts repeated (<c>?site_id=a&amp;site_id=b</c>) or comma-separated (<c>?site_id=a,b</c>) values.</param>
	/// <param name="local_authority_code">One or more ONS local authority codes, e.g. <c>E07000223</c>, matched ignoring case. Same repeated/comma-separated forms.</param>
	/// <param name="publisher">One or more publisher names, matched exactly. A row matches when any of its <c>oa_publisher_names</c> is any of these. Same repeated/comma-separated forms.</param>
	/// <param name="match_method">One or more match channels — <c>spatial</c>, <c>spatial_and_postcode</c>, <c>spatial_centroid_only</c>, <c>postcode</c>, <c>name</c> — matched ignoring case. Same repeated/comma-separated forms.</param>
	[HttpGet("active-places-site-mappings")]
	[ProducesResponseType(typeof(AdminPage<ActivePlacesSiteMappingRow>), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status502BadGateway)]
	public async Task<ActionResult<AdminPage<ActivePlacesSiteMappingRow>>> ActivePlacesSiteMappings(
		int page = 1,
		int page_size = DefaultPageSize,
		[FromQuery] string[]? site_id = null,
		[FromQuery] string[]? local_authority_code = null,
		[FromQuery] string[]? publisher = null,
		[FromQuery] string[]? match_method = null)
	{
		var cancellationToken = HttpContext.RequestAborted;

		ActivePlacesCoverageReport report;
		IReadOnlyList<ActivePlacesSiteMapping> mappings;
		try
		{
			// The report as well as the mapping: it carries the run date both endpoints are labelled
			// with, and the two files are published together by one run of the analysis.
			report = await source.GetCoverage(cancellationToken);
			mappings = await source.GetSiteMappings(cancellationToken);
		}
		catch (Exception e) when (IsUpstreamFailure(e))
		{
			return Unavailable(e);
		}

		var selected = ActivePlacesMappingFilter.Apply(
			mappings,
			NormaliseMultiValue(site_id),
			NormaliseMultiValue(local_authority_code),
			NormaliseMultiValue(publisher),
			NormaliseMultiValue(match_method));

		var rows = selected.Select(ToRow).ToList();

		return Ok(Paginate(rows, page, page_size, report.RunDate));
	}

	/// <summary>
	/// Active Places Coverage
	/// </summary>
	/// <remarks>
	/// The coverage analysis's summary report — the published <c>active_places_coverage.json</c>,
	/// passed through in <c>data</c> exactly as generated. Takes no parameters.
	///
	/// It answers, for the English built sporting estate: how much of Active Places appears in the
	/// OpenActive data, where the gaps are, and how much of the OpenActive data is not Active Places
	/// estate at all. <c>headline.coverage_pct</c> is the figure the dashboard leads on.
	///
	/// What the document carries, section by section:
	///
	/// - **`headline`** — sites in scope, matched, missing and the coverage percentage, plus the same
	///	  read from the OpenActive side (venues matched and unmatched).
	/// - **`source` and `parameters`** — what was analysed and the thresholds it was analysed with: the
	///	  200m spatial buffer, the 1000m postcode cap, the 500m/0.8 name channel, the 50m venue cluster.
	/// - **`channels`** — what each match channel contributed, and a per-method breakdown with median
	///	  distances.
	/// - **`distance_sensitivity`** — coverage at 25m through 1000m, with the buffer in use flagged.
	///	  Read this before quoting the headline: coverage roughly doubles between 100m and 250m.
	/// - **`coverage_by_region`, `coverage_by_local_authority`, `coverage_by_ownership`,
	///	  `coverage_by_management`, `coverage_by_facility_type`** — the same four counts cut five ways. A
	///	  site is counted once per facility type it offers, so that cut sums to more than the total.
	/// - **`publishers`** — which publishers account for the coverage, by sites covered.
	/// - **`coordinate_provenance`** — how many OpenActive points sit exactly on a postcode centroid
	///	  rather than a surveyed position, overall and per publisher. This is the main reason the
	///	  headline is a lower bound.
	/// - **`unmatched`** — the OpenActive side: venues matching no site, by district, by publisher, and
	///	  the largest of them by opportunity count.
	/// - **`data_quality`** — what was excluded from the OpenActive side and why, and the clustering
	///	  diagnostics.
	///
	/// The things worth knowing:
	///
	/// - **`data` is passed through unmodelled.** The report is generated by the analysis job and
	///	  carries its own <c>schema_version</c>; a section added upstream appears here without a release,
	///	  so read it defensively and check <c>schema_version</c> rather than assuming a key is present.
	///	  Its keys are already snake_case, like the rest of this API.
	/// - **Read the headline as a lower bound.** Active Places records one point per site while
	///	  OpenActive records the point a session happens at, and 31% of OpenActive points are postcode
	///	  centroids rather than surveyed coordinates. Both push genuine matches outside the 200m buffer.
	/// - **A high "absent from Active Places" figure is not a data-quality problem.** Parks, halls,
	///	  streets and outdoor meeting points host OpenActive opportunities and are outside the Active
	///	  Places remit by construction.
	/// - **England only.** Active Places is an England-only register, so the OpenActive side is
	///	  restricted to England to compare like with like; <c>data_quality</c> accounts for every
	///	  excluded point.
	///
	/// This is not a monitor: there is no <c>as_of</c>, no trend endpoint and no tile on
	/// <c>/admin/summary</c>. One run of the analysis is published at a time, and a past run cannot be
	/// fetched.
	///
	/// <c>meta</c> is the standard envelope with the paging fields fixed at one row on one page.
	/// <c>meta.snapshot_date</c> is the report's <c>run_date</c> and <c>meta.generated_at</c> its
	/// <c>generated_at</c> — when the analysis ran, not when this response was assembled, which is the
	/// one endpoint on this surface where those differ.
	///
	/// Answers <c>502</c> when the published report cannot be fetched or read. Successful results are
	/// cached until the next daily refresh.
	/// </remarks>
	[HttpGet("active-places-coverage")]
	[ProducesResponseType(typeof(AdminDocument<JsonElement>), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status502BadGateway)]
	public async Task<ActionResult<AdminDocument<JsonElement>>> ActivePlacesCoverage()
	{
		ActivePlacesCoverageReport report;
		try
		{
			report = await source.GetCoverage(HttpContext.RequestAborted);
		}
		catch (Exception e) when (IsUpstreamFailure(e))
		{
			return Unavailable(e);
		}

		return Ok(Document(report.Document, report.RunDate, report.GeneratedAt));
	}

	#region Utilities

	/// <summary>
	/// Whether an exception is the published report being unreachable or unreadable, as opposed to a
	/// fault in this API. Only these become a <c>502</c>; anything else is left to propagate.
	/// </summary>
	/// <remarks>
	/// <see cref="TaskCanceledException"/> covers the fetch timing out, which
	/// <see cref="HttpClient"/> raises rather than a timeout type of its own. A caller who has gone away
	/// cancels the request too, but the response is discarded in that case whatever it says.
	/// </remarks>
	private static bool IsUpstreamFailure(Exception e) =>
		e is HttpRequestException or TaskCanceledException or InvalidDataException;

	/// <summary>
	/// The failure the dashboard sees when the upstream report cannot be served: a <c>502</c> carrying
	/// the same <c>message</c> shape as the auth failure, so one error handler covers both.
	/// </summary>
	/// <remarks>
	/// A gateway failure and not a <c>500</c>: nothing is wrong with this API, and the distinction tells
	/// the dashboard the answer may well be there on a retry. The exception message is included because
	/// the surface is token-gated and the URL it names is public configuration, not a secret.
	///
	/// Not cached: the output cache stores <c>200</c> responses only, so a failure now does not deny the
	/// dashboard the report for the rest of the day.
	/// </remarks>
	private ObjectResult Unavailable(Exception e) =>
		new(new { message = "The Active Places coverage report is currently unavailable: " + e.Message })
		{
			StatusCode = StatusCodes.Status502BadGateway,
		};

	/// <summary>Hydrates one published pair into the dashboard payload.</summary>
	private static ActivePlacesSiteMappingRow ToRow(ActivePlacesSiteMapping mapping) =>
		new()
		{
			SiteId = mapping.SiteId,
			SiteName = mapping.SiteName,
			Postcode = mapping.Postcode,
			LocalAuthorityCode = mapping.LocalAuthorityCode,
			LocalAuthorityName = mapping.LocalAuthorityName,
			OwnershipTypeGroup = mapping.OwnershipTypeGroup,
			SiteLat = mapping.SiteLat,
			SiteLng = mapping.SiteLng,
			ApFacilityCount = mapping.ApFacilityCount,
			OaLocationNames = mapping.OaLocationNames,
			OaLat = mapping.OaLat,
			OaLng = mapping.OaLng,
			OaDatasetUrls = mapping.OaDatasetUrls,
			OaPublisherNames = mapping.OaPublisherNames,
			OaPostalCodes = mapping.OaPostalCodes,
			OaKinds = mapping.OaKinds,
			OaOpportunityCount = mapping.OaOpportunityCount,
			OaLocationJson = mapping.OaLocationJson,
			DistanceMetres = mapping.DistanceMetres,
			MatchMethod = mapping.MatchMethod,
			NameSimilarity = mapping.NameSimilarity,
			SpatialMatch = mapping.SpatialMatch,
			PostcodeMatch = mapping.PostcodeMatch,
			IsPrimaryForVenue = mapping.IsPrimaryForVenue,
			IsMutualBest = mapping.IsMutualBest,
		};

	#endregion
}
