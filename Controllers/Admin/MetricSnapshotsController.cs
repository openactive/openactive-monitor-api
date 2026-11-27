using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Options;
using MonitorApi.Models.Admin;
using MonitorApi.Services.Admin;

namespace MonitorApi.Controllers.Admin;

/// <summary>
/// The metric snapshot store: the month-by-month series the KPI report is built from.
/// </summary>
/// <remarks>
/// Every other admin endpoint reports current state, because the tables behind them hold current
/// state only. This one exists so the measures defined as a trend have a history at all: a period
/// that is never captured cannot be recovered afterwards.
///
/// Not cached. The capture job writes and reads back in one run, and a figure held for the rest of
/// the day would make a fresh capture look like it had failed.
/// </remarks>
[OutputCache(NoStore = true)]
public class MetricSnapshotsController(
	IOptions<BigQueryOptions> bigQueryOptions,
	IOptions<ApiOptions> apiOptions)
	: AdminControllerBase(bigQueryOptions, apiOptions)
{
	/// <summary>
	/// Read the captured series, newest period first.
	/// </summary>
	/// <remarks>
	/// Every filter is optional and omitting one means "all values", not "none". Filters accept
	/// repeated (<c>?source=a&amp;source=b</c>) or comma-separated (<c>?source=a,b</c>) values;
	/// values within one filter are OR'd and different filters are AND'd, as elsewhere on this API.
	/// </remarks>
	/// <param name="source">Where the figure came from, e.g. <c>feed_quality</c>.</param>
	/// <param name="field">The field within that source, e.g. <c>completeness_location</c>.</param>
	/// <param name="from">Earliest period to return, inclusive. A date, given as the first of the month.</param>
	/// <param name="to">Latest period to return, inclusive.</param>
	/// <param name="page">One-based page number.</param>
	/// <param name="page_size">Rows per page, up to 1000.</param>
	[HttpGet("metric-snapshots")]
	[ProducesResponseType(typeof(AdminPage<MetricSnapshotRow>), StatusCodes.Status200OK)]
	public async Task<ActionResult<AdminPage<MetricSnapshotRow>>> List(
		[FromQuery] string[]? source = null,
		[FromQuery] string[]? field = null,
		[FromQuery] DateOnly? from = null,
		[FromQuery] DateOnly? to = null,
		int page = 1,
		int page_size = DefaultPageSize)
	{
		var table = Fq(Tables.MetricSnapshots);

		var sql = MetricSnapshotsQuery.ListSql(table);
		var parameters = MetricSnapshotsQuery.ListParameters(
			NormaliseMultiValue(source),
			NormaliseMultiValue(field),
			from,
			to);

		var rows = new List<MetricSnapshotRow>();
		await foreach (var row in await Query(sql, parameters))
		{
			rows.Add(MetricSnapshotsQuery.ParseRow(row));
		}

		return Ok(Paginate(rows, page, page_size, DateOnly.FromDateTime(DateTime.UtcNow)));
	}

	/// <summary>
	/// Capture one or more measurements. A measurement is identified by
	/// (<c>period</c>, <c>source</c>, <c>field</c>) and may be captured only once.
	/// </summary>
	/// <remarks>
	/// The whole batch is written in one statement, so a monthly capture of every field costs a
	/// single INSERT. It is all-or-nothing in the ordinary case: a batch carrying a measurement that
	/// already exists, or the same measurement twice, is refused in full and nothing is written —
	/// which makes a retried capture safe rather than a source of duplicates.
	///
	/// The check and the insert are two statements, so two captures of the same period running at
	/// the same time could still both succeed. The monthly job is the only writer and runs alone,
	/// and the table carries no constraint of its own to fall back on.
	///
	/// <c>captured_at</c> is set by BigQuery and ignored if supplied.
	/// </remarks>
	/// <param name="body">The measurements to capture. Must not be empty.</param>
	[HttpPost("metric-snapshots")]
	[ProducesResponseType(typeof(AdminPage<MetricSnapshotRow>), StatusCodes.Status201Created)]
	[ProducesResponseType(StatusCodes.Status400BadRequest)]
	[ProducesResponseType(StatusCodes.Status409Conflict)]
	public async Task<ActionResult<AdminPage<MetricSnapshotRow>>> Create(
		[FromBody] List<MetricSnapshotWrite> body)
	{
		if (body is null || body.Count == 0)
		{
			return BadRequest(new { message = "Provide at least one measurement to capture." });
		}

		var blank = body.Any(r => string.IsNullOrWhiteSpace(r.Source) || string.IsNullOrWhiteSpace(r.Field));
		if (blank)
		{
			return BadRequest(new { message = "Every measurement needs a non-empty source and field." });
		}

		var keys = body.Select(MetricSnapshotsQuery.Key).ToList();
		var repeated = keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
		if (repeated.Count > 0)
		{
			return BadRequest(new
			{
				message = "The same measurement appears more than once in this batch.",
				measurements = repeated,
			});
		}

		var table = Fq(Tables.MetricSnapshots);
		var boxSql = MetricSnapshotsQuery.BoxSql(table);
		var boxParameters = MetricSnapshotsQuery.BoxParameters(body);

		var wanted = keys.ToHashSet(StringComparer.Ordinal);
		var clashes = new List<string>();

		await foreach (var row in await Query(boxSql, boxParameters))
		{
			var key = MetricSnapshotsQuery.Key(row);
			if (wanted.Contains(key))
			{
				clashes.Add(key);
			}
		}

		if (clashes.Count > 0)
		{
			return Conflict(new
			{
				message = "These measurements have already been captured. Nothing was written.",
				measurements = clashes.Order(StringComparer.Ordinal).ToList(),
			});
		}

		var (insertSql, insertParameters) = MetricSnapshotsQuery.Insert(table, body);
		await QuerySingle(insertSql, insertParameters);

		// Read back through the same box, so the response carries what BigQuery actually stored --
		// captured_at in particular, which the caller never supplies.
		var written = new List<MetricSnapshotRow>();
		await foreach (var row in await Query(boxSql, boxParameters))
		{
			if (wanted.Contains(MetricSnapshotsQuery.Key(row)))
			{
				written.Add(MetricSnapshotsQuery.ParseRow(row));
			}
		}

		var page = Paginate(
			written.OrderBy(r => r.Period).ThenBy(r => r.Source, StringComparer.Ordinal)
				.ThenBy(r => r.Field, StringComparer.Ordinal).ToList(),
			page: 1,
			pageSize: MaxPageSize,
			snapshotDate: DateOnly.FromDateTime(DateTime.UtcNow));

		return StatusCode(StatusCodes.Status201Created, page);
	}
}
