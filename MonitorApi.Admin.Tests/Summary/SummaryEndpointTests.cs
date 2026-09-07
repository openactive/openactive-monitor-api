using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MonitorApi.Models.Admin;

namespace MonitorApi.Admin.Tests.Summary;

/// <summary>
/// Live-data tests for <c>/admin/summary</c>: the envelope, the internal consistency of the headline
/// figures, and their agreement with the monitor endpoints they aggregate.
/// </summary>
public class SummaryEndpointTests(AdminApiFixture fixture) : IClassFixture<AdminApiFixture>
{
	private const string Route = "/admin/summary";

	private readonly AdminApiFixture _fixture = fixture;

	private static readonly JsonSerializerOptions JsonOptions =
		new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

	private async Task<AdminDocument<AdminSummary>> Get()
	{
		using var client = _fixture.CreateClient();
		var response = await client.GetAsync(_fixture.WithAdminToken(Route));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return (await response.Content.ReadFromJsonAsync<AdminDocument<AdminSummary>>(JsonOptions))!;
	}

	private async Task<T> GetAdmin<T>(string route)
	{
		using var client = _fixture.CreateClient();
		var response = await client.GetAsync(_fixture.WithAdminToken(route));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;
	}

	[Fact]
	public async Task ReturnsASingleDocumentInTheStandardEnvelope()
	{
		var document = await Get();

		Assert.NotNull(document.Data);
		Assert.Equal(1, document.Meta.Total);
		Assert.Equal(1, document.Meta.Page);
		Assert.Equal(1, document.Meta.PageSize);
	}

	[Fact]
	public async Task SnapshotDateIsTheDayOfGeneratedAt()
	{
		var document = await Get();

		Assert.Equal(DateOnly.FromDateTime(document.Meta.GeneratedAt), document.Meta.SnapshotDate);
	}

	[Fact]
	public async Task CoverageIsNonEmptyAndOnePublisherIsOneDataset()
	{
		var summary = (await Get()).Data;

		Assert.True(summary.Datasets > 0);
		Assert.True(summary.Feeds > 0);
		Assert.Equal(summary.Datasets, summary.PublishersMonitored);
	}

	[Fact]
	public async Task PublishersWithIssuesIsNeverNegative()
	{
		var summary = (await Get()).Data;

		Assert.True(summary.PublishersWithIssues >= 0);
	}

	[Fact]
	public async Task CrossMonitorTotalsAreNotReportedYet()
	{
		var summary = (await Get()).Data;

		Assert.Null(summary.OpenIncidents);
		Assert.Null(summary.PastThreshold);
		Assert.Null(summary.OpenIncidentsDelta);
	}

	[Fact]
	public async Task ReportsTheSingleFeedStallMonitor()
	{
		var summary = (await Get()).Data;

		var monitor = Assert.Single(summary.Monitors, m => m.MonitorId == "single_feed_stall");

		Assert.InRange(monitor.Sparkline.Count, 1, 7);
		Assert.Equal(monitor.Count, monitor.Sparkline[^1]);
		Assert.True(monitor.PastThresholdCount <= monitor.Count);
	}

	[Fact]
	public async Task HeadlineDeltasAggregateTheMonitorDeltas()
	{
		var summary = (await Get()).Data;
		var trend = await GetAdmin<AdminPage<StallTrendPoint>>("/admin/single-feed-stall-trend?trend_days=7");

		var expectedOpenDelta = trend.Data[^1].OpenCount - trend.Data[^2].OpenCount;
		var expectedPastThresholdDelta = trend.Data[^1].PastThresholdCount - trend.Data[^2].PastThresholdCount;

		Assert.Equal(expectedOpenDelta, summary.PublishersWithIssuesDelta);
		Assert.Equal(expectedPastThresholdDelta, summary.PastThresholdDelta);
	}

	[Fact]
	public async Task MonitorCountAgreesWithItsOwnIncidentsEndpoint()
	{
		var summary = (await Get()).Data;
		var incidents = await GetAdmin<AdminPage<StallIncident>>("/admin/single-feed-stall-incidents?page_size=1000");

		var monitor = summary.Monitors.Single(m => m.MonitorId == "single_feed_stall");

		Assert.Equal(incidents.Meta.Total, monitor.Count);
		Assert.Equal(incidents.Data.Count(i => i.PastThreshold), monitor.PastThresholdCount);
	}

	[Fact]
	public async Task SparklineAgreesWithTheMonitorsTrendEndpoint()
	{
		var summary = (await Get()).Data;
		var trend = await GetAdmin<AdminPage<StallTrendPoint>>("/admin/single-feed-stall-trend?trend_days=7");

		var monitor = summary.Monitors.Single(m => m.MonitorId == "single_feed_stall");

		Assert.Equal(trend.Data.Select(p => p.OpenCount), monitor.Sparkline);
	}
}
