using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Options;
using MonitorApi.Models.Admin;
using MonitorApi.Services.Admin;

namespace MonitorApi.Controllers.Admin;

/// <summary>
/// Publisher notification list: read/write work list for stewards.
/// Not cached- writes must be visible immediately.
/// </summary>
[OutputCache(NoStore = true)]
public class PublisherNotificationsController(
	IOptions<BigQueryOptions> bigQueryOptions,
	IOptions<ApiOptions> apiOptions)
	: MonitorControllerBase(bigQueryOptions, apiOptions)
{
	/// <summary>List publisher notification rows.</summary>
	[HttpGet("publisher-notifications")]
	[ProducesResponseType(typeof(AdminPage<PublisherNotificationRow>), StatusCodes.Status200OK)]
	public async Task<ActionResult<AdminPage<PublisherNotificationRow>>> List(
		int page = 1,
		int page_size = DefaultPageSize)
	{
		var table = Fq(Tables.PublisherNotifications);
		var rows = new List<PublisherNotificationRow>();

		await foreach (var row in await Query(PublisherNotificationsQuery.ListSql(table)))
		{
			rows.Add(PublisherNotificationsQuery.ParseRow(row));
		}

		var snapshotDate = DateOnly.FromDateTime(DateTime.UtcNow);
		return Ok(Paginate(rows, page, page_size, snapshotDate));
	}

	/// <summary>Summary counts for the publisher notification list.</summary>
	[HttpGet("publisher-notifications/summary")]
	[ProducesResponseType(typeof(AdminDocument<PublisherNotificationSummary>), StatusCodes.Status200OK)]
	public async Task<ActionResult<AdminDocument<PublisherNotificationSummary>>> Summary()
	{
		var table = Fq(Tables.PublisherNotifications);
		var row = await QuerySingle(PublisherNotificationsQuery.SummarySql(table));
		var summary = PublisherNotificationsQuery.ParseSummary(row);
		var snapshotDate = DateOnly.FromDateTime(DateTime.UtcNow);
		return Ok(Document(summary, snapshotDate));
	}

	/// <summary>Create a publisher notification row.</summary>
	[HttpPost("publisher-notifications")]
	[ProducesResponseType(typeof(AdminDocument<PublisherNotificationRow>), StatusCodes.Status201Created)]
	public async Task<ActionResult<AdminDocument<PublisherNotificationRow>>> Create(
		[FromBody] PublisherNotificationBody body)
	{
		var table = Fq(Tables.PublisherNotifications);

		await QuerySingle(
			PublisherNotificationsQuery.InsertSql(table),
			PublisherNotificationsQuery.InsertParameters(body));

		var row = await QuerySingle(
			PublisherNotificationsQuery.GetByIdSql(table),
			PublisherNotificationsQuery.IdParameter(body.Id));

		var parsed = PublisherNotificationsQuery.ParseRow(row!);
		var snapshotDate = DateOnly.FromDateTime(DateTime.UtcNow);
		return StatusCode(StatusCodes.Status201Created, Document(parsed, snapshotDate));
	}

	 /// <summary>Partially update a publisher notification row.</summary>
    /// <remarks>
    /// Only the fields sent are changed; a field that is missing or null keeps its current value, so a
    /// steward can change the status without re-sending the problems list. The id comes from the URL.
    /// A field cannot be cleared back to empty through this endpoint.
    /// </remarks>
	[HttpPatch("publisher-notifications/{id}")]
	[ProducesResponseType(typeof(AdminDocument<PublisherNotificationRow>), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<ActionResult<AdminDocument<PublisherNotificationRow>>> Update(
		string id,
		[FromBody] PublisherNotificationPatch body)
	{
		var table = Fq(Tables.PublisherNotifications);

		var existing = await QuerySingle(
			PublisherNotificationsQuery.GetByIdSql(table),
			PublisherNotificationsQuery.IdParameter(id));

		if (existing is null)
		{
			return NotFound(new { message = "Publisher notification not found." });
		}

		await QuerySingle(
			PublisherNotificationsQuery.UpdateSql(table),
			PublisherNotificationsQuery.UpdateParameters(id, body));

		var row = await QuerySingle(
			PublisherNotificationsQuery.GetByIdSql(table),
			PublisherNotificationsQuery.IdParameter(id));

		var parsed = PublisherNotificationsQuery.ParseRow(row!);
		var snapshotDate = DateOnly.FromDateTime(DateTime.UtcNow);
		return Ok(Document(parsed, snapshotDate));
	}

	/// <summary>Delete a publisher notification row.</summary>
	[HttpDelete("publisher-notifications/{id}")]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> Delete(string id)
	{
		var table = Fq(Tables.PublisherNotifications);
		var idParam = PublisherNotificationsQuery.IdParameter(id);

		var existing = await QuerySingle(
			PublisherNotificationsQuery.GetByIdSql(table),
			idParam);

		if (existing is null)
		{
			return NotFound(new { message = "Publisher notification not found." });
		}

		await QuerySingle(PublisherNotificationsQuery.DeleteSql(table), idParam);
		return NoContent();
	}


    /// <summary>Scan the monitors into the notification list.</summary>
    /// <remarks>
    /// Works out today's monitor actions, the same list <c>/admin/actions</c> returns, groups them by
    /// publisher and saves one row per publisher. New publishers get a new <c>open</c> row. For
    /// publishers already on the list only the problem fields are replaced: status, stakeholder,
    /// contact, date contacted and notes are never changed by a scan.
    ///
    /// Rows for publishers whose problems have gone away are left as they are. Problems whose
    /// publisher name isn't known are skipped and counted in <c>actions_skipped</c>.
    /// </remarks>
    [HttpPost("publisher-notifications/scan")]
    [ProducesResponseType(typeof(AdminDocument<PublisherScanSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult<AdminDocument<PublisherScanSummary>>> Scan()
    {
        var snapshotDate = await ResolveSnapshotDate(asOf: null);
        if (snapshotDate is null)
        {
            var empty = new PublisherScanSummary { PublishersSaved = 0, ActionsFound = 0, ActionsSkipped = 0 };
            return Ok(Document(empty, DateOnly.FromDateTime(DateTime.UtcNow)));
        }

        var rows = await LoadActionRows(snapshotDate.Value);
        var result = PublisherNotificationScan.Group(rows);

        if (result.Publishers.Count > 0)
        {
            await QuerySingle(
                PublisherNotificationsQuery.ScanMergeSql(Fq(Tables.PublisherNotifications)),
                PublisherNotificationsQuery.ScanMergeParameters(result.Publishers));
        }

        var summary = new PublisherScanSummary
        {
            PublishersSaved = result.Publishers.Count,
            ActionsFound = rows.Count,
            ActionsSkipped = result.SkippedActions,
        };

        return Ok(Document(summary, snapshotDate.Value));
    }
}
