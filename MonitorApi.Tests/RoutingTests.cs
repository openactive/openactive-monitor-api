using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MonitorApi.Tests;

/// <summary>
/// Guards the route table itself. <see cref="ApiFixture"/> boots the real app, so an ambiguous or
/// missing route shows up here as a 500 rather than only in a browser.
/// </summary>
/// <remarks>
/// Both controllers implement <c>IActionFilter</c> to check their access token. Their filter methods are
/// public methods on a controller, so they must be marked <c>[NonAction]</c>; without that MVC routes
/// them as actions sharing the controller's own route template, and every request to that template
/// fails with <c>AmbiguousMatchException</c>. These tests pin that.
/// </remarks>
public class RoutingTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
	private readonly ApiFixture _fixture = fixture;

	[Fact]
	public async Task Root_RedirectsToTheApiReference()
	{
		using var client = _fixture.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		var response = await client.GetAsync("/");

		Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
		Assert.Equal("/scalar", response.Headers.Location?.OriginalString);
	}

	[Theory]
	[InlineData("/")]
	[InlineData("/admin")]
	public async Task ControllerRouteTemplates_AreNotAmbiguous(string path)
	{
		using var client = _fixture.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		var response = await client.GetAsync(path);

		Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
	}

	[Fact]
	public async Task ApiReference_IsServedAndNotTokenGated()
	{
		using var client = _fixture.CreateClient();

		var scalar = await client.GetAsync("/scalar");
		var openapi = await client.GetAsync("/openapi/v1.json");

		Assert.Equal(HttpStatusCode.OK, scalar.StatusCode);
		Assert.Equal(HttpStatusCode.OK, openapi.StatusCode);
	}

	[Fact]
	public async Task AnalyticsDocument_ContainsThePublicEndpointsAndNoAdminOnes()
	{
		var paths = await OpenApiPaths("v1");

		Assert.Contains("/summary", paths);
		Assert.Contains("/opportunities", paths);
		Assert.Contains("/feed-quality", paths);
		Assert.DoesNotContain(paths, path => path.StartsWith("/admin", StringComparison.Ordinal));
	}

	[Fact]
	public async Task AnalyticsDocument_DoesNotLeakAdminSchemas()
	{
		using var client = _fixture.CreateClient();
		var document = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");

		var schemas = document.GetProperty("components").GetProperty("schemas")
			.EnumerateObject().Select(p => p.Name).ToList();

		Assert.NotEmpty(schemas);
		Assert.DoesNotContain(schemas, name => name.Contains("Stall", StringComparison.Ordinal));
		Assert.DoesNotContain(schemas, name => name.Contains("AdminPage", StringComparison.Ordinal));
	}

	[Fact]
	public async Task BothDocumentsAreServedAndHaveDistinctTitles()
	{
		using var client = _fixture.CreateClient();

		var analytics = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
		var admin = await client.GetFromJsonAsync<JsonElement>("/openapi/admin.json");

		var analyticsTitle = analytics.GetProperty("info").GetProperty("title").GetString();
		var adminTitle = admin.GetProperty("info").GetProperty("title").GetString();

		Assert.False(string.IsNullOrWhiteSpace(analyticsTitle));
		Assert.False(string.IsNullOrWhiteSpace(adminTitle));
		Assert.NotEqual(analyticsTitle, adminTitle);
	}

	[Fact]
	public async Task CombinedReference_OffersBothDocumentsInTheSelector()
	{
		// "/scalar" is the combined reference: it must list both documents with their display titles.
		// The per-document deep links ("/scalar/v1", "/scalar/admin") intentionally scope to one each.
		using var client = _fixture.CreateClient();
		var html = await client.GetStringAsync("/scalar");

		Assert.Contains("openapi/v1.json", html);
		Assert.Contains("openapi/admin.json", html);
		Assert.Contains("Analytics API", html);
		Assert.Contains("Admin API", html);
	}

	private async Task<List<string>> OpenApiPaths(string documentName)
	{
		using var client = _fixture.CreateClient();
		var document = await client.GetFromJsonAsync<JsonElement>($"/openapi/{documentName}.json");

		return document.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
	}
}
