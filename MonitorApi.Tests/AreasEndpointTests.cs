using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MonitorApi.Tests;

public class AreasEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
	private readonly ApiFixture _fixture = fixture;

	private async Task<JsonElement> Get(string query)
	{
		using var client = _fixture.CreateAuthenticatedClient();
		var response = await client.GetAsync(_fixture.WithToken("/areas" + query));
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return await response.Content.ReadFromJsonAsync<JsonElement>();
	}

	[Fact]
	public async Task NoFilters_ReturnsCountriesAsKeys()
	{
		var json = await Get("");
		Assert.Equal(JsonValueKind.Object, json.ValueKind);

		foreach (var country in json.EnumerateObject())
		{
			Assert.True(country.Value.TryGetProperty("country_code", out _),
				$"Country '{country.Name}' is missing country_code");
		}
	}

	[Fact]
	public async Task PublisherFilter_ReturnsObject()
	{
		var json = await Get("?publisher=__definitely_not_real__");
		Assert.Equal(JsonValueKind.Object, json.ValueKind);
		Assert.Empty(json.EnumerateObject()); // unknown publisher → no countries
	}

	[Fact]
	public async Task ActivityFilter_ReturnsObject()
	{
		var json = await Get("?activity=Yoga");
		Assert.Equal(JsonValueKind.Object, json.ValueKind);
	}

	[Fact]
	public async Task OrganizationFilter_ReturnsObject()
	{
		var json = await Get("?organization=__definitely_not_real__");
		Assert.Empty(json.EnumerateObject());
	}

	[Fact]
	public async Task EmptyFilterStrings_AreIgnored()
	{
		var unfiltered = await Get("");
		var withEmpty = await Get("?publisher=&organization=");

		Assert.Equal(unfiltered.EnumerateObject().Count(), withEmpty.EnumerateObject().Count());
	}

	[Fact]
	public async Task TwoFilters_BothApplied()
	{
		var json = await Get("?publisher=__nope__&activity=Yoga");
		Assert.Empty(json.EnumerateObject());
	}
}
