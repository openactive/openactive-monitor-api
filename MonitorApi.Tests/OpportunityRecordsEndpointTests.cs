using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MonitorApi.Tests;

public class OpportunityRecordsEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
	private readonly ApiFixture _fixture = fixture;

	private async Task<JsonElement> Get(string query)
	{
		using var client = _fixture.CreateAuthenticatedClient();
		var response = await client.GetAsync(_fixture.WithToken("/opportunity-records" + query));
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return await response.Content.ReadFromJsonAsync<JsonElement>();
	}

	[Fact]
	public async Task NoFilters_ReturnsDefaultPage()
	{
		var json = await Get("");
		Assert.Equal(0, json.GetProperty("offset").GetInt32());
		Assert.Equal(20, json.GetProperty("limit").GetInt32());
		Assert.Equal(JsonValueKind.Array, json.GetProperty("items").ValueKind);
	}

	[Fact]
	public async Task LimitOver100_IsClampedTo100()
	{
		var json = await Get("?limit=9999");
		Assert.Equal(100, json.GetProperty("limit").GetInt32());
	}

	[Fact]
	public async Task LimitBelowOne_IsClampedToOne()
	{
		var json = await Get("?limit=0");
		Assert.Equal(1, json.GetProperty("limit").GetInt32());
	}

	[Fact]
	public async Task NegativeOffset_IsClampedToZero()
	{
		var json = await Get("?offset=-50");
		Assert.Equal(0, json.GetProperty("offset").GetInt32());
	}

	[Fact]
	public async Task PublisherFilter_AllRowsHaveThatPublisherName()
	{
		var unfiltered = await Get("?limit=5");
		var items = unfiltered.GetProperty("items");
		if (items.GetArrayLength() == 0) return;
		var publisher = items[0].GetProperty("publisher_name").GetString();

		var filtered = await Get($"?publisher={Uri.EscapeDataString(publisher!)}");

		Assert.All(filtered.GetProperty("items").EnumerateArray(), row =>
			Assert.Equal(publisher, row.GetProperty("publisher_name").GetString()));
	}

	[Fact]
	public async Task CountryFilter_AllRowsHaveThatCountryCode()
	{
		var filtered = await Get("?country=E92000001");

		Assert.All(filtered.GetProperty("items").EnumerateArray(), row =>
			Assert.Equal("E92000001", row.GetProperty("country_code").GetString()));
	}

	[Fact]
	public async Task DistrictFilter_AllRowsHaveThatDistrictCode()
	{
		var unfiltered = await Get("?limit=10");
		var district = unfiltered.GetProperty("items").EnumerateArray()
			.Select(r => r.TryGetProperty("district_code", out var d) ? d.GetString() : null)
			.FirstOrDefault(d => !string.IsNullOrEmpty(d));
		if (district is null) return;

		var filtered = await Get($"?district={Uri.EscapeDataString(district)}");

		Assert.All(filtered.GetProperty("items").EnumerateArray(), row =>
			Assert.Equal(district, row.GetProperty("district_code").GetString()));
	}

	[Fact]
	public async Task RegionFilter_AllRowsHaveThatRegionCode()
	{
		var unfiltered = await Get("?limit=10");
		var region = unfiltered.GetProperty("items").EnumerateArray()
			.Select(r => r.TryGetProperty("region_code", out var d) ? d.GetString() : null)
			.FirstOrDefault(d => !string.IsNullOrEmpty(d));
		if (region is null) return;

		var filtered = await Get($"?region={Uri.EscapeDataString(region)}");

		Assert.All(filtered.GetProperty("items").EnumerateArray(), row =>
			Assert.Equal(region, row.GetProperty("region_code").GetString()));
	}

	[Fact]
	public async Task ActivityFilter_ReturnsPage()
	{
		var json = await Get("?activity=Yoga");
		Assert.Equal(JsonValueKind.Array, json.GetProperty("items").ValueKind);
	}

	[Fact]
	public async Task NonExistentOrganization_ReturnsEmptyItems()
	{
		var json = await Get("?organization=__definitely_not_real__");
		Assert.Equal(0, json.GetProperty("items").GetArrayLength());
		Assert.False(json.GetProperty("has_more").GetBoolean());
	}

	[Fact]
	public async Task EmptyFilterStrings_AreIgnored()
	{
		var unfiltered = await Get("");
		var withEmpty = await Get("?publisher=&district=&country=");

		Assert.Equal(
			unfiltered.GetProperty("items").GetArrayLength(),
			withEmpty.GetProperty("items").GetArrayLength());
	}

	[Fact]
	public async Task TwoFilters_BothApplied()
	{
		var filtered = await Get("?country=E92000001&activity=Yoga");

		Assert.All(filtered.GetProperty("items").EnumerateArray(), row =>
			Assert.Equal("E92000001", row.GetProperty("country_code").GetString()));
	}
}
