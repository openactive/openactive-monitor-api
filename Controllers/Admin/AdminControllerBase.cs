using Google.Cloud.BigQuery.V2;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Options;
using MonitorApi.Models.Admin;

namespace MonitorApi.Controllers.Admin;

/// <summary>
/// Base class for the admin dashboard API. Every admin controller derives from this and inherits the
/// route prefix, the <c>AdminToken</c> check and the shared BigQuery/paging plumbing.
/// </summary>
/// <remarks>
/// Kept entirely separate from <see cref="ApiController"/>, which serves the public analytics platform
/// and is authenticated with a different token. Neither surface shares state with the other.
/// </remarks>
[Route("admin")]
[ApiController]
[OutputCache(PolicyName = "FifteenMinutes")]
// Puts every derived controller in the "admin" OpenAPI document instead of the public one.
[ApiExplorerSettings(GroupName = ApiDocuments.AdminGroupName)]
public abstract class AdminControllerBase(IOptions<BigQueryOptions> bigQueryOptions, IOptions<ApiOptions> apiOptions)
	: ControllerBase, IActionFilter
{
	/// <summary>Largest page the admin API will serve, whatever <c>page_size</c> asks for.</summary>
	public const int MaxPageSize = 1000;

	/// <summary>Page size used when the caller does not supply one.</summary>
	public const int DefaultPageSize = 500;

	protected readonly BigQueryOptions bigQuery = bigQueryOptions.Value;
	protected readonly ApiOptions api = apiOptions.Value;

	private BigQueryClient? client;

	/// <summary>
	/// Admin endpoints are gated on <c>Api:AdminToken</c>, passed as the <c>token</c> query parameter.
	/// When no admin token is configured the whole surface refuses every request rather than falling
	/// back to the public token.
	/// </summary>
	/// <remarks>
	/// <c>[NonAction]</c> is required on both filter methods: they are public methods on a controller, so
	/// without it MVC routes them as actions. Having no route template of their own they inherit the
	/// controller's (<c>admin</c>), which makes every request to <c>/admin</c> ambiguous.
	/// </remarks>
	[NonAction]
	public void OnActionExecuting(ActionExecutingContext context)
	{
		var configured = api.AdminToken;
		var supplied = context.HttpContext.Request.Query["token"].ToString();

		if (string.IsNullOrWhiteSpace(configured) || supplied != configured)
		{
			context.Result = new ObjectResult(new { message = "Please provide a valid admin token." })
			{
				StatusCode = StatusCodes.Status403Forbidden,
			};
		}
	}

	[NonAction]
	public void OnActionExecuted(ActionExecutedContext context)
	{
	}

	/// <summary>Fully qualifies a table name from <see cref="Tables"/>.</summary>
	protected string Fq(string table) => $"`{bigQuery.ProjectId}.{bigQuery.DatasetId}.{table}`";

	/// <summary>
	/// Runs a query and streams the rows back as dictionaries keyed by column name. Columns that are
	/// <c>NULL</c> for a row are absent from that row's dictionary.
	/// </summary>
	protected async Task<IAsyncEnumerable<Dictionary<string, object>>> Query(
		string sql,
		params IEnumerable<BigQueryParameter> parameters)
	{
		client ??= await BigQueryClient.CreateAsync(bigQuery.ProjectId, bigQuery.GoogleCredential);
		var result = await client.ExecuteQueryAsync(sql, parameters);

		return result.GetRowsAsync().Select(row =>
		{
			var values = new Dictionary<string, object>();
			foreach (var field in row.Schema.Fields)
			{
				var cell = row[field.Name];
				if (cell is not null)
				{
					values[field.Name] = cell;
				}
			}
			return values;
		});
	}

	/// <summary>Runs a query expected to return a single row, or <c>null</c> when it returns none.</summary>
	protected async Task<Dictionary<string, object>?> QuerySingle(
		string sql,
		params IEnumerable<BigQueryParameter> parameters)
	{
		var rows = await Query(sql, parameters);
		await foreach (var row in rows)
		{
			return row;
		}
		return null;
	}

	/// <summary>
	/// Applies one-based paging to an already-ordered result set and wraps it in the standard envelope.
	/// </summary>
	protected static AdminPage<T> Paginate<T>(IReadOnlyList<T> rows, int page, int pageSize, DateOnly snapshotDate)
	{
		page = Math.Max(1, page);
		pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

		// Read the clock once: truncating with two separate DateTime.UtcNow reads can straddle a tick
		// boundary and leave the sub-second component intact.
		var now = DateTime.UtcNow;

		return new AdminPage<T>
		{
			Data = rows.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
			Meta = new AdminPageMeta
			{
				SnapshotDate = snapshotDate,
				// Truncated to whole seconds so the payload matches the documented ISO-8601 shape.
				GeneratedAt = new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc),
				Page = page,
				PageSize = pageSize,
				Total = rows.Count,
			},
		};
	}
}
