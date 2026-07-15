using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MonitorApi.Tests;

public class SocioEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
	private readonly ApiFixture _fixture = fixture;

	private async Task<JsonElement> Get(string query)
	{
		using var client = _fixture.CreateAuthenticatedClient();
		var response = await client.GetAsync(_fixture.WithToken("/socio" + query));
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return await response.Content.ReadFromJsonAsync<JsonElement>();
	}

	[Fact]
	public async Task NoFilters_ReturnsArrayOfRecordsWithAreaCode()
	{
		var json = await Get("");
		Assert.Equal(JsonValueKind.Array, json.ValueKind);

		foreach (var record in json.EnumerateArray())
		{
			Assert.True(record.TryGetProperty("area_code", out var code));
			Assert.Equal(JsonValueKind.String, code.ValueKind);
			Assert.True(record.TryGetProperty("total_population", out _));
			Assert.True(record.TryGetProperty("als_active_rate", out _));
		}
	}

	[Fact]
	public async Task DistrictFilter_ReturnsOnlyMatchingAreaCode()
	{
		var json = await Get("?district=E09000003");
		Assert.Equal(JsonValueKind.Array, json.ValueKind);

		foreach (var record in json.EnumerateArray())
		{
			Assert.Equal("E09000003", record.GetProperty("area_code").GetString());
		}
	}

	[Fact]
	public async Task UnknownCode_ReturnsEmptyArray()
	{
		var json = await Get("?district=__definitely_not_real__");
		Assert.Equal(JsonValueKind.Array, json.ValueKind);
		Assert.Empty(json.EnumerateArray());
	}

	[Fact]
	public async Task MultiValueFilter_ReturnsSubsetOfSuppliedCodes()
	{
		var json = await Get("?district=E09000003&region=E12000007");
		Assert.Equal(JsonValueKind.Array, json.ValueKind);

		var allowed = new[] { "E09000003", "E12000007" };
		foreach (var record in json.EnumerateArray())
		{
			Assert.Contains(record.GetProperty("area_code").GetString(), allowed);
		}
	}

	[Fact]
	public async Task EmptyFilterStrings_AreIgnored()
	{
		var unfiltered = await Get("");
		var withEmpty = await Get("?district=&region=&country=");

		Assert.Equal(unfiltered.EnumerateArray().Count(), withEmpty.EnumerateArray().Count());
	}
}
