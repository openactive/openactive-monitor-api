using System.Net;
using System.Net.Http.Json;

namespace MonitorApi.Tests;

public class OrganizationsEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
	private readonly ApiFixture _fixture = fixture;

	private async Task<string[]> Get(string query)
	{
		using var client = _fixture.CreateAuthenticatedClient();
		var response = await client.GetAsync(_fixture.WithToken("/organizations" + query));
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return (await response.Content.ReadFromJsonAsync<string[]>())!;
	}

	[Fact]
	public async Task NoFilters_ReturnsSortedDistinctList()
	{
		var organizations = await Get("");

		Assert.Equal(organizations.Distinct().Count(), organizations.Length);
		var sorted = organizations.OrderBy(o => o, StringComparer.OrdinalIgnoreCase).ToArray();
		Assert.Equal(sorted, organizations);
	}

	[Fact]
	public async Task PublisherFilter_NonExistent_ReturnsEmpty()
	{
		Assert.Empty(await Get("?publisher=__nope__"));
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
		Assert.Empty(await Get("?district=__nope__"));
	}

	[Fact]
	public async Task RegionFilter_NonExistent_ReturnsEmpty()
	{
		Assert.Empty(await Get("?region=__nope__"));
	}

	[Fact]
	public async Task ActivityFilter_ReturnsSubsetOfUnfiltered()
	{
		var unfiltered = await Get("");
		var filtered = await Get("?activity=Yoga");

		Assert.Subset(unfiltered.ToHashSet(), filtered.ToHashSet());
	}

	[Fact]
	public async Task EmptyFilterStrings_AreIgnored()
	{
		var unfiltered = await Get("");
		var withEmpty = await Get("?publisher=&district=&country=");

		Assert.Equal(unfiltered.Length, withEmpty.Length);
	}

	[Fact]
	public async Task TwoFilters_BothApplied()
	{
		var filtered = await Get("?country=E92000001&publisher=__nope__");
		Assert.Empty(filtered);
	}

	[Fact]
	public async Task MultiValueCountryFilter_ReturnsSupersetOfSingleValue()
	{
		var singleCountry = await Get("?country=E92000001");
		var multiCountry = await Get("?country=E92000001&country=W92000004");

		Assert.Subset(multiCountry.ToHashSet(), singleCountry.ToHashSet());
	}
}
