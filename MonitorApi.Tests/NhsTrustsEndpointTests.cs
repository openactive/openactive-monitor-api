using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MonitorApi.Tests;

public class NhsTrustsEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
	private readonly ApiFixture _fixture = fixture;

	private async Task<string[]> Get(string query)
	{
		using var client = _fixture.CreateAuthenticatedClient();
		var response = await client.GetAsync(_fixture.WithToken("/nhs-trusts" + query));
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return (await response.Content.ReadFromJsonAsync<string[]>())!;
	}

	[Fact]
	public async Task NoFilters_ReturnsSortedDistinctList()
	{
		var trusts = await Get("");

		Assert.Equal(trusts.Distinct().Count(), trusts.Length);
		var sorted = trusts.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToArray();
		Assert.Equal(sorted, trusts);
	}

	[Fact]
	public async Task CountryFilter_ReturnsSubsetOfUnfiltered()
	{
		var unfiltered = await Get("");
		var filtered = await Get("?country=E92000001");

		Assert.Subset(unfiltered.ToHashSet(), filtered.ToHashSet());
	}

	[Fact]
	public async Task DistrictFilter_NonExistent_ReturnsEmpty()
	{
		var json = await Get("?district=__nope__");
		Assert.Empty(json);
	}

	[Fact]
	public async Task RegionFilter_NonExistent_ReturnsEmpty()
	{
		var json = await Get("?region=__nope__");
		Assert.Empty(json);
	}

	[Fact]
	public async Task ActivityFilter_ReturnsSubsetOfUnfiltered()
	{
		var unfiltered = await Get("");
		var filtered = await Get("?activity=Yoga");

		Assert.Subset(unfiltered.ToHashSet(), filtered.ToHashSet());
	}

	[Fact]
	public async Task OrganizationFilter_NonExistent_ReturnsEmpty()
	{
		var json = await Get("?organization=__nope__");
		Assert.Empty(json);
	}

	[Fact]
	public async Task PublisherFilter_NonExistent_ReturnsEmpty()
	{
		var json = await Get("?publisher=__nope__");
		Assert.Empty(json);
	}

	[Fact]
	public async Task EmptyFilterStrings_AreIgnored()
	{
		var unfiltered = await Get("");
		var withEmpty = await Get("?district=&region=&country=&organization=&publisher=");

		Assert.Equal(unfiltered.Length, withEmpty.Length);
	}

	[Fact]
	public async Task TwoFilters_BothApplied()
	{
		var byCountry = await Get("?country=E92000001");
		var byActivity = await Get("?activity=Yoga");
		var byBoth = await Get("?country=E92000001&activity=Yoga");

		Assert.Subset(byCountry.ToHashSet(), byBoth.ToHashSet());
		Assert.Subset(byActivity.ToHashSet(), byBoth.ToHashSet());
	}

	[Fact]
	public async Task MultiValueCountryFilter_ReturnsSupersetOfSingleValue()
	{
		var singleCountry = await Get("?country=E92000001");
		var multiCountry = await Get("?country=E92000001&country=W92000004");

		Assert.Subset(multiCountry.ToHashSet(), singleCountry.ToHashSet());
	}
}
