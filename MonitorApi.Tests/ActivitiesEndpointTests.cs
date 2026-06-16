using System.Net;
using System.Net.Http.Json;

namespace MonitorApi.Tests;

public class ActivitiesEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
	private readonly ApiFixture _fixture = fixture;

	private async Task<string[]> Get(string query)
	{
		using var client = _fixture.CreateAuthenticatedClient();
		var response = await client.GetAsync(_fixture.WithToken("/activities" + query));
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return (await response.Content.ReadFromJsonAsync<string[]>())!;
	}

	[Fact]
	public async Task NoFilters_ReturnsSortedDistinctList()
	{
		var activities = await Get("");

		Assert.Equal(activities.Distinct().Count(), activities.Length);
		var sorted = activities.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToArray();
		Assert.Equal(sorted, activities);
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
	public async Task OrganizationFilter_NonExistent_ReturnsEmpty()
	{
		Assert.Empty(await Get("?organization=__nope__"));
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
		var unfiltered = await Get("");
		var filtered = await Get("?country=E92000001&publisher=__nope__");

		Assert.Empty(filtered);
		Assert.NotEmpty(unfiltered); // sanity: unfiltered isn't itself empty
	}
}
