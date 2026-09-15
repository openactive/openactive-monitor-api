using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MonitorApi.Models.Admin;
using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.Supply;

/// <summary>
/// Live-data tests for <c>/admin/dataset-future-decline-incidents</c>. The detection rules themselves
/// are pinned by <see cref="DatasetFutureDeclineDetectorTests"/>; these check the wiring, the response
/// envelope, and the invariants that must hold whatever the data looks like on the day.
/// </summary>
public class DatasetFutureDeclineIncidentsEndpointTests(AdminApiFixture fixture) : IClassFixture<AdminApiFixture>
{
	private const string Route = "/admin/dataset-future-decline-incidents";

	private readonly AdminApiFixture _fixture = fixture;

	private static readonly JsonSerializerOptions JsonOptions =
		new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

	private async Task<AdminPage<DatasetFutureDeclineIncident>> Get(string query = "") =>
		await GetAdmin<AdminPage<DatasetFutureDeclineIncident>>(Route + query);

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
			Assert.Equal(DatasetFutureDeclineDetector.MonitorId, incident.MonitorId);
			Assert.NotEmpty(incident.DatasetUrl);
			Assert.NotEmpty(incident.DatasetName);
			Assert.StartsWith("pub_", incident.PublisherId);
			Assert.Equal("open", incident.Status);
			Assert.Null(incident.LastContacted);

			Assert.Equal(incident.ConsecutiveDays, incident.DaysOpen);
			Assert.Equal(incident.DaysOpen, page.Meta.SnapshotDate.DayNumber - incident.FirstDetected.DayNumber);

			// The decline is dated from inside the window it is measured over, so it can never claim to
			// have started before the window did.
			Assert.InRange(incident.DaysOpen, 0, incident.Detail.WindowDays - 1);
			Assert.Equal(5, incident.Detail.WindowDays);

			// trend is the trailing ten days of raw forward-supply totals, so it is a fixed length whatever
			// the age of the incident, and every value is either absent or a non-negative count.
			Assert.Equal(10, incident.Trend.Count);
			Assert.All(incident.Trend, value => Assert.True(value is null || value >= 0));
		});
	}

	[Fact]
	public async Task EveryIncidentAccountsForAtLeastOneFeedAndItsTotalsCoverExactlyThoseFeeds()
	{
		var page = await Get("?page_size=1000");

		Assert.All(page.Data, incident =>
		{
			Assert.True(incident.FeedCount >= 1);
			Assert.Equal(incident.FeedCount, incident.Detail.Feeds.Count);

			// The dataset's figures are the sum of the feeds it reports and nothing else, which is what
			// lets drop and drop_percent be read against each other.
			Assert.Equal(incident.Detail.Feeds.Sum(f => f.StartFuture), incident.Detail.StartTotal);
			Assert.Equal(incident.Detail.Feeds.Sum(f => f.CurrentFuture), incident.Detail.CurrentTotal);
			Assert.Equal(incident.Detail.StartTotal - incident.Detail.CurrentTotal, incident.Detail.Drop);

			Assert.All(incident.Detail.Feeds, feed =>
			{
				Assert.NotEmpty(feed.FeedId);
				Assert.NotEmpty(feed.FeedName);
				Assert.Equal(feed.StartFuture - feed.CurrentFuture, feed.Drop);
				// The minimum-supply floor is applied to the figure the window starts from.
				Assert.True(feed.StartFuture >= 50);
				// The dataset is dated from the earliest of its feeds' declines, so no feed can have been
				// falling for longer than the incident has been open.
				Assert.True(feed.ConsecutiveDecliningDays <= incident.DaysOpen);
				Assert.True(feed.UpdatedInWindow >= 0);
				Assert.True(feed.DeletesInWindow >= 0);
			});

			// Largest loss first, so the feed to look at is the one read first.
			var drops = incident.Detail.Feeds.Select(f => f.Drop).ToList();
			Assert.Equal(drops.OrderByDescending(d => d), drops);
		});
	}

	[Fact]
	public async Task EveryIncidentMatchesTheRuleItSaysFired()
	{
		var page = await Get("?page_size=1000");

		Assert.All(page.Data.SelectMany(i => i.Detail.Feeds), feed =>
		{
			Assert.Contains(feed.Reason, new[] { "monotonic_decline", "sharp_drop", "both" });

			// A sharp drop is exactly the claim that one step lost at least drop_percent; a monotonic
			// decline makes no claim about any single step, but it did fall, so something dropped.
			if (feed.Reason is "sharp_drop" or "both")
			{
				Assert.True(feed.LargestDailyDropPercent >= 10);
			}

			Assert.True(feed.LargestDailyDropPercent >= 0);
		});

		// The dataset's reason is the roll-up of its feeds': one kind throughout, or "both".
		Assert.All(page.Data, incident =>
		{
			var reasons = incident.Detail.Feeds.Select(f => f.Reason).Distinct().ToList();
			Assert.Equal(reasons.Count == 1 ? reasons[0] : "both", incident.Detail.Reason);
		});
	}

	/// <summary>
	/// The qualifying gate, as an invariant: nothing is reported unless it lost at least
	/// <c>qualify_drop_percent</c> across the longer window, or removed more than it added across the
	/// detection window.
	/// </summary>
	[Fact]
	public async Task EveryReportedFeedClearsTheQualifyingGate()
	{
		var page = await Get("?page_size=1000");

		Assert.All(page.Data, incident =>
		{
			Assert.Equal(10, incident.Detail.QualifyWindowDays);
			Assert.Equal(
				incident.Detail.Feeds.Sum(f => f.QualifyStartFuture), incident.Detail.QualifyStartTotal);

			Assert.All(incident.Detail.Feeds, feed =>
			{
				Assert.Equal(feed.UpdatedInWindow - feed.DeletesInWindow, feed.DeltaInWindow);

				// The qualifying window is a superset of the detection window ending on the same day, so
				// the longer look-back can never start from less supply than the shorter one.
				Assert.True(feed.QualifyStartFuture >= feed.StartFuture);

				Assert.True(
					feed.QualifyDropPercent >= 10 || feed.DeltaInWindow < 0,
					$"{feed.FeedId} qualified on neither clause: " +
					$"{feed.QualifyDropPercent}% over ten days, delta {feed.DeltaInWindow}");
			});
		});
	}

	[Fact]
	public async Task LoweringTheQualifyingDrop_NeverReportsFewerIncidents()
	{
		var strict = await Get("?qualify_drop_percent=90");
		var loose = await Get("?qualify_drop_percent=1");

		Assert.True(loose.Meta.Total >= strict.Meta.Total);
	}

	[Fact]
	public async Task PastThresholdFollowsTheNetPercentageRatherThanTheRawLoss()
	{
		var page = await Get("?page_size=1000");

		Assert.All(page.Data, incident =>
			Assert.Equal(incident.Detail.DropPercent >= 25, incident.PastThreshold));
	}

	[Fact]
	public async Task EachDatasetIsReportedAtMostOnce()
	{
		var page = await Get("?page_size=1000");

		Assert.Equal(page.Data.Count, page.Data.Select(i => i.DatasetUrl).Distinct().Count());
	}

	[Fact]
	public async Task IncidentsAreOrderedLargestLossFirst()
	{
		var page = await Get();
		var drops = page.Data.Select(i => i.Detail.Drop).ToList();

		Assert.Equal(drops.OrderByDescending(d => d), drops);
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
	public async Task LoweringTheDropThreshold_NeverReportsFewerIncidents()
	{
		var strict = await Get("?drop_percent=90");
		var loose = await Get("?drop_percent=2");

		Assert.True(loose.Meta.Total >= strict.Meta.Total);
	}

	[Fact]
	public async Task RaisingTheEscalationThreshold_NeverFlagsMoreIncidents()
	{
		var low = await Get("?past_threshold_drop_percent=10");
		var high = await Get("?past_threshold_drop_percent=100");

		Assert.True(high.Data.Count(i => i.PastThreshold) <= low.Data.Count(i => i.PastThreshold));
		Assert.Equal(low.Meta.Total, high.Meta.Total);
	}

	[Fact]
	public async Task RaisingTheMinimumSupplyBeyondTheEstate_ReportsNothing()
	{
		// No feed carries a billion future opportunities, so the floor excludes every one of them.
		var page = await Get("?min_future_opportunities=1000000000");

		Assert.Empty(page.Data);
		Assert.Equal(0, page.Meta.Total);
	}

	[Fact]
	public async Task AWindowTooShortToHoldAStep_ReportsNothing()
	{
		// One day holds at most one completed run, and a single observation is not a trend. The minimum
		// never falls below two observations precisely so this window reports nothing rather than
		// everything.
		var page = await Get("?window_days=1");

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
