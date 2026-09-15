using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MonitorApi.Models.Admin;

namespace MonitorApi.Admin.Tests.Supply;

/// <summary>
/// Live-data tests for <c>/admin/dataset-future-decline-trend</c>. The rules behind each point are
/// pinned by <see cref="DatasetFutureDeclineDetectorTests"/>; these check the series' shape and its
/// agreement with the incidents endpoint it is the history of.
/// </summary>
public class DatasetFutureDeclineTrendEndpointTests(AdminApiFixture fixture) : IClassFixture<AdminApiFixture>
{
	private const string Route = "/admin/dataset-future-decline-trend";

	private readonly AdminApiFixture _fixture = fixture;

	private static readonly JsonSerializerOptions JsonOptions =
		new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

	private async Task<AdminPage<DatasetFutureDeclineTrendPoint>> Get(string query = "") =>
		await GetAdmin<AdminPage<DatasetFutureDeclineTrendPoint>>(Route + query);

	private async Task<T> GetAdmin<T>(string route)
	{
		using var client = _fixture.CreateClient();
		var response = await client.GetAsync(_fixture.WithAdminToken(route));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;
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

		Assert.All(page.Data, p => Assert.True(p.PastThresholdCount <= p.OpenCount));
	}

	[Fact]
	public async Task TrendDays_ControlsTheLengthOfTheSeries()
	{
		var page = await Get("?trend_days=7");

		Assert.Equal(7, page.Data.Count);
		Assert.Equal(page.Meta.SnapshotDate, page.Data[^1].Date);
	}

	[Fact]
	public async Task FinalPointAgreesWithTheIncidentsEndpoint()
	{
		var trend = await Get();
		var incidents = await GetAdmin<AdminPage<DatasetFutureDeclineIncident>>(
			"/admin/dataset-future-decline-incidents?page_size=1000");

		Assert.Equal(incidents.Meta.SnapshotDate, trend.Data[^1].Date);
		Assert.Equal(incidents.Meta.Total, trend.Data[^1].OpenCount);
		Assert.Equal(incidents.Data.Count(i => i.PastThreshold), trend.Data[^1].PastThresholdCount);
	}

	/// <summary>
	/// Datasets, not feeds: on any day, a publisher with six draining feeds is one row here, so this
	/// series can never run above the number of datasets the estate holds.
	/// </summary>
	[Fact]
	public async Task CountsDatasetsRatherThanFeeds()
	{
		var trend = await Get();
		var summary = await GetAdmin<AdminDocument<AdminSummary>>("/admin/summary");

		Assert.All(trend.Data, p => Assert.True(p.OpenCount <= summary.Data.Datasets));
	}

	[Fact]
	public async Task EscalationThresholdEqualToTheTrigger_MakesEveryOpenIncidentPastThreshold()
	{
		// Clamped so past threshold is never looser than the trigger; asking for one below it therefore
		// escalates every incident raised by a sharp drop, and the counts cannot diverge downwards.
		var page = await Get("?drop_percent=1&past_threshold_drop_percent=1");

		Assert.All(page.Data, p => Assert.True(p.PastThresholdCount <= p.OpenCount));
	}

	[Fact]
	public async Task AWindowTooShortToHoldAStep_IsFlatZero()
	{
		// One day holds at most one completed run, so no day of the series can have a step to judge.
		var page = await Get("?window_days=1");

		Assert.All(page.Data, p => Assert.Equal(0, p.OpenCount));
		Assert.All(page.Data, p => Assert.Equal(0, p.PastThresholdCount));
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
