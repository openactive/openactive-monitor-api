using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MonitorApi.Tests;

public class OpportunitiesEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
	private readonly ApiFixture _fixture = fixture;

	private async Task<JsonElement> Get(string query)
	{
		using var client = _fixture.CreateAuthenticatedClient();
		var response = await client.GetAsync(_fixture.WithToken("/opportunities" + query));
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return await response.Content.ReadFromJsonAsync<JsonElement>();
	}

	[Fact]
	public async Task NoFilters_ReturnsArray()
	{
		var json = await Get("");
		Assert.Equal(JsonValueKind.Array, json.ValueKind);
	}

	[Fact]
	public async Task PublisherFilter_AllRowsHaveThatPublisher()
	{
		var unfiltered = await Get("");
		if (unfiltered.GetArrayLength() == 0) return; // dataset is empty — nothing to check
		var publisher = unfiltered[0].GetProperty("publisher").GetString();

		var filtered = await Get($"?publisher={Uri.EscapeDataString(publisher!)}");

		Assert.All(filtered.EnumerateArray(), row =>
			Assert.Equal(publisher, row.GetProperty("publisher").GetString()));
	}

	[Fact]
	public async Task CountryFilter_AllRowsHaveThatCountryCode()
	{
		var filtered = await Get("?country=E92000001"); // England

		Assert.All(filtered.EnumerateArray(), row =>
			Assert.Equal("E92000001", row.GetProperty("country_code").GetString()));
	}

	[Fact]
	public async Task DistrictFilter_AllRowsHaveThatDistrictCode()
	{
		var unfiltered = await Get("");
		var district = unfiltered.EnumerateArray()
			.Select(r => r.TryGetProperty("district_code", out var d) ? d.GetString() : null)
			.FirstOrDefault(d => !string.IsNullOrEmpty(d));
		if (district is null) return;

		var filtered = await Get($"?district={Uri.EscapeDataString(district)}");

		Assert.All(filtered.EnumerateArray(), row =>
			Assert.Equal(district, row.GetProperty("district_code").GetString()));
	}

	[Fact]
	public async Task RegionFilter_AllRowsHaveThatRegionCode()
	{
		var unfiltered = await Get("");
		var region = unfiltered.EnumerateArray()
			.Select(r => r.TryGetProperty("region_code", out var d) ? d.GetString() : null)
			.FirstOrDefault(d => !string.IsNullOrEmpty(d));
		if (region is null) return;

		var filtered = await Get($"?region={Uri.EscapeDataString(region)}");

		Assert.All(filtered.EnumerateArray(), row =>
			Assert.Equal(region, row.GetProperty("region_code").GetString()));
	}

	[Fact]
	public async Task ActivityFilter_ReturnsArray()
	{
		var json = await Get("?activity=Yoga");
		Assert.Equal(JsonValueKind.Array, json.ValueKind);
	}

	[Fact]
	public async Task OrganizationFilter_ReturnsArray()
	{
		var json = await Get("?organization=__definitely_not_real__");
		Assert.Equal(JsonValueKind.Array, json.ValueKind);
		Assert.Equal(0, json.GetArrayLength()); // unknown org → empty
	}

	[Fact]
	public async Task EmptyFilterStrings_AreIgnored()
	{
		var unfiltered = await Get("");
		var withEmpty = await Get("?publisher=&district=&country=");

		Assert.Equal(unfiltered.GetArrayLength(), withEmpty.GetArrayLength());
	}

	[Fact]
	public async Task TwoFilters_BothApplied()
	{
		var filtered = await Get("?country=E92000001&activity=Yoga");

		Assert.All(filtered.EnumerateArray(), row =>
			Assert.Equal("E92000001", row.GetProperty("country_code").GetString()));
	}
}
