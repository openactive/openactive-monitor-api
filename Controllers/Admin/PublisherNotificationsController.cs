using Google.Cloud.BigQuery.V2;
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
	: AdminControllerBase(bigQueryOptions, apiOptions)
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
			new BigQueryParameter("id", BigQueryDbType.String, body.Id));

		var parsed = PublisherNotificationsQuery.ParseRow(row!);
		var snapshotDate = DateOnly.FromDateTime(DateTime.UtcNow);
		return StatusCode(StatusCodes.Status201Created, Document(parsed, snapshotDate));
	}

	/// <summary>Update a publisher notification row. Id comes from the URL.</summary>
	[HttpPatch("publisher-notifications/{id}")]
	[ProducesResponseType(typeof(AdminDocument<PublisherNotificationRow>), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<ActionResult<AdminDocument<PublisherNotificationRow>>> Update(
		string id,
		[FromBody] PublisherNotificationBody body)
	{
		var table = Fq(Tables.PublisherNotifications);

		var existing = await QuerySingle(
			PublisherNotificationsQuery.GetByIdSql(table),
			new BigQueryParameter("id", BigQueryDbType.String, id));

		if (existing is null)
		{
			return NotFound(new { message = "Publisher notification not found." });
		}

		await QuerySingle(
			PublisherNotificationsQuery.UpdateSql(table),
			PublisherNotificationsQuery.UpdateParameters(id, body));

		var row = await QuerySingle(
			PublisherNotificationsQuery.GetByIdSql(table),
			new BigQueryParameter("id", BigQueryDbType.String, id));

		var parsed = PublisherNotificationsQuery.ParseRow(row!);
		var snapshotDate = DateOnly.FromDateTime(DateTime.UtcNow);
		return Ok(Document(parsed, snapshotDate));
	}
}
