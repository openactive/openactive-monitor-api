using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MonitorApi.Tests;

public class FeedQualityEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
	private readonly ApiFixture _fixture = fixture;

	[Fact]
	public async Task FeedQuality_ReturnsArrayOfRecords()
	{
		using var client = _fixture.CreateAuthenticatedClient();
		var response = await client.GetAsync(_fixture.WithToken("/feed-quality"));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var json = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(JsonValueKind.Array, json.ValueKind);

		foreach (var record in json.EnumerateArray())
		{
			Assert.True(record.TryGetProperty("dataset_name", out _));
			Assert.True(record.TryGetProperty("feed_url", out _));
			Assert.True(record.TryGetProperty("last_assessed", out _));
		}
	}
}
