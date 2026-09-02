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
		Assert.All(paths, path => Assert.StartsWith("/admin/", path));
	}

	[Fact]
	public async Task AdminDocument_DocumentsTheQueryParametersWithTheirDefaults()
	{
		using var client = _fixture.CreateClient();
		var document = await client.GetFromJsonAsync<JsonElement>("/openapi/admin.json");

		var parameters = document
			.GetProperty("paths")
			.GetProperty("/admin/single-feed-stall-incidents")
			.GetProperty("get")
			.GetProperty("parameters")
			.EnumerateArray()
			.Select(p => p.GetProperty("name").GetString())
			.ToList();

		Assert.Equal(
			["page", "page_size", "lookback_days", "stall_days", "past_threshold_days", "as_of"],
			parameters);
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
