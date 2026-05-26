using Google.Cloud.BigQuery.V2;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;

namespace MonitorApi.Controllers;

[Route("/")]
[ApiController]
public class ApiController(IOptions<BigQueryOptions> options) : ControllerBase
{
	protected BigQueryOptions options = options.Value;

	#region Endpoints

	[HttpGet("opportunities")]
	public Task<object> Opportunities() => Execute(
"""
SELECT *
FROM openactive-monitor.openactive_analytics.active_opportunities_summary
LIMIT 1000
"""
);

	[HttpGet("opportunities_over")]
	public Task<object> OpportunitiesByProvider([Required] int rows) => Execute(
		"""
SELECT *
FROM openactive-monitor.openactive_analytics.active_opportunities_summary
WHERE row_count > @rows
LIMIT 1000
""",
		new BigQueryParameter("rows", BigQueryDbType.Int64, rows)
	);

	[HttpGet("opportunities_by_provider")]
	public Task<object> OpportunitiesByProvider(string provider) => Execute(
		"""
SELECT *
FROM openactive-monitor.openactive_analytics.active_opportunities_summary
WHERE provider = @provider
LIMIT 1000
""",
		new BigQueryParameter("provider", BigQueryDbType.String, provider)
	);

	#endregion

	#region Utilities

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
