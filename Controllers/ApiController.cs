using Google.Cloud.BigQuery.V2;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Options;
using MonitorApi.Models;
using System.ComponentModel.DataAnnotations;

namespace MonitorApi.Controllers;

/// <summary>
/// OpenActive monitor API. Every endpoint requires a valid access token supplied as the <c>token</c> query parameter;
/// requests with a missing or incorrect token receive HTTP 403.
/// </summary>
[Route("/")]
[ApiController]
[OutputCache(PolicyName = "FourHours")]
public class ApiController(IOptions<BigQueryOptions> options, IOptions<ApiOptions> apiOptions) : ControllerBase, IActionFilter
{
	protected BigQueryOptions options = options.Value;
	protected ApiOptions apiOptions = apiOptions.Value;

	public void OnActionExecuting(ActionExecutingContext context)
	{
		// All services are protected by a simple access token for now, to prevent abuse. The token is passed as a query parameter.
		var token = context.HttpContext.Request.Query["token"].ToString();
		if (token != apiOptions.AccessToken)
		{
			context.Result = new ObjectResult(new { message = "Please provide a valid token." })
			{
				StatusCode = StatusCodes.Status403Forbidden,
			};
		}
	}

	public void OnActionExecuted(ActionExecutedContext context)
	{
	}

	#region Endpoints

	/// <summary>
	/// Returns aggregate metrics across all opportunities (counts of opportunities, publishers, and activities).
	/// </summary>
	/// <remarks>
	/// The result is cached for four hours via output caching; the first request after expiry re-runs the underlying BigQuery queries.
	/// </remarks>
	[HttpGet("summary")]
	[ProducesResponseType(typeof(SummaryResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<SummaryResponse>> Summary()
	{
		var insightRows = (IAsyncEnumerable<Dictionary<string, object>>)await Execute(
			$"""
			SELECT total_num_future_opportunity_items AS n, run_date
			FROM {Fq(Tables.InsightRunSummary)}
			ORDER BY run_date DESC
			LIMIT 1
			"""
		);
		var opportunitiesCount = (IAsyncEnumerable<Dictionary<string, object>>)await Execute(
			$"""
			SELECT COUNT(*) AS n
			FROM {Fq(Tables.Opportunities)}
			WHERE startDate >= TIMESTAMP(CURRENT_DATE()) AND district_name IS NOT NULL AND district_name != '' AND publisher_name IS NOT NULL AND publisher_name != ''
			"""
		);
		var publisherRows = (IAsyncEnumerable<Dictionary<string, object>>)await Execute(
			$"""
			SELECT COUNT(DISTINCT dataset_url) AS n
			FROM {Fq(Tables.Feeds)}
			"""
		);
		var activityRows = (IAsyncEnumerable<Dictionary<string, object>>)await Execute(
			$"""
			SELECT COUNT(DISTINCT JSON_VALUE(a)) AS n
			FROM {Fq(Tables.Opportunities)} AS o,
			     UNNEST(JSON_EXTRACT_ARRAY(o.activity)) AS a
			WHERE JSON_VALUE(a) IS NOT NULL
			"""
		);

		var insight = await insightRows.FirstAsync();
		var opportunities = await opportunitiesCount.FirstAsync();
		var publishers = await publisherRows.FirstAsync();
		var activities = await activityRows.FirstAsync();

		return Ok(new SummaryResponse
		{
			NumberOfOpportunities = (long)opportunities["n"],
			NumberOfPublishers = (long)publishers["n"],
			NumberOfActivities = (long)activities["n"],
			PercentageOfLocalAuthorities = 74,
			NumberOfActivityProviders = 4885,
			Date = (DateTime)insight["run_date"],
		});
	}

	/// <summary>
	/// Returns active opportunities, optionally narrowed by publisher, location, and activity/facility type.
	/// </summary>
	/// <remarks>
	/// When no parameters are supplied, all results are returned unfiltered.
	/// Supplying one or more parameters narrows the results — all supplied filters are combined with AND.
	/// The <c>activity</c> filter accepts either a single value (<c>?activity=Yoga</c>) or multiple values
	/// (<c>?activity=Yoga&amp;activity=Pilates</c> or a comma-separated <c>?activity=Yoga,Pilates</c>);
	/// rows are returned if any of the supplied activities is present.
	/// </remarks>
	/// <param name="publisher">One or more publisher names. A row matches if any of the supplied values is present.</param>
	/// <param name="district">One or more local authority district (LAD) codes.</param>
	/// <param name="region">One or more region codes.</param>
	/// <param name="country">One or more country codes.</param>
	/// <param name="activity">One or more activity/facility labels. A row matches if any of the supplied values is present.</param>
	/// <param name="organization">One or more organization names. A row matches if any of the supplied values is present.</param>
	/// <param name="nhs_trust">One or more NHS trust codes. A row matches if any of the supplied values is present. Accepts repeated (<c>?nhs_trust=X&amp;nhs_trust=Y</c>) or comma-separated (<c>?nhs_trust=X,Y</c>) values.</param>
	[HttpGet("opportunities")]
	[ProducesResponseType(typeof(IEnumerable<Dictionary<string, object>>), StatusCodes.Status200OK)]
	public Task<object> Opportunities([FromQuery] string[]? publisher = null, [FromQuery] string[]? district = null, [FromQuery] string[]? region = null, [FromQuery] string[]? country = null, [FromQuery] string[]? activity = null, [FromQuery] string[]? organization = null, [FromQuery] string[]? nhs_trust = null)
	{
		var conditions = new List<string>();
		var parameters = new List<BigQueryParameter>();

		AddLocationFilters(conditions, parameters, district, region, country);
		AddPublisherFilter(conditions, parameters, publisher);
		AddActivityFilter(conditions, parameters, activity);
		AddOrganizationFilter(conditions, parameters, organization);
		AddNhsTrustFilter(conditions, parameters, nhs_trust);

		var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

		return Execute(
			$"""
			SELECT *
			FROM {Fq(Tables.ActiveOpportunitiesSummary)}
			{where}
			""",
			parameters
		);
	}

	/// <summary>
	/// Returns active opportunity records (one row per opportunity) from the raw <c>opportunities</c> table, paginated by offset and limit.
	/// </summary>
	/// <remarks>
	/// In addition to the supplied filters, the result always satisfies: <c>startDate &gt;= today's midnight UTC</c>, non-empty <c>district_name</c>, and non-empty <c>publisher_name</c>.
	/// Pagination is offset-based; <c>hasMore</c> indicates whether further results exist beyond the returned page (no total count is computed).
	/// JSON columns (<c>location</c>, <c>activity</c>, <c>facility</c>, <c>json_data</c>) are emitted as nested JSON, not stringified.
	/// </remarks>
	/// <param name="publisher">One or more publisher names (against <c>publisher_name</c>).</param>
	/// <param name="district">One or more local authority district (LAD) codes.</param>
	/// <param name="region">One or more region codes.</param>
	/// <param name="country">One or more country codes.</param>
	/// <param name="activity">One or more activity/facility labels; a row matches if any of the supplied values is present in either the <c>activity</c> array or the <c>facility</c> array. Accepts a single value (<c>?activity=Yoga</c>) or multiple values (<c>?activity=Yoga&amp;activity=Pilates</c> or comma-separated <c>?activity=Yoga,Pilates</c>).</param>
	/// <param name="organization">One or more organization names.</param>
	/// <param name="nhs_trust">One or more NHS trust codes. A row matches if any of the supplied values is present.</param>
	/// <param name="offset">Records offset. Default <c>0</c>.</param>
	/// <param name="limit">Page size. Default <c>20</c>.</param>
	[HttpGet("opportunity-records")]
	[ProducesResponseType(typeof(PaginatedResponse<OpportunityRecord>), StatusCodes.Status200OK)]
	public async Task<ActionResult<PaginatedResponse<OpportunityRecord>>> OpportunityRecords(
		[FromQuery] string[]? publisher = null,
		[FromQuery] string[]? district = null,
		[FromQuery] string[]? region = null,
		[FromQuery] string[]? country = null,
		[FromQuery] string[]? activity = null,
		[FromQuery] string[]? organization = null,
		[FromQuery] string[]? nhs_trust = null,
		int offset = 0,
		int limit = 20)
	{
		offset = Math.Max(0, offset);
		limit = Math.Clamp(limit, 1, 100);

		var conditions = new List<string>
		{
			"startDate >= TIMESTAMP(CURRENT_DATE())",
			"district_name IS NOT NULL AND district_name != ''",
			"publisher_name IS NOT NULL AND publisher_name != ''",
		};
		var parameters = new List<BigQueryParameter>();

		AddLocationFilters(conditions, parameters, district, region, country);
		AddPublisherFilter(conditions, parameters, publisher, column: "publisher_name");
		AddOpportunityActivityFilter(conditions, parameters, activity);
		AddOpportunityOrganizationFilter(conditions, parameters, organization);
		AddNhsTrustFilter(conditions, parameters, nhs_trust);

		parameters.Add(new BigQueryParameter("offset", BigQueryDbType.Int64, (long)offset));
		parameters.Add(new BigQueryParameter("limit", BigQueryDbType.Int64, (long)(limit + 1)));

		var where = "WHERE " + string.Join(" AND ", conditions);
		var query = $"""
			SELECT publisher_name, feed_id, id, kind, startDate, endDate, last_updated,
			       location, district_name, district_code, region_name, region_code,
			       country_name, country_code, activity, facility, json_data, ageRange, level, accessibilitySupport, genderRestriction, organization_name
			FROM {Fq(Tables.Opportunities)}
			{where}
			ORDER BY startDate ASC, feed_id ASC, id ASC
			LIMIT @limit OFFSET @offset
			""";

		var rows = (IAsyncEnumerable<Dictionary<string, object>>)await Execute(query, parameters);
		var fetched = await rows.ToListAsync();

		var hasMore = fetched.Count > limit;
		var items = fetched.Take(limit).Select(OpportunityRecord.FromBigQueryRow).ToList();

		return Ok(new PaginatedResponse<OpportunityRecord>
		{
			Items = items,
			Offset = offset,
			Limit = limit,
			HasMore = hasMore,
		});
	}

	/// <summary>
	/// Returns the full location hierarchy (country → regions → districts) derived from the opportunities data.
	/// In Northern Ireland, Wales and Scotland, where there are no regions, districts are attached directly to the country (country → districts); in other countries, districts are grouped under their respective regions.
	/// </summary>
	/// <remarks>
	/// The response is keyed by country name; each country carries its <c>country_code</c> and a list of regions,
	/// each region (keyed by region name) carries its <c>region_code</c> and a list of <c>{ district_name, district_code }</c> entries.
	/// Districts whose region is null are attached directly to the country under a <c>districts</c> list.
	/// <param name="publisher">One or more publisher names.</param>
	/// <param name="activity">One or more activity/facility labels. A row matches if any of the supplied values is present. Accepts a single value (<c>?activity=Yoga</c>) or multiple values (<c>?activity=Yoga&amp;activity=Pilates</c> or comma-separated <c>?activity=Yoga,Pilates</c>).</param>
	/// <param name="organization">One or more organization names.</param>
	/// <param name="nhs_trust">One or more NHS trust codes. A row matches if any of the supplied values is present.</param>
	/// </remarks>
	[HttpGet("areas")]
	[ProducesResponseType(typeof(Dictionary<string, object>), StatusCodes.Status200OK)]
	public async Task<IActionResult> Areas([FromQuery] string[]? publisher = null, [FromQuery] string[]? activity = null, [FromQuery] string[]? organization = null, [FromQuery] string[]? nhs_trust = null)
	{
		var conditions = new List<string>();
		var parameters = new List<BigQueryParameter>();

		AddPublisherFilter(conditions, parameters, publisher);
		AddActivityFilter(conditions, parameters, activity);
		AddOrganizationFilter(conditions, parameters, organization);
		AddNhsTrustFilter(conditions, parameters, nhs_trust);

		var extra_conditions = conditions.Count > 0 ? "AND " + string.Join(" AND ", conditions) : "";

		var rows = (IAsyncEnumerable<Dictionary<string, object>>)await Execute(
			$"""
			SELECT DISTINCT country_name, country_code, region_name, region_code, district_name, district_code
			FROM {Fq(Tables.ActiveOpportunitiesSummary)}
			WHERE country_name IS NOT NULL AND district_name IS NOT NULL {extra_conditions}
			""",
			parameters
		);

		var countryCodes = new Dictionary<string, string?>();
		var regionsByCountry = new Dictionary<string, HashSet<string>>();
		var regionCodes = new Dictionary<(string Country, string Region), string?>();
		var districtsByRegion = new Dictionary<(string Country, string Region), List<(string Name, string? Code)>>();
		var districtsByCountry = new Dictionary<string, List<(string Name, string? Code)>>();

		await foreach (var row in rows)
		{
			var country = (string)row["country_name"];
			var region = row.GetValueOrDefault("region_name") as string;
			var district = ((string)row["district_name"], row.GetValueOrDefault("district_code") as string);

			if (!countryCodes.ContainsKey(country))
			{
				countryCodes[country] = row.GetValueOrDefault("country_code") as string;
				regionsByCountry[country] = new HashSet<string>();
				districtsByCountry[country] = new List<(string, string?)>();
			}

			if (string.IsNullOrWhiteSpace(region))
			{
				districtsByCountry[country].Add(district);
				continue;
			}

			var key = (country, region);
			if (!regionCodes.ContainsKey(key))
			{
				regionsByCountry[country].Add(region);
				regionCodes[key] = row.GetValueOrDefault("region_code") as string;
				districtsByRegion[key] = new List<(string, string?)>();
			}

			districtsByRegion[key].Add(district);
		}

		static IEnumerable<object> SortedDistricts(List<(string Name, string? Code)> list) =>
			list.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
				.Select(d => new { district_name = d.Name, district_code = d.Code });

		var result = new Dictionary<string, object>();
		foreach (var country in countryCodes.Keys.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
		{
			var regions = new List<object>();
			foreach (var region in regionsByCountry[country].OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
			{
				var key = (country, region);
				regions.Add(new Dictionary<string, object>
				{
					[region] = new
					{
						region_code = regionCodes[key],
						districts = SortedDistricts(districtsByRegion[key]),
					},
				});
			}

			var countryNode = new Dictionary<string, object?>
			{
				["country_code"] = countryCodes[country],
				["regions"] = regions,
			};

			if (districtsByCountry[country].Count > 0)
			{
				countryNode["districts"] = SortedDistricts(districtsByCountry[country]);
				countryNode.Remove("regions");
			}

			result[country] = countryNode;
		}

		return Ok(result);
	}

	/// <summary>
	/// Returns all distinct publisher names in alphabetical order, optionally narrowed by location.
	/// </summary>
	/// <remarks>
	/// When no parameters are supplied, every publisher is returned.
	/// Supplying one or more parameters narrows the results — all supplied filters are combined with AND.
	/// </remarks>
	/// <param name="district">One or more local authority district (LAD) codes.</param>
	/// <param name="region">One or more region codes.</param>
	/// <param name="country">One or more country codes.</param>
	/// <param name="activity">One or more activity/facility labels. A row matches if any of the supplied values is present. Accepts a single value (<c>?activity=Yoga</c>) or multiple values (<c>?activity=Yoga&amp;activity=Pilates</c> or comma-separated <c>?activity=Yoga,Pilates</c>).</param>
	/// <param name="organization">One or more organization names.</param>
	/// <param name="nhs_trust">One or more NHS trust codes. A row matches if any of the supplied values is present.</param>
	[HttpGet("publishers")]
	[ProducesResponseType(typeof(string[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<string[]>> Publishers([FromQuery] string[]? district = null, [FromQuery] string[]? region = null, [FromQuery] string[]? country = null, [FromQuery] string[]? activity = null, [FromQuery] string[]? organization = null, [FromQuery] string[]? nhs_trust = null)
	{
		var conditions = new List<string> { "publisher IS NOT NULL" };
		var parameters = new List<BigQueryParameter>();

		AddLocationFilters(conditions, parameters, district, region, country);
		AddActivityFilter(conditions, parameters, activity);
		AddOrganizationFilter(conditions, parameters, organization);
		AddNhsTrustFilter(conditions, parameters, nhs_trust);

		var where = "WHERE " + string.Join(" AND ", conditions);

		var rows = (IAsyncEnumerable<Dictionary<string, object>>)await Execute(
			$"""
			SELECT DISTINCT publisher
			FROM {Fq(Tables.ActiveOpportunitiesSummary)}
			{where}
			""",
			parameters
		);

		var publishers = await rows.Select(r => (string)r["publisher"]).ToListAsync();
		publishers.Sort(StringComparer.OrdinalIgnoreCase);

		return Ok(publishers);
	}

	/// <summary>
	/// Returns every distinct activity/facility value (flattened from the <c>activity_or_facility</c> JSON array) in alphabetical order, optionally narrowed by location.
	/// </summary>
	/// <remarks>
	/// When no parameters are supplied, every activity is returned.
	/// Supplying one or more parameters narrows the results — all supplied filters are combined with AND.
	/// </remarks>
	/// <param name="publisher">One or more publisher names.</param>
	/// <param name="district">One or more local authority district (LAD) codes.</param>
	/// <param name="region">One or more region codes.</param>
	/// <param name="country">One or more country codes.</param>
	/// <param name="organization">One or more organization names.</param>
	/// <param name="nhs_trust">One or more NHS trust codes. A row matches if any of the supplied values is present.</param>
	[HttpGet("activities")]
	[ProducesResponseType(typeof(string[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<string[]>> Activities([FromQuery] string[]? publisher = null, [FromQuery] string[]? district = null, [FromQuery] string[]? region = null, [FromQuery] string[]? country = null, [FromQuery] string[]? organization = null, [FromQuery] string[]? nhs_trust = null)
	{
		var conditions = new List<string> { "JSON_VALUE(a) IS NOT NULL" };
		var parameters = new List<BigQueryParameter>();

		AddLocationFilters(conditions, parameters, district, region, country);
		AddPublisherFilter(conditions, parameters, publisher);
		AddOrganizationFilter(conditions, parameters, organization);
		AddNhsTrustFilter(conditions, parameters, nhs_trust);

		var where = "WHERE " + string.Join(" AND ", conditions);

		var rows = (IAsyncEnumerable<Dictionary<string, object>>)await Execute(
			$"""
			SELECT DISTINCT JSON_VALUE(a) AS activity
			FROM {Fq(Tables.ActiveOpportunitiesSummary)} AS o,
			     UNNEST(JSON_EXTRACT_ARRAY(o.activity_or_facility)) AS a
			{where}
			""",
			parameters
		);

		var activities = await rows.Select(r => (string)r["activity"]).ToListAsync();
		activities.Sort(StringComparer.OrdinalIgnoreCase);

		return Ok(activities);
	}

	/// <summary>
	/// Returns all distinct NHS trust names in alphabetical order, optionally narrowed by location, publisher, activity, and organization.
	/// </summary>
	/// <remarks>
	/// When no parameters are supplied, every NHS trust is returned.
	/// Supplying one or more parameters narrows the results — all supplied filters are combined with AND.
	/// </remarks>
	/// <param name="publisher">One or more publisher names.</param>
	/// <param name="district">One or more local authority district (LAD) codes.</param>
	/// <param name="region">One or more region codes.</param>
	/// <param name="country">One or more country codes.</param>
	/// <param name="activity">One or more activity/facility labels. A row matches if any of the supplied values is present. Accepts a single value (<c>?activity=Yoga</c>) or multiple values (<c>?activity=Yoga&amp;activity=Pilates</c> or comma-separated <c>?activity=Yoga,Pilates</c>).</param>
	/// <param name="organization">One or more organization names.</param>
	[HttpGet("nhs-trusts")]
	[ProducesResponseType(typeof(string[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<string[]>> NhsTrusts([FromQuery] string[]? publisher = null, [FromQuery] string[]? district = null, [FromQuery] string[]? region = null, [FromQuery] string[]? country = null, [FromQuery] string[]? activity = null, [FromQuery] string[]? organization = null)
	{
		var conditions = new List<string> { "nhstrust_name IS NOT NULL" };
		var parameters = new List<BigQueryParameter>();

		AddLocationFilters(conditions, parameters, district, region, country);
		AddPublisherFilter(conditions, parameters, publisher);
		AddActivityFilter(conditions, parameters, activity);
		AddOrganizationFilter(conditions, parameters, organization);

		var where = "WHERE " + string.Join(" AND ", conditions);

		var rows = (IAsyncEnumerable<Dictionary<string, object>>)await Execute(
			$"""
			SELECT DISTINCT nhstrust_name
			FROM {Fq(Tables.ActiveOpportunitiesSummary)}
			{where}
			""",
			parameters
		);

		var trusts = await rows.Select(r => (string)r["nhstrust_name"]).ToListAsync();
		trusts.Sort(StringComparer.OrdinalIgnoreCase);

		return Ok(trusts);
	}

	/// <summary>
	/// Returns all distinct organization names in alphabetical order, optionally narrowed by location, publisher, and activity.
	/// </summary>
	/// <param name="publisher">One or more publisher names.</param>
	/// <param name="district">One or more local authority district (LAD) codes.</param>
	/// <param name="region">One or more region codes.</param>
	/// <param name="country">One or more country codes.</param>
	/// <param name="activity">One or more activity/facility labels. A row matches if any of the supplied values is present.</param>
	/// <param name="nhs_trust">One or more NHS trust codes. A row matches if any of the supplied values is present.</param>
	[HttpGet("organizations")]
	[ProducesResponseType(typeof(string[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<string[]>> Organizations([FromQuery] string[]? publisher = null, [FromQuery] string[]? district = null, [FromQuery] string[]? region = null, [FromQuery] string[]? country = null, [FromQuery] string[]? activity = null, [FromQuery] string[]? nhs_trust = null)
	{
		var conditions = new List<string>
		{
			"organization_name IS NOT NULL AND organization_name != ''",
			"startDate >= TIMESTAMP(CURRENT_DATE())",
			"district_name IS NOT NULL AND district_name != ''",
			"publisher_name IS NOT NULL AND publisher_name != ''",
		};
		var parameters = new List<BigQueryParameter>();

		AddLocationFilters(conditions, parameters, district, region, country);
		AddPublisherFilter(conditions, parameters, publisher, column: "publisher_name");
		AddOpportunityActivityFilter(conditions, parameters, activity);
		AddNhsTrustFilter(conditions, parameters, nhs_trust);

		var where = "WHERE " + string.Join(" AND ", conditions);

		var rows = (IAsyncEnumerable<Dictionary<string, object>>)await Execute(
			$"""
			SELECT DISTINCT organization_name AS organization
			FROM {Fq(Tables.Opportunities)}
			{where}
			""",
			parameters
		);

		var organizations = await rows.Select(r => (string)r["organization"]).ToListAsync();
		organizations.Sort(StringComparer.OrdinalIgnoreCase);

		return Ok(organizations);
	}

	/// <summary>
	/// Returns feed quality rows for all feeds.
	/// </summary>
	/// <remarks>
	/// This endpoint returns the latest values available in <c>feed_quality</c> for every row, with a fixed column set.
	/// </remarks>
	[HttpGet("feed-quality")]
	[ProducesResponseType(typeof(IEnumerable<FeedQualityRecord>), StatusCodes.Status200OK)]
	public async Task<ActionResult<IEnumerable<FeedQualityRecord>>> FeedQuality()
	{
		var rows = (IAsyncEnumerable<Dictionary<string, object>>)await Execute(
			$"""
			SELECT dataset_name,
			       dataset_url,
			       feed_type,
			       feed_url,
			       status,
			       warnings,
			       errors,
			       location_completeness,
			       start_date_completeness,
			       end_date_completeness,
			       activities_completeness,
			       facilities_completeness,
				   age_range_completeness,
				   level_completeness,
				   accessibility_support_completeness,
				   gender_restriction_completeness,
			       num_future_opportunity_items,
			       feed_version,
			       last_assessed
			FROM {Fq(Tables.FeedQuality)}
			ORDER BY last_assessed DESC, dataset_name ASC, feed_url ASC
			"""
		);

		var records = await rows.Select(FeedQualityRecord.FromBigQueryRow).ToListAsync();
		return Ok(records);
	}

	#endregion

	#region Utilities

	private static void AddLocationFilters(List<string> conditions, List<BigQueryParameter> parameters, string[]? district, string[]? region, string[]? country)
	{
		var districts = NormaliseMultiValue(district);
		if (districts.Count > 0)
		{
			conditions.Add("district_code IN UNNEST(@districts)");
			parameters.Add(new BigQueryParameter("districts", BigQueryDbType.Array, districts) { ArrayElementType = BigQueryDbType.String });
		}

		var regions = NormaliseMultiValue(region);
		if (regions.Count > 0)
		{
			conditions.Add("region_code IN UNNEST(@regions)");
			parameters.Add(new BigQueryParameter("regions", BigQueryDbType.Array, regions) { ArrayElementType = BigQueryDbType.String });
		}

		var countries = NormaliseMultiValue(country);
		if (countries.Count > 0)
		{
			conditions.Add("country_code IN UNNEST(@countries)");
			parameters.Add(new BigQueryParameter("countries", BigQueryDbType.Array, countries) { ArrayElementType = BigQueryDbType.String });
		}
	}

	private static void AddPublisherFilter(List<string> conditions, List<BigQueryParameter> parameters, string[]? publisher, string column = "publisher")
	{
		var values = NormaliseMultiValue(publisher);
		if (values.Count == 0) return;

		conditions.Add($"{column} IN UNNEST(@publishers)");
		parameters.Add(new BigQueryParameter("publishers", BigQueryDbType.Array, values) { ArrayElementType = BigQueryDbType.String });
	}

	private static void AddOrganizationFilter(List<string> conditions, List<BigQueryParameter> parameters, string[]? organization)
	{
		var values = NormaliseMultiValue(organization);
		if (values.Count == 0) return;

		conditions.Add("EXISTS (SELECT 1 FROM UNNEST(JSON_EXTRACT_ARRAY(organization_names)) AS o WHERE JSON_VALUE(o) IN UNNEST(@organizations))");
		parameters.Add(new BigQueryParameter("organizations", BigQueryDbType.Array, values) { ArrayElementType = BigQueryDbType.String });
	}

	private static void AddOpportunityOrganizationFilter(List<string> conditions, List<BigQueryParameter> parameters, string[]? organization)
	{
		var values = NormaliseMultiValue(organization);
		if (values.Count == 0) return;

		conditions.Add("organization_name IN UNNEST(@organizations)");
		parameters.Add(new BigQueryParameter("organizations", BigQueryDbType.Array, values) { ArrayElementType = BigQueryDbType.String });
	}

	private static void AddNhsTrustFilter(List<string> conditions, List<BigQueryParameter> parameters, string[]? nhs_trust, string column = "nhstrust_code")
	{
		var values = NormaliseMultiValue(nhs_trust);
		if (values.Count == 0) return;

		conditions.Add($"{column} IN UNNEST(@nhs_trusts)");
		parameters.Add(new BigQueryParameter("nhs_trusts", BigQueryDbType.Array, values) { ArrayElementType = BigQueryDbType.String });
	}

	private static void AddActivityFilter(List<string> conditions, List<BigQueryParameter> parameters, string[]? activity)
	{
		var values = NormaliseMultiValue(activity);
		if (values.Count == 0) return;

		conditions.Add("EXISTS (SELECT 1 FROM UNNEST(JSON_EXTRACT_ARRAY(activity_or_facility)) AS a WHERE JSON_VALUE(a) IN UNNEST(@activities))");
		parameters.Add(new BigQueryParameter("activities", BigQueryDbType.Array, values) { ArrayElementType = BigQueryDbType.String });
	}

	private static void AddOpportunityActivityFilter(List<string> conditions, List<BigQueryParameter> parameters, string[]? activity)
	{
		var values = NormaliseMultiValue(activity);
		if (values.Count == 0) return;

		conditions.Add(
			"(EXISTS (SELECT 1 FROM UNNEST(JSON_EXTRACT_ARRAY(activity)) AS a WHERE JSON_VALUE(a) IN UNNEST(@activities)) " +
			"OR EXISTS (SELECT 1 FROM UNNEST(JSON_EXTRACT_ARRAY(facility)) AS f WHERE JSON_VALUE(f) IN UNNEST(@activities)))"
		);
		parameters.Add(new BigQueryParameter("activities", BigQueryDbType.Array, values) { ArrayElementType = BigQueryDbType.String });
	}

	private static List<string> NormaliseMultiValue(string[]? values)
	{
		if (values is null || values.Length == 0) return [];

		return values
			.SelectMany(a => a?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [])
			.Where(a => !string.IsNullOrWhiteSpace(a))
			.Distinct()
			.ToList();
	}

	private string Fq(string table) => $"`{options.ProjectId}.{options.DatasetId}.{table}`";

	async private Task<object> Execute(string query, params IEnumerable<BigQueryParameter> parameters)
	{
		var client = await BigQueryClient.CreateAsync(options.ProjectId, options.GoogleCredential);
		var queryResult = await client.ExecuteQueryAsync(query, parameters);

		return queryResult.GetRowsAsync().Select(Convert);
	}

	private static Dictionary<string, object> Convert(BigQueryRow row)
	{
		var result = new Dictionary<string, object>();

		foreach (var field in row.Schema.Fields)
		{
			var cell = row[field.Name];

			if (cell is not null)
			{
				result[field.Name] = cell;
			}
		}

		return result;
	}

	#endregion
}
