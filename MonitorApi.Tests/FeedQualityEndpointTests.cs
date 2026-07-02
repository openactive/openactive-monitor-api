using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MonitorApi.Tests;

public class FeedQualityEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
	private readonly ApiFixture _fixture = fixture;

	private async Task<JsonElement[]> Get(string query)
	{
		using var client = _fixture.CreateAuthenticatedClient();
		var response = await client.GetAsync(_fixture.WithToken("/feed-quality" + query));
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var json = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(JsonValueKind.Array, json.ValueKind);
		return json.EnumerateArray().ToArray();
	}

	// Identifies a feed_quality row so filtered results can be compared against the unfiltered set.
	private static HashSet<string> Keys(IEnumerable<JsonElement> rows) =>
		rows.Select(r =>
			(r.TryGetProperty("dataset_url", out var d) ? d.GetString() : null) + "|" +
			(r.TryGetProperty("feed_url", out var f) ? f.GetString() : null) + "|" +
			(r.TryGetProperty("feed_type", out var t) ? t.GetString() : null))
		.ToHashSet();

	[Fact]
	public async Task FeedQuality_ReturnsArrayOfRecords()
	{
		var rows = await Get("");

		foreach (var record in rows)
		{
			Assert.True(record.TryGetProperty("dataset_name", out _));
			Assert.True(record.TryGetProperty("feed_url", out _));
			Assert.True(record.TryGetProperty("last_assessed", out _));
		}
	}

	[Fact]
	public async Task PublisherFilter_ReturnsNonEmptySubsetOfUnfiltered()
	{
		var unfiltered = await Get("");
		var filtered = await Get("?publisher=Active%20Hartlepool");

		Assert.NotEmpty(filtered);
		Assert.True(filtered.Length < unfiltered.Length);
		Assert.Subset(Keys(unfiltered), Keys(filtered));
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

		Assert.Subset(Keys(unfiltered), Keys(filtered));
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
	public async Task NhsTrustFilter_NonExistent_ReturnsEmpty()
	{
		Assert.Empty(await Get("?nhs_trust=__nope__"));
	}

	[Fact]
	public async Task MultiValueCountryFilter_ReturnsSupersetOfSingleValue()
	{
		var singleCountry = await Get("?country=E92000001");
		var multiCountry = await Get("?country=E92000001&country=W92000004");

		Assert.Subset(Keys(multiCountry), Keys(singleCountry));
	}

	[Fact]
	public async Task EmptyFilterStrings_AreIgnored()
	{
		var unfiltered = await Get("");
		var withEmpty = await Get("?publisher=&district=&region=&country=&activity=&organization=&nhs_trust=");

		Assert.Equal(unfiltered.Length, withEmpty.Length);
	}

	[Fact]
	public async Task TwoFilters_BothApplied()
	{
		// A valid country combined with a non-existent publisher yields no publishers in phase one.
		Assert.Empty(await Get("?country=E92000001&publisher=__nope__"));
	}
}
