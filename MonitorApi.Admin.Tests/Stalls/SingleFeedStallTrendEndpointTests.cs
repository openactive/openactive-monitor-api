using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MonitorApi.Models.Admin;

namespace MonitorApi.Admin.Tests.Stalls;

/// <summary>
/// Live-data tests for <c>/admin/single-feed-stall-trend</c>, including the cross-endpoint invariant
/// that the final trend point agrees with what the incidents endpoint reports for the same day.
/// </summary>
public class SingleFeedStallTrendEndpointTests(AdminApiFixture fixture) : IClassFixture<AdminApiFixture>
{
	private const string Route = "/admin/single-feed-stall-trend";

	private readonly AdminApiFixture _fixture = fixture;

	private static readonly JsonSerializerOptions JsonOptions =
		new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

	private async Task<AdminPage<StallTrendPoint>> Get(string query = "")
	{
		using var client = _fixture.CreateClient();
		var response = await client.GetAsync(_fixture.WithAdminToken(Route + query));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return (await response.Content.ReadFromJsonAsync<AdminPage<StallTrendPoint>>(JsonOptions))!;
	}

	[Fact]
	public async Task ReturnsThirtyDaysByDefault()
	{
		var page = await Get();

		Assert.Equal(30, page.Data.Count);
		Assert.Equal(30, page.Meta.Total);
		Assert.Equal(1, page.Meta.Page);
	}

	[Fact]
	public async Task PointsAreOneContiguousDayApartOldestFirstEndingAtTheSnapshot()
	{
		var page = await Get();

		Assert.Equal(page.Meta.SnapshotDate, page.Data[^1].Date);
		Assert.Equal(page.Meta.SnapshotDate.AddDays(-(page.Data.Count - 1)), page.Data[0].Date);

		for (var i = 1; i < page.Data.Count; i++)
		{
			Assert.Equal(page.Data[i - 1].Date.AddDays(1), page.Data[i].Date);
		}
	}

	[Fact]
	public async Task PastThresholdCountIsAlwaysASubsetOfOpenCount()
	{
		var page = await Get();

		Assert.All(page.Data, p =>
		{
			Assert.True(p.OpenCount >= 0);
			Assert.True(p.PastThresholdCount <= p.OpenCount);
		});
	}

	[Fact]
	public async Task TrendDays_ControlsTheLengthOfTheSeries()
	{
		var page = await Get("?trend_days=7");

		Assert.Equal(7, page.Data.Count);
		Assert.Equal(7, page.Meta.Total);
	}

	[Fact]
	public async Task FinalPointAgreesWithTheIncidentsEndpoint()
	{
		var trend = await Get();

		using var client = _fixture.CreateClient();
		var response = await client.GetAsync(
			_fixture.WithAdminToken("/admin/single-feed-stall-incidents?page_size=1000"));
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var incidents = (await response.Content.ReadFromJsonAsync<AdminPage<StallIncident>>(JsonOptions))!;

		Assert.Equal(incidents.Meta.SnapshotDate, trend.Data[^1].Date);
		Assert.Equal(incidents.Meta.Total, trend.Data[^1].OpenCount);
		Assert.Equal(incidents.Data.Count(i => i.PastThreshold), trend.Data[^1].PastThresholdCount);
	}

	[Fact]
	public async Task EscalationThresholdEqualToTheStallThreshold_MakesEveryOpenIncidentPastThreshold()
	{
		var page = await Get("?stall_days=5&past_threshold_days=5");

		Assert.All(page.Data, p => Assert.Equal(p.OpenCount, p.PastThresholdCount));
	}

	[Fact]
	public async Task AsOf_EndsTheSeriesOnTheRequestedDay()
	{
		var latest = await Get();
		var asOf = latest.Meta.SnapshotDate.AddDays(-3);

		var earlier = await Get($"?as_of={asOf:yyyy-MM-dd}");

		Assert.Equal(asOf, earlier.Meta.SnapshotDate);
		Assert.Equal(asOf, earlier.Data[^1].Date);
	}

	[Fact]
	public async Task PagingArgumentsAreClamped()
	{
		var page = await Get("?page=0&page_size=99999");

		Assert.Equal(1, page.Meta.Page);
		Assert.Equal(1000, page.Meta.PageSize);
	}
}
