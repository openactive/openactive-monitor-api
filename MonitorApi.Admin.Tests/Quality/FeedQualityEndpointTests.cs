using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MonitorApi.Models.Admin;
using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.Quality;

/// <summary>
/// Live-data tests for <c>/admin/feed-quality</c>. The summary arithmetic is pinned by
/// <see cref="FeedQualitySummariserTests"/>; these check the wiring, the three-key envelope, the
/// filters and the invariants that must hold whatever the data looks like on the day.
/// </summary>
public class FeedQualityEndpointTests(AdminApiFixture fixture) : IClassFixture<AdminApiFixture>
{
	private const string Route = "/admin/feed-quality";

	private readonly AdminApiFixture _fixture = fixture;

	private static readonly JsonSerializerOptions JsonOptions =
		new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

	private async Task<AdminSummarisedPage<FeedQualityRow, FeedQualitySummary>> Get(string query = "")
	{
		using var client = _fixture.CreateClient();
		var response = await client.GetAsync(_fixture.WithAdminToken(Route + query));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return (await response.Content.ReadFromJsonAsync<AdminSummarisedPage<FeedQualityRow, FeedQualitySummary>>(JsonOptions))!;
	}

	#region Envelope

	[Fact]
	public async Task ReturnsTheDocumentedEnvelope()
	{
		var page = await Get();

		Assert.NotNull(page.Data);
		Assert.NotNull(page.Summary);
		Assert.Equal(1, page.Meta.Page);
		Assert.Equal(500, page.Meta.PageSize);
		Assert.True(page.Meta.Total >= page.Data.Count);
		Assert.Equal(DateTimeKind.Utc, page.Meta.GeneratedAt.Kind);
		Assert.True(page.Meta.SnapshotDate > DateOnly.MinValue);
	}

	[Fact]
	public async Task SnapshotDate_IsTheLatestAssessmentDay()
	{
		var page = await Get();

		if (page.Summary.NewestAssessment is null)
		{
			return;
		}

		Assert.Equal(DateOnly.FromDateTime(page.Summary.NewestAssessment.Value), page.Meta.SnapshotDate);
		Assert.All(page.Data, row =>
			Assert.True(row.LastAssessed is null || DateOnly.FromDateTime(row.LastAssessed.Value) <= page.Meta.SnapshotDate));
	}

	#endregion

	#region Rows

	[Fact]
	public async Task EveryRowIsInternallyConsistent()
	{
		var page = await Get("?page_size=1000");

		Assert.All(page.Data, row =>
		{
			Assert.NotEmpty(row.FeedId);
			Assert.NotEmpty(row.DatasetUrl);
			// Derived, so never empty however little the assessment recorded.
			Assert.NotEmpty(row.DatasetName);
			Assert.StartsWith("pub_", row.PublisherId);

			Assert.True(row.Score is null or (>= 0 and <= 100), $"{row.FeedId} scored {row.Score}");
			Assert.True(row.NumFutureOpportunityItems is null or >= 0);

			// A percentage, not a ratio: the stored columns run 0–100.
			Assert.All(Completeness(row), value =>
				Assert.True(value is null or (>= 0 and <= 100), $"{row.FeedId} reported completeness {value}"));

			Assert.True(row.LastAssessed is null || row.LastAssessed.Value.Kind == DateTimeKind.Utc);
		});
	}

	[Fact]
	public async Task EachFeedIsReportedAtMostOnce()
	{
		var page = await Get("?page_size=1000");

		Assert.Equal(page.Data.Count, page.Data.Select(r => r.FeedId).Distinct().Count());
	}

	[Fact]
	public async Task RowsAreOrderedBestScoringFirstWithUnscoredFeedsLast()
	{
		var page = await Get("?page_size=1000");

		var scored = page.Data.TakeWhile(r => r.Score is not null).Select(r => r.Score!.Value).ToList();

		Assert.Equal(scored.OrderByDescending(s => s), scored);
		// Everything after the first unscored row is also unscored — NULLS LAST, not interleaved.
		Assert.All(page.Data.Skip(scored.Count), r => Assert.Null(r.Score));
	}

	#endregion

	#region Summary

	[Fact]
	public async Task SummaryCoversTheWholeResultSetRatherThanThePage()
	{
		var page = await Get("?page_size=1");

		Assert.Equal(page.Meta.Total, page.Summary.TotalFeeds);
		Assert.True(page.Data.Count <= 1);
	}

	[Fact]
	public async Task SummaryDoesNotChangeBetweenPages()
	{
		var first = await Get("?page=1&page_size=10");
		if (first.Meta.Total <= 10)
		{
			return;
		}

		var second = await Get("?page=2&page_size=10");

		Assert.Equal(first.Summary.TotalFeeds, second.Summary.TotalFeeds);
		Assert.Equal(first.Summary.TotalDatasets, second.Summary.TotalDatasets);
		Assert.Equal(first.Summary.AverageScore, second.Summary.AverageScore);
		Assert.Equal(first.Summary.FeedsWithErrors, second.Summary.FeedsWithErrors);
	}

	[Fact]
	public async Task SummaryCountersAccountForEveryFeed()
	{
		var summary = (await Get()).Summary;

		Assert.Equal(
			summary.TotalFeeds,
			summary.FeedsOk + summary.FeedsWithWarnings + summary.FeedsWithErrors + summary.FeedsStatusUnknown);
		Assert.Equal(
			summary.TotalFeeds,
			summary.RegularFeeds + summary.IrregularFeeds + summary.RegularityUnknown);

		foreach (var breakdown in new[]
		{
			summary.StatusBreakdown, summary.GradeBreakdown, summary.FeedTypeBreakdown, summary.FeedVersionBreakdown,
		})
		{
			Assert.Equal(summary.TotalFeeds, breakdown.Sum(b => b.FeedCount));
			Assert.All(breakdown, b => Assert.InRange(b.Share, 0, 1));
			// Totally ordered, so the same request always renders the same list.
			Assert.Equal(
				breakdown.OrderByDescending(b => b.FeedCount).ThenBy(b => b.Value, StringComparer.OrdinalIgnoreCase),
				breakdown);
		}
	}

	[Fact]
	public async Task SummaryAgreesWithTheRowsItWasReducedFrom()
	{
		var page = await Get("?page_size=1000");
		if (page.Meta.Total > page.Data.Count)
		{
			return;
		}

		Assert.Equal(
			page.Data.Count(r => string.Equals(r.Status, FeedQualitySummariser.StatusError, StringComparison.OrdinalIgnoreCase)),
			page.Summary.FeedsWithErrors);
		Assert.Equal(page.Data.Select(r => r.DatasetUrl).Distinct(StringComparer.OrdinalIgnoreCase).Count(), page.Summary.TotalDatasets);
		Assert.Equal(page.Data.Count(r => r.NumFutureOpportunityItems > 0), page.Summary.FeedsWithFutureData);
		Assert.Equal(page.Data.Sum(r => r.NumFutureOpportunityItems ?? 0), page.Summary.TotalFutureOpportunityItems);
	}

	[Fact]
	public async Task ScoreFiguresAreConsistentWithEachOther()
	{
		var summary = (await Get()).Summary;

		Assert.Equal(FeedQualitySummariser.ScoreBucketCount, summary.ScoreBuckets.Count);
		Assert.Equal(summary.FeedsScored, summary.ScoreBuckets.Sum(b => b.FeedCount));
		Assert.True(summary.FeedsScored <= summary.TotalFeeds);

		if (summary.FeedsScored == 0)
		{
			Assert.Null(summary.AverageScore);
			return;
		}

		Assert.NotNull(summary.MinScore);
		Assert.NotNull(summary.MaxScore);
		Assert.True(summary.MinScore <= summary.AverageScore);
		Assert.True(summary.AverageScore <= summary.MaxScore);
		Assert.InRange(summary.MedianScore!.Value, summary.MinScore!.Value, summary.MaxScore!.Value);
	}

	[Fact]
	public async Task CompletenessAveragesCarryTheirOwnDenominator()
	{
		var summary = (await Get()).Summary;

		foreach (var average in CompletenessAverages(summary.Completeness))
		{
			Assert.True(average.FeedsReporting <= summary.TotalFeeds);

			if (average.FeedsReporting == 0)
			{
				// Nothing reported it, so the mean is absent rather than zero.
				Assert.Null(average.Average);
			}
			else
			{
				Assert.NotNull(average.Average);
				Assert.InRange(average.Average!.Value, 0, 100);
			}
		}
	}

	#endregion

	#region Paging

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
		Assert.Empty(first.Data.Select(r => r.FeedId).Intersect(second.Data.Select(r => r.FeedId)));
	}

	[Fact]
	public async Task PagingArgumentsAreClamped()
	{
		var page = await Get("?page=0&page_size=99999");

		Assert.Equal(1, page.Meta.Page);
		Assert.Equal(1000, page.Meta.PageSize);
	}

	[Fact]
	public async Task PageBeyondTheEnd_IsEmptyButStillReportsTheTotalAndSummary()
	{
		var all = await Get("?page_size=1000");
		var beyond = await Get("?page=100000&page_size=10");

		Assert.Empty(beyond.Data);
		Assert.Equal(all.Meta.Total, beyond.Meta.Total);
		Assert.Equal(all.Summary.TotalFeeds, beyond.Summary.TotalFeeds);
	}

	#endregion

	#region Filters

	[Fact]
	public async Task DatasetUrlFilter_NarrowsToThatDataset()
	{
		var all = await Get("?page_size=1000");
		if (all.Data.Count == 0)
		{
			return;
		}

		var dataset = all.Data[0].DatasetUrl;
		var filtered = await Get("?dataset_url=" + Uri.EscapeDataString(dataset));

		Assert.NotEmpty(filtered.Data);
		Assert.All(filtered.Data, r => Assert.Equal(dataset, r.DatasetUrl));
		Assert.True(filtered.Meta.Total <= all.Meta.Total);
		Assert.Equal(1, filtered.Summary.TotalDatasets);
		Assert.Equal(filtered.Meta.Total, filtered.Summary.TotalFeeds);
	}

	[Fact]
	public async Task PublisherFilter_NarrowsToThatPublisher()
	{
		var all = await Get("?page_size=1000");
		var named = all.Data.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.PublisherName));
		if (named is null)
		{
			return;
		}

		var filtered = await Get("?publisher=" + Uri.EscapeDataString(named.PublisherName));

		Assert.NotEmpty(filtered.Data);
		Assert.All(filtered.Data, r => Assert.Equal(named.PublisherName, r.PublisherName));
		Assert.True(filtered.Meta.Total <= all.Meta.Total);
	}

	[Fact]
	public async Task RepeatedAndCommaSeparatedValuesAgree()
	{
		var all = await Get("?page_size=1000");
		var datasets = all.Data.Select(r => r.DatasetUrl).Distinct(StringComparer.Ordinal).Take(2).ToList();
		if (datasets.Count < 2)
		{
			return;
		}

		var escaped = datasets.Select(Uri.EscapeDataString).ToList();
		var repeated = await Get($"?dataset_url={escaped[0]}&dataset_url={escaped[1]}");
		var commaSeparated = await Get($"?dataset_url={escaped[0]},{escaped[1]}");

		Assert.Equal(repeated.Meta.Total, commaSeparated.Meta.Total);
		Assert.Equal(repeated.Data.Select(r => r.FeedId), commaSeparated.Data.Select(r => r.FeedId));
		// Values within one parameter are OR'd, so two datasets return at least as much as one.
		Assert.Equal(2, repeated.Summary.TotalDatasets);
	}

	[Fact]
	public async Task FiltersAreCombinedWithAnd()
	{
		var all = await Get("?page_size=1000");
		var named = all.Data.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.PublisherName));
		if (named is null)
		{
			return;
		}

		var both = await Get(
			$"?dataset_url={Uri.EscapeDataString(named.DatasetUrl)}&publisher={Uri.EscapeDataString(named.PublisherName)}");
		var datasetOnly = await Get("?dataset_url=" + Uri.EscapeDataString(named.DatasetUrl));

		Assert.True(both.Meta.Total <= datasetOnly.Meta.Total);
		Assert.All(both.Data, r =>
		{
			Assert.Equal(named.DatasetUrl, r.DatasetUrl);
			Assert.Equal(named.PublisherName, r.PublisherName);
		});
	}

	[Fact]
	public async Task UnmatchedFilter_IsAnEmptyPageWithAZeroedSummary()
	{
		var page = await Get("?dataset_url=" + Uri.EscapeDataString("https://no-such-dataset.invalid/"));

		Assert.Empty(page.Data);
		Assert.Equal(0, page.Meta.Total);
		Assert.Equal(0, page.Summary.TotalFeeds);
		Assert.Equal(0, page.Summary.TotalDatasets);
		Assert.Null(page.Summary.AverageScore);
		// The snapshot still dates the table, not the empty result.
		Assert.True(page.Meta.SnapshotDate > DateOnly.MinValue);
	}

	#endregion

	private static IEnumerable<double?> Completeness(FeedQualityRow row) =>
	[
		row.Completeness.Location,
		row.Completeness.StartDate,
		row.Completeness.EndDate,
		row.Completeness.Activities,
		row.Completeness.Facilities,
		row.Completeness.AgeRange,
		row.Completeness.Level,
		row.Completeness.AccessibilitySupport,
		row.Completeness.GenderRestriction,
	];

	private static IEnumerable<FeedQualityAverage> CompletenessAverages(FeedQualityCompleteness completeness) =>
	[
		completeness.Location,
		completeness.StartDate,
		completeness.EndDate,
		completeness.Activities,
		completeness.Facilities,
		completeness.AgeRange,
		completeness.Level,
		completeness.AccessibilitySupport,
		completeness.GenderRestriction,
	];
}
