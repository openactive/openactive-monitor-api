using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MonitorApi.Models.Admin;
using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.Errors;

/// <summary>
/// Live-data tests for <c>/admin/feed-ingestion-error-incidents</c>. The detection rules themselves are
/// pinned by <see cref="FeedIngestionErrorDetectorTests"/>; these check the wiring, the response
/// envelope and the invariants that must hold whatever the data looks like on the day.
/// </summary>
public class FeedIngestionErrorIncidentsEndpointTests(AdminApiFixture fixture) : IClassFixture<AdminApiFixture>
{
	private const string Route = "/admin/feed-ingestion-error-incidents";

	private readonly AdminApiFixture _fixture = fixture;

	private static readonly JsonSerializerOptions JsonOptions =
		new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

	private async Task<AdminPage<IngestionErrorIncident>> Get(string query = "")
	{
		using var client = _fixture.CreateClient();
		var response = await client.GetAsync(_fixture.WithAdminToken(Route + query));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return (await response.Content.ReadFromJsonAsync<AdminPage<IngestionErrorIncident>>(JsonOptions))!;
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
			Assert.Equal(FeedIngestionErrorDetector.MonitorId, incident.MonitorId);
			Assert.NotEmpty(incident.FeedId);
			Assert.NotEmpty(incident.FeedName);
			Assert.StartsWith("pub_", incident.PublisherId);
			Assert.Equal("open", incident.Status);
			Assert.Null(incident.LastContacted);

			// The incident opens the day after the last completed ingestion, so these agree by
			// construction.
			Assert.Equal(incident.FirstDetected, incident.Detail.LastCompleted.AddDays(1));
			Assert.Equal(incident.ConsecutiveDays, incident.DaysOpen);
			Assert.Equal(
				incident.DaysOpen,
				page.Meta.SnapshotDate.DayNumber - incident.Detail.LastCompleted.DayNumber);

			// Default thresholds: open on the first failing day, escalated at three, ignored past fifteen.
			Assert.True(incident.DaysOpen >= 1);
			Assert.True(incident.DaysOpen <= 15);
			Assert.Equal(incident.DaysOpen >= 3, incident.PastThreshold);
		});
	}

	[Fact]
	public async Task AuthorisationFailuresAreLeftToTheirOwnMonitor()
	{
		var page = await Get("?page_size=1000");

		Assert.All(page.Data, incident =>
			Assert.DoesNotContain(
				incident.Detail.ErrorCode ?? "",
				FeedIngestionErrorDetector.AuthErrorCodes));
	}

	[Fact]
	public async Task ErrorCodeAndMessageAreEitherBothPresentOrBothAbsent()
	{
		// They are read from the same failing run, so a code without a message would mean the query has
		// stopped pairing them.
		var page = await Get("?page_size=1000");

		Assert.All(page.Data, incident =>
			Assert.Equal(
				incident.Detail.ErrorCode is null,
				string.IsNullOrEmpty(incident.Detail.ErrorMessage)));
	}

	[Fact]
	public async Task TrendIsAlignedAcrossIncidentsAndEndsOnAFailingDay()
	{
		var page = await Get("?page_size=1000");

		// Every incident covers the same ten days, so the arrays line up column-for-column in the UI.
		Assert.All(page.Data, incident => Assert.Equal(10, incident.Trend.Count));

		Assert.All(page.Data, incident =>
		{
			// The snapshot day failed — that is what opened the incident.
			Assert.Equal(1, incident.Trend[^1]);

			// A flag per day: 1 for a failing day, 0 for anything else.
			Assert.All(incident.Trend, flag => Assert.True(flag is 0 or 1, $"unexpected trend value {flag}"));

			// The day the feed last completed is not a failing day, so wherever it falls inside the
			// window it reads 0. This is what ties trend, days_open and detail.last_completed together.
			var lastCompletedIndex = incident.Trend.Count - 1 - incident.DaysOpen;
			if (lastCompletedIndex >= 0)
			{
				Assert.Equal(0, incident.Trend[lastCompletedIndex]);
			}
		});
	}

	[Fact]
	public async Task EachFeedIsReportedAtMostOnce()
	{
		var page = await Get("?page_size=1000");

		Assert.Equal(page.Data.Count, page.Data.Select(i => i.FeedId).Distinct().Count());
	}

	[Fact]
	public async Task IncidentsAreOrderedLongestFailingFirst()
	{
		var page = await Get();
		var daysOpen = page.Data.Select(i => i.DaysOpen).ToList();

		Assert.Equal(daysOpen.OrderByDescending(d => d), daysOpen);
	}

	[Fact]
	public async Task PagesAreDisjointAndCoverTheWholeResultSet()
	{
		var first = await Get("?page=1&page_size=2");
		if (first.Meta.Total <= 2)
		{
			return;
		}

		var second = await Get("?page=2&page_size=2");

		Assert.Equal(2, first.Data.Count);
		Assert.Equal(first.Meta.Total, second.Meta.Total);
		Assert.Empty(first.Data.Select(i => i.FeedId).Intersect(second.Data.Select(i => i.FeedId)));
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
	public async Task WideningTheSuccessLookback_NeverReportsFewerIncidents()
	{
		// A longer lookback can only rescue more feeds from being written off as permanently broken.
		var narrow = await Get("?success_lookback_days=2");
		var wide = await Get("?success_lookback_days=60");

		Assert.True(wide.Meta.Total >= narrow.Meta.Total);
	}

	[Fact]
	public async Task RaisingTheFailureThreshold_NeverReportsMoreIncidents()
	{
		var loose = await Get("?error_days=1");
		var strict = await Get("?error_days=10");

		Assert.True(strict.Meta.Total <= loose.Meta.Total);
	}

	[Fact]
	public async Task RaisingTheEscalationThreshold_NeverFlagsMoreIncidents()
	{
		var low = await Get("?past_threshold_days=1&page_size=1000");
		var high = await Get("?past_threshold_days=365&page_size=1000");

		Assert.True(high.Data.Count(i => i.PastThreshold) <= low.Data.Count(i => i.PastThreshold));
		Assert.Equal(low.Meta.Total, high.Meta.Total);
	}

	[Fact]
	public async Task EscalationThresholdOfOne_MakesEveryIncidentPastThreshold()
	{
		var page = await Get("?past_threshold_days=1&page_size=1000");

		Assert.All(page.Data, incident => Assert.True(incident.PastThreshold));
	}

	[Fact]
	public async Task SuccessLookbackShorterThanTheFailureThreshold_ReportsNothing()
	{
		// A feed cannot both have completed within two days and have been failing for five.
		var page = await Get("?success_lookback_days=2&error_days=5");

		Assert.Empty(page.Data);
		Assert.Equal(0, page.Meta.Total);
	}

	[Fact]
	public async Task AsOf_MovesTheSnapshotDateAndRecomputesAgainstIt()
	{
		var latest = await Get();
		var asOf = latest.Meta.SnapshotDate.AddDays(-2);

		var earlier = await Get($"?as_of={asOf:yyyy-MM-dd}");

		Assert.Equal(asOf, earlier.Meta.SnapshotDate);
		Assert.All(earlier.Data, i =>
			Assert.Equal(i.DaysOpen, asOf.DayNumber - i.Detail.LastCompleted.DayNumber));
	}
}
