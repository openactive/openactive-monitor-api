using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MonitorApi.Models.Admin;
using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.Stalls;

/// <summary>
/// Live-data tests for <c>/admin/dataset-stall-incidents</c>. The detection rules themselves are
/// pinned by <see cref="DatasetStallDetectorTests"/>; these check the wiring, the response envelope,
/// and the invariants that must hold whatever the data looks like on the day — including the one that
/// spans monitors, that this endpoint and the single-feed one never report the same silence twice.
/// </summary>
public class DatasetStallIncidentsEndpointTests(AdminApiFixture fixture) : IClassFixture<AdminApiFixture>
{
	private const string Route = "/admin/dataset-stall-incidents";

	private readonly AdminApiFixture _fixture = fixture;

	private static readonly JsonSerializerOptions JsonOptions =
		new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

	private async Task<AdminPage<DatasetStallIncident>> Get(string query = "") =>
		await GetAdmin<AdminPage<DatasetStallIncident>>(Route + query);

	private async Task<T> GetAdmin<T>(string route)
	{
		using var client = _fixture.CreateClient();
		var response = await client.GetAsync(_fixture.WithAdminToken(route));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;
	}

	[Fact]
	public async Task ReturnsTheDocumentedEnvelope()
	{
		var page = await Get();

		Assert.NotNull(page.Data);
		Assert.Equal(1, page.Meta.Page);
		Assert.Equal(500, page.Meta.PageSize);
		Assert.True(page.Meta.Total >= page.Data.Count);
		Assert.Equal(DateTimeKind.Utc, page.Meta.GeneratedAt.Kind);
		Assert.True(page.Meta.SnapshotDate > DateOnly.MinValue);
	}

	[Fact]
	public async Task EveryIncidentIsInternallyConsistent()
	{
		var page = await Get();

		Assert.All(page.Data, incident =>
		{
			Assert.Equal(DatasetStallDetector.MonitorId, incident.MonitorId);
			Assert.NotEmpty(incident.DatasetUrl);
			Assert.NotEmpty(incident.DatasetName);
			Assert.StartsWith("pub_", incident.PublisherId);
			Assert.Equal("open", incident.Status);
			Assert.Null(incident.LastContacted);

			// The incident opens the day the dataset goes quiet, so these three agree by construction.
			Assert.Equal(incident.FirstDetected, incident.Detail.LastModified);
			Assert.Equal(incident.ConsecutiveDays, incident.DaysOpen);
			Assert.Equal(incident.DaysOpen, page.Meta.SnapshotDate.DayNumber - incident.FirstDetected.DayNumber);

			// Default thresholds: open at five days, escalated at seven.
			Assert.True(incident.DaysOpen >= 5);
			Assert.True(incident.DaysOpen <= 120);
			Assert.Equal(incident.DaysOpen >= 7, incident.PastThreshold);

			// trend is the trailing ten days of raw dataset-wide "updated" totals, so it is a fixed
			// length whatever the age of the incident, and every value is either absent or a
			// non-negative count.
			Assert.Equal(10, incident.Trend.Count);
			Assert.All(incident.Trend, value => Assert.True(value is null || value >= 0));
		});
	}

	[Fact]
	public async Task EveryIncidentAccountsForAtLeastOneFeedAndAllOfThemAreSilent()
	{
		var page = await Get("?page_size=1000");

		Assert.All(page.Data, incident =>
		{
			Assert.True(incident.FeedCount >= 1);
			Assert.Equal(incident.FeedCount, incident.Detail.Feeds.Count);
			Assert.All(incident.Detail.Feeds, feed =>
			{
				Assert.NotEmpty(feed.FeedId);
				Assert.NotEmpty(feed.FeedName);
				// A feed cannot have been quieter for less time than the dataset it belongs to; one that
				// never published at all reports neither figure.
				Assert.True(feed.ConsecutiveDays is null || feed.ConsecutiveDays >= incident.DaysOpen);
				Assert.Equal(feed.LastPublished is null, feed.ConsecutiveDays is null);
			});

			// The dataset went quiet when its last remaining feed did, so exactly that feed leads the
			// list and dates the incident.
			Assert.Equal(incident.DaysOpen, incident.Detail.Feeds[0].ConsecutiveDays);
			Assert.Equal(incident.FirstDetected, incident.Detail.Feeds[0].LastPublished);

			var silence = incident.Detail.Feeds.Select(f => f.ConsecutiveDays ?? int.MaxValue).ToList();
			Assert.Equal(silence.Order(), silence);
		});
	}

	[Fact]
	public async Task TrendIsAlignedAcrossIncidentsAndShowsTheStallAsZeroes()
	{
		var page = await Get("?page_size=1000");

		// Every incident covers the same ten days, so the arrays line up column-for-column in the UI.
		Assert.All(page.Data, incident => Assert.Equal(10, incident.Trend.Count));

		// An incident is by definition silent across every feed for the last stall_days, so the final
		// entries cannot be positive: the dataset either published nothing (0) or was not polled (null).
		Assert.All(page.Data, incident =>
			Assert.All(incident.Trend.TakeLast(Math.Min(5, incident.DaysOpen)), value =>
				Assert.True(value is null or 0, $"{incident.DatasetUrl} published {value} while stalled")));
	}

	[Fact]
	public async Task EachDatasetIsReportedAtMostOnce()
	{
		var page = await Get("?page_size=1000");

		Assert.Equal(page.Data.Count, page.Data.Select(i => i.DatasetUrl).Distinct().Count());
	}

	[Fact]
	public async Task IncidentsAreOrderedLongestRunningFirst()
	{
		var page = await Get();
		var daysOpen = page.Data.Select(i => i.DaysOpen).ToList();

		Assert.Equal(daysOpen.OrderByDescending(d => d), daysOpen);
	}

	/// <summary>
	/// The reason this monitor exists: a feed whose whole dataset has gone dark is reported here, once,
	/// rather than by the single-feed monitor as one incident per feed. At equal thresholds the two
	/// endpoints partition the silence, so neither the datasets nor the feeds may overlap.
	/// </summary>
	[Fact]
	public async Task NoSilenceIsReportedByBothStallMonitors()
	{
		var datasetStalls = await Get("?page_size=1000");
		var singleFeedStalls = await GetAdmin<AdminPage<StallIncident>>(
			"/admin/single-feed-stall-incidents?page_size=1000");

		Assert.Equal(datasetStalls.Meta.SnapshotDate, singleFeedStalls.Meta.SnapshotDate);

		var stalledFeeds = datasetStalls.Data.SelectMany(i => i.Detail.Feeds).Select(f => f.FeedId).ToHashSet();

		Assert.Empty(stalledFeeds.Intersect(singleFeedStalls.Data.Select(i => i.FeedId)));
	}

	[Fact]
	public async Task PagesAreDisjointAndCoverTheWholeResultSet()
	{
		var first = await Get("?page=1&page_size=10");
		if (first.Meta.Total <= 10)
		{
			return;
		}

		var second = await Get("?page=2&page_size=10");

		Assert.Equal(10, first.Data.Count);
		Assert.Equal(first.Meta.Total, second.Meta.Total);
		Assert.Empty(first.Data.Select(i => i.DatasetUrl).Intersect(second.Data.Select(i => i.DatasetUrl)));
	}

	[Fact]
	public async Task PagingArgumentsAreClamped()
	{
		var page = await Get("?page=0&page_size=99999");

		Assert.Equal(1, page.Meta.Page);
		Assert.Equal(1000, page.Meta.PageSize);
	}

	[Fact]
	public async Task PageBeyondTheEnd_IsEmptyButStillReportsTheTotal()
	{
		var all = await Get("?page_size=1000");
		var beyond = await Get("?page=1000&page_size=10");

		Assert.Empty(beyond.Data);
		Assert.Equal(all.Meta.Total, beyond.Meta.Total);
	}

	[Fact]
	public async Task LoweringTheStallThreshold_NeverReportsFewerIncidents()
	{
		var strict = await Get("?stall_days=10");
		var loose = await Get("?stall_days=2");

		Assert.True(loose.Meta.Total >= strict.Meta.Total);
	}

	[Fact]
	public async Task RaisingTheEscalationThreshold_NeverFlagsMoreIncidents()
	{
		var low = await Get("?past_threshold_days=5");
		var high = await Get("?past_threshold_days=365");

		Assert.True(high.Data.Count(i => i.PastThreshold) <= low.Data.Count(i => i.PastThreshold));
		Assert.Equal(low.Meta.Total, high.Meta.Total);
	}

	[Fact]
	public async Task LookbackShorterThanTheStallThreshold_ReportsNothing()
	{
		// A dataset cannot both have published within three days and have been silent for five.
		var page = await Get("?lookback_days=3&stall_days=5");

		Assert.Empty(page.Data);
		Assert.Equal(0, page.Meta.Total);
	}

	[Fact]
	public async Task NeverMoreDatasetsThanTheEstateHolds()
	{
		var page = await Get("?page_size=1000");
		var summary = await GetAdmin<AdminDocument<AdminSummary>>("/admin/summary");

		Assert.True(page.Meta.Total <= summary.Data.Datasets);
	}

	[Fact]
	public async Task AsOf_MovesTheSnapshotDateAndRecomputesAgainstIt()
	{
		var latest = await Get();
		var asOf = latest.Meta.SnapshotDate.AddDays(-2);

		var earlier = await Get($"?as_of={asOf:yyyy-MM-dd}");

		Assert.Equal(asOf, earlier.Meta.SnapshotDate);
		Assert.All(earlier.Data, i => Assert.Equal(i.DaysOpen, asOf.DayNumber - i.FirstDetected.DayNumber));
	}
}
