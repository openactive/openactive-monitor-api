using Google.Cloud.BigQuery.V2;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;

namespace MonitorApi.Controllers;

/// <summary>
/// OpenActive monitor API. Every endpoint requires a valid access token supplied as the <c>token</c> query parameter;
/// requests with a missing or incorrect token receive HTTP 403.
/// </summary>
[Route("/")]
[ApiController]
public class ApiController(IOptions<BigQueryOptions> options, IOptions<ApiOptions> apiOptions, IMemoryCache cache) : ControllerBase, IActionFilter
{
	private const string SummaryCacheKey = "summary";
	private static readonly TimeSpan SummaryCacheTtl = TimeSpan.FromHours(1);

	protected BigQueryOptions options = options.Value;
	protected ApiOptions apiOptions = apiOptions.Value;
	protected IMemoryCache cache = cache;

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
	/// Returns active opportunities, optionally narrowed by publisher, location, and activity/facility type.
	/// </summary>
	/// <remarks>
	/// When no parameters are supplied, all results are returned unfiltered.
	/// Supplying one or more parameters narrows the results — all supplied filters are combined with AND.
	/// </remarks>
	/// <param name="publisher">Exact publisher name to match.</param>
	/// <param name="district">Local authority district (LAD) code to match.</param>
	/// <param name="region">Region code to match.</param>
	/// <param name="country">Country code to match.</param>
	/// <param name="activity">Activity or facility label.</param>
	[HttpGet("opportunities")]
	public Task<object> Opportunities(string? publisher = null, string? district = null, string? region = null, string? country = null, string? activity = null)
	{
		var conditions = new List<string>();
		var parameters = new List<BigQueryParameter>();

		if (!string.IsNullOrWhiteSpace(publisher))
		{
			conditions.Add("publisher = @publisher");
			parameters.Add(new BigQueryParameter("publisher", BigQueryDbType.String, publisher));
		}

		if (!string.IsNullOrWhiteSpace(district))
		{
			conditions.Add("district_code = @district");
			parameters.Add(new BigQueryParameter("district", BigQueryDbType.String, district));
		}

		if (!string.IsNullOrWhiteSpace(region))
		{
			conditions.Add("region_code = @region");
			parameters.Add(new BigQueryParameter("region", BigQueryDbType.String, region));
		}

		if (!string.IsNullOrWhiteSpace(country))
		{
			conditions.Add("country_code = @country");
			parameters.Add(new BigQueryParameter("country", BigQueryDbType.String, country));
		}

		if (!string.IsNullOrWhiteSpace(activity))
		{
			conditions.Add("EXISTS (SELECT 1 FROM UNNEST(JSON_EXTRACT_ARRAY(activity_or_facility)) AS a WHERE JSON_VALUE(a) = @activity)");
			parameters.Add(new BigQueryParameter("activity", BigQueryDbType.String, activity));
		}

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
	/// Returns aggregate metrics across all opportunities (counts of opportunities, publishers, and activities).
	/// </summary>
	/// <remarks>
	/// The result is cached for one hour; the first request after expiry re-runs the underlying BigQuery queries.
	/// </remarks>
	[HttpGet("summary")]
	public async Task<IActionResult> Summary()
	{
		// Cache the summary for one hour
		var payload = await cache.GetOrCreateAsync(SummaryCacheKey, async entry =>
		{
			entry.AbsoluteExpirationRelativeToNow = SummaryCacheTtl;

			var insightRows = (IAsyncEnumerable<Dictionary<string, object>>)await Execute(
				$"""
				SELECT total_num_future_opportunity_items AS n, run_date
				FROM {Fq(Tables.InsightRunSummary)}
				ORDER BY run_date DESC
				LIMIT 1
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
			var publishers = await publisherRows.FirstAsync();
			var activities = await activityRows.FirstAsync();

			return (object)new
			{
				number_of_opportunities = insight["n"],
				number_of_publishers = publishers["n"],
				number_of_activities = activities["n"],
				percentage_of_local_authorities = 74,
				number_of_activity_providers = 4885,
				date = insight["run_date"],
			};
		});

		return Ok(payload);
	}

	/// <summary>
	/// Returns the full location hierarchy (country → regions → districts) derived from the opportunities data.
	/// In Northern Ireland, Wales and Scotland, where there are no regions, districts are attached directly to the country (country → districts); in other countries, districts are grouped under their respective regions.
	/// </summary>
	/// <remarks>
	/// The response is keyed by country name; each country carries its <c>country_code</c> and a list of regions,
	/// each region (keyed by region name) carries its <c>region_code</c> and a list of <c>{ district_name, district_code }</c> entries.
	/// Districts whose region is null are attached directly to the country under a <c>districts</c> list.
	/// </remarks>
	[HttpGet("areas")]
	public async Task<IActionResult> Areas()
	{
		var rows = (IAsyncEnumerable<Dictionary<string, object>>)await Execute(
			$"""
			SELECT DISTINCT country_name, country_code, region_name, region_code, district_name, district_code
			FROM {Fq(Tables.ActiveOpportunitiesSummary)}
			WHERE country_name IS NOT NULL AND district_name IS NOT NULL
			"""
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
	/// <param name="district">Local authority district (LAD) code to match.</param>
	/// <param name="region">Region code to match.</param>
	/// <param name="country">Country code to match.</param>
	[HttpGet("publishers")]
	public async Task<IActionResult> Publishers(string? district = null, string? region = null, string? country = null)
	{
		var conditions = new List<string> { "publisher IS NOT NULL" };
		var parameters = new List<BigQueryParameter>();

		if (!string.IsNullOrWhiteSpace(district))
		{
			conditions.Add("district_code = @district");
			parameters.Add(new BigQueryParameter("district", BigQueryDbType.String, district));
		}

		if (!string.IsNullOrWhiteSpace(region))
		{
			conditions.Add("region_code = @region");
			parameters.Add(new BigQueryParameter("region", BigQueryDbType.String, region));
		}

		if (!string.IsNullOrWhiteSpace(country))
		{
			conditions.Add("country_code = @country");
			parameters.Add(new BigQueryParameter("country", BigQueryDbType.String, country));
		}

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
	/// <param name="district">Local authority district (LAD) code to match.</param>
	/// <param name="region">Region code to match.</param>
	/// <param name="country">Country code to match.</param>
	[HttpGet("activities")]
	public async Task<IActionResult> Activities(string? district = null, string? region = null, string? country = null)
	{
		var conditions = new List<string> { "JSON_VALUE(a) IS NOT NULL" };
		var parameters = new List<BigQueryParameter>();

		if (!string.IsNullOrWhiteSpace(district))
		{
			conditions.Add("district_code = @district");
			parameters.Add(new BigQueryParameter("district", BigQueryDbType.String, district));
		}

		if (!string.IsNullOrWhiteSpace(region))
		{
			conditions.Add("region_code = @region");
			parameters.Add(new BigQueryParameter("region", BigQueryDbType.String, region));
		}

		if (!string.IsNullOrWhiteSpace(country))
		{
			conditions.Add("country_code = @country");
			parameters.Add(new BigQueryParameter("country", BigQueryDbType.String, country));
		}

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

	#endregion

	#region Utilities

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
