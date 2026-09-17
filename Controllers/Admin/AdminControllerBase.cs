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
[OutputCache(PolicyName = DailyRefreshCachePolicy.PolicyName)]
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

		return new AdminPage<T>
		{
			Data = rows.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
			Meta = Meta(snapshotDate, generatedAt: null, page, pageSize, total: rows.Count),
		};
	}

	/// <summary>
	/// Applies the same paging as <see cref="Paginate{T}"/> and carries an aggregate alongside the page.
	/// For endpoints that answer with rows <em>and</em> figures describing all of them.
	/// </summary>
	/// <param name="rows">The whole, already-ordered result set — not the page.</param>
	/// <param name="summary">
	/// The aggregate for <paramref name="rows"/> in full. Computed by the caller from the same list, so
	/// the figures always describe the rows being paged rather than a separately queried population.
	/// </param>
	/// <param name="page">Requested one-based page; clamped to at least one.</param>
	/// <param name="pageSize">Requested page size; clamped to <c>1</c>..<see cref="MaxPageSize"/>.</param>
	/// <param name="snapshotDate">The day the figures describe.</param>
	protected static AdminSummarisedPage<TRow, TSummary> PaginateWithSummary<TRow, TSummary>(
		IReadOnlyList<TRow> rows,
		TSummary summary,
		int page,
		int pageSize,
		DateOnly snapshotDate)
	{
		page = Math.Max(1, page);
		pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

		return new AdminSummarisedPage<TRow, TSummary>
		{
			Data = rows.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
			Summary = summary,
			Meta = Meta(snapshotDate, generatedAt: null, page, pageSize, total: rows.Count),
		};
	}

	/// <summary>
	/// Flattens a repeated query parameter into the distinct values it carries, accepting both
	/// <c>?a=x&amp;a=y</c> and <c>?a=x,y</c>. Blank entries are dropped.
	/// </summary>
	/// <remarks>
	/// The same shape the analytics surface offers, so a dashboard developer who has used one filter has
	/// used them all. Deliberately a copy rather than a reference to <c>ApiController</c>'s private
	/// helper: the two surfaces share nothing but options and table names, and a shared utility class
	/// for four lines would be the first thread tying them together.
	///
	/// <c>protected static</c> and not <c>public</c>: a public method on a controller with no route
	/// template of its own is routed as an action, and would collide with everything else on the
	/// controller's path.
	/// </remarks>
	protected static List<string> NormaliseMultiValue(string[]? values)
	{
		if (values is null || values.Length == 0)
		{
			return [];
		}

		return values
			.SelectMany(v => v?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [])
			.Where(v => !string.IsNullOrWhiteSpace(v))
			.Distinct(StringComparer.Ordinal)
			.ToList();
	}

	/// <summary>
	/// Wraps a single object in the same envelope, with the paging fields fixed at one row on one page.
	/// For endpoints whose answer is one document rather than a list.
	/// </summary>
	/// <param name="data">The document to return.</param>
	/// <param name="snapshotDate">The day the figures describe.</param>
	/// <param name="generatedAt">
	/// When the figures were computed, if the data itself dates them; <c>null</c> to use the current
	/// time as the paged endpoints do.
	/// </param>
	protected static AdminDocument<T> Document<T>(T data, DateOnly snapshotDate, DateTime? generatedAt = null) =>
		new()
		{
			Data = data,
			Meta = Meta(snapshotDate, generatedAt, page: 1, pageSize: 1, total: 1),
		};

	private static AdminPageMeta Meta(DateOnly snapshotDate, DateTime? generatedAt, int page, int pageSize, int total)
	{
		// Read the clock once: truncating with two separate DateTime.UtcNow reads can straddle a tick
		// boundary and leave the sub-second component intact.
		var stamp = generatedAt ?? DateTime.UtcNow;

		return new AdminPageMeta
		{
			SnapshotDate = snapshotDate,
			// Truncated to whole seconds so the payload matches the documented ISO-8601 shape.
			GeneratedAt = new DateTime(stamp.Ticks - (stamp.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc),
			Page = page,
			PageSize = pageSize,
			Total = total,
		};
	}
}
