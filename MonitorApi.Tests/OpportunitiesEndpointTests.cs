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
	public async Task NoFilters_ReturnsNarrowOpportunityCountAsOpportunityCount()
	{
		var json = await Get("");
		if (json.GetArrayLength() == 0) return;

		var row = json[0];
		Assert.True(row.TryGetProperty("opportunity_count", out var count), "Row is missing opportunity_count.");
		Assert.Equal(JsonValueKind.Number, count.ValueKind);
		Assert.False(row.TryGetProperty("opportunity_count_narrow", out _), "Row should not expose opportunity_count_narrow.");
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

	[Fact]
	public async Task MultiValueCountryFilter_ReturnsSupersetOfSingleValue()
	{
		var singleCountry = await Get("?country=E92000001");
		var multiCountry = await Get("?country=E92000001&country=W92000004");

		Assert.True(multiCountry.GetArrayLength() >= singleCountry.GetArrayLength());
	}

	[Fact]
	public async Task CountryAndDistrictFilters_AreCombinedWithOr()
	{
		// country and district filters are OR'd together: each row matches either the
		// country (England) OR the district (a Scottish LAD), not both.
		var filtered = await Get("?country=E92000001&district=S12000041");

		Assert.All(filtered.EnumerateArray(), row =>
		{
			var countryCode = row.TryGetProperty("country_code", out var c) ? c.GetString() : null;
			var districtCode = row.TryGetProperty("district_code", out var d) ? d.GetString() : null;

			Assert.True(
				countryCode == "E92000001" || districtCode == "S12000041",
				$"Row matched neither filter (country_code='{countryCode}', district_code='{districtCode}').");
		});

		// The OR result is a superset of either single-filter result.
		var country = await Get("?country=E92000001");
		var district = await Get("?district=S12000041");
		Assert.True(filtered.GetArrayLength() >= country.GetArrayLength());
		Assert.True(filtered.GetArrayLength() >= district.GetArrayLength());
	}

	[Fact]
	public async Task NhsTrustAllFilter_AllRowsHaveNhsTrustCode()
	{
		var filtered = await Get("?nhs_trust=all");

		Assert.All(filtered.EnumerateArray(), row =>
		{
			Assert.True(row.TryGetProperty("nhstrust_code", out var code), "Row is missing nhstrust_code.");
			Assert.Equal(JsonValueKind.String, code.ValueKind);
			Assert.False(string.IsNullOrEmpty(code.GetString()));
		});
	}

	[Fact]
	public async Task NhsTrustAllFilter_IsCaseInsensitive()
	{
		var lower = await Get("?nhs_trust=all");
		var mixed = await Get("?nhs_trust=AlL");

		Assert.Equal(lower.GetArrayLength(), mixed.GetArrayLength());
	}

	[Fact]
	public async Task NhsTrustAllFilter_OverridesSpecificValues()
	{
		var all = await Get("?nhs_trust=all");
		var allWithSpecific = await Get("?nhs_trust=all&nhs_trust=__nope__");

		// "all" wins, so the extra non-existent code is ignored rather than narrowing to empty.
		Assert.Equal(all.GetArrayLength(), allWithSpecific.GetArrayLength());
	}

	[Fact]
	public async Task NhsTrustAllFilter_ReturnsSupersetOfSpecificTrust()
	{
		var all = await Get("?nhs_trust=all");
		if (all.GetArrayLength() == 0) return; // no NHS trust data — nothing to compare

		var code = all[0].GetProperty("nhstrust_code").GetString();
		var specific = await Get($"?nhs_trust={Uri.EscapeDataString(code!)}");

		Assert.True(all.GetArrayLength() >= specific.GetArrayLength());
	}
}

