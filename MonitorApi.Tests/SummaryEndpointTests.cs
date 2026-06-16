using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MonitorApi.Tests;

public class SummaryEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
	private readonly ApiFixture _fixture = fixture;

	[Fact]
	public async Task Summary_ReturnsAggregateCounts()
	{
		using var client = _fixture.CreateAuthenticatedClient();

		var response = await client.GetAsync(_fixture.WithToken("/summary"));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var json = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.True(json.GetProperty("number_of_opportunities").GetInt64() >= 0);
		Assert.True(json.GetProperty("number_of_publishers").GetInt64() >= 0);
		Assert.True(json.GetProperty("number_of_activities").GetInt64() >= 0);
		Assert.True(json.TryGetProperty("date", out _));
	}
}
