using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MonitorApi.Admin.Tests;

/// <summary>
/// The admin surface publishes its own OpenAPI document and Scalar page, so the dashboard developer
/// gets a reference containing only the admin endpoints.
/// </summary>
public class AdminApiReferenceTests(AdminApiFixture fixture) : IClassFixture<AdminApiFixture>
{
	private readonly AdminApiFixture _fixture = fixture;

	[Theory]
	[InlineData("/openapi/admin.json")]
	[InlineData("/scalar/admin")]
	public async Task AdminReference_IsServedAndNotTokenGated(string path)
	{
		using var client = _fixture.CreateClient();
		var response = await client.GetAsync(path);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Fact]
	public async Task AdminDocument_ContainsEveryAdminEndpointAndNothingElse()
	{
		var paths = await AdminDocumentPaths();

		Assert.Contains("/admin/single-feed-stall-incidents", paths);
		Assert.Contains("/admin/single-feed-stall-trend", paths);
		Assert.Contains("/admin/feed-ingestion-error-incidents", paths);
		Assert.Contains("/admin/feed-ingestion-error-trend", paths);
		Assert.Contains("/admin/dataset-orphaned-children-incidents", paths);
		Assert.All(paths, path => Assert.StartsWith("/admin/", path));
	}

	[Theory]
	[InlineData("/admin/single-feed-stall-incidents", "page,page_size,lookback_days,stall_days,past_threshold_days,as_of")]
	[InlineData("/admin/feed-ingestion-error-incidents", "page,page_size,success_lookback_days,error_days,past_threshold_days,as_of")]
	[InlineData("/admin/feed-ingestion-error-trend", "page,page_size,trend_days,success_lookback_days,error_days,past_threshold_days,as_of")]
	// No date parameter of any kind: opportunities holds current state only, so a past date cannot be
	// answered and there is nothing to window.
	[InlineData("/admin/dataset-orphaned-children-incidents", "page,page_size,min_orphans,past_threshold_orphans")]
	public async Task AdminDocument_DocumentsTheQueryParametersWithTheirDefaults(string path, string expected)
	{
		using var client = _fixture.CreateClient();
		var document = await client.GetFromJsonAsync<JsonElement>("/openapi/admin.json");

		var parameters = document
			.GetProperty("paths")
			.GetProperty(path)
			.GetProperty("get")
			.GetProperty("parameters")
			.EnumerateArray()
			.Select(p => p.GetProperty("name").GetString())
			.ToList();

		Assert.Equal(expected.Split(','), parameters);
	}

	[Fact]
	public async Task AdminDocument_DescribesTheSharedEnvelopeInSnakeCase()
	{
		using var client = _fixture.CreateClient();
		var document = await client.GetFromJsonAsync<JsonElement>("/openapi/admin.json");

		var schemas = document.GetProperty("components").GetProperty("schemas");
		var incident = schemas.GetProperty("StallIncident").GetProperty("properties")
			.EnumerateObject().Select(p => p.Name).ToList();

		Assert.Contains("monitor_id", incident);
		Assert.Contains("past_threshold", incident);
		Assert.Contains("quality_score", incident);

		var error = schemas.GetProperty("IngestionErrorIncident").GetProperty("properties")
			.EnumerateObject().Select(p => p.Name).ToList();

		Assert.Contains("monitor_id", error);
		Assert.Contains("past_threshold", error);

		var orphans = schemas.GetProperty("OrphanedChildrenIncident").GetProperty("properties")
			.EnumerateObject().Select(p => p.Name).ToList();

		Assert.Contains("monitor_id", orphans);
		Assert.Contains("past_threshold", orphans);
		Assert.Contains("dataset_url", orphans);
		Assert.Contains("missing_parent_count", orphans);

		// The lean-model decision, made executable. This monitor is per-dataset and has no history, so
		// these are absent rather than present-and-always-null; re-adding them as nullable fields would
		// commit the API to narrowing them later, which is a breaking change.
		Assert.DoesNotContain("feed_id", orphans);
		Assert.DoesNotContain("days_open", orphans);
		Assert.DoesNotContain("consecutive_days", orphans);
		Assert.DoesNotContain("first_detected", orphans);
		Assert.DoesNotContain("trend", orphans);
		Assert.DoesNotContain("quality_score", orphans);

		var errorDetail = schemas.GetProperty("IngestionErrorIncidentDetail").GetProperty("properties")
			.EnumerateObject().Select(p => p.Name).ToList();

		Assert.Contains("error_code", errorDetail);
		Assert.Contains("error_message", errorDetail);
		Assert.Contains("last_completed", errorDetail);

		var meta = schemas.GetProperty("AdminPageMeta").GetProperty("properties")
			.EnumerateObject().Select(p => p.Name).ToList();

		Assert.Contains("snapshot_date", meta);
		Assert.Contains("page_size", meta);
	}

	private async Task<List<string>> AdminDocumentPaths()
	{
		using var client = _fixture.CreateClient();
		var document = await client.GetFromJsonAsync<JsonElement>("/openapi/admin.json");

		return document.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
	}
}
