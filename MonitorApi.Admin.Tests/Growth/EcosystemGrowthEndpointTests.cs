using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MonitorApi.Models.Admin;

namespace MonitorApi.Admin.Tests.Growth;

/// <summary>
/// Live-data tests for <c>/admin/ecosystem-growth-trend</c>. The rules are pinned by
/// <see cref="EcosystemGrowthCalculatorTests"/>; these check shape, invariants, and that every list agrees
/// with the count beside it. Never absolute counts: the data moves daily.
/// </summary>
public class EcosystemGrowthEndpointTests(AdminApiFixture fixture) : IClassFixture<AdminApiFixture>
{
	private const string TrendRoute = "/admin/ecosystem-growth-trend";

	private readonly AdminApiFixture _fixture = fixture;

	private static readonly JsonSerializerOptions JsonOptions =
		new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

	private Task<AdminPage<EcosystemGrowthTrendPoint>> Trend(string query = "") =>
		GetAdmin<AdminPage<EcosystemGrowthTrendPoint>>(TrendRoute + query);

	private async Task<T> GetAdmin<T>(string route)
	{
		using var client = _fixture.CreateClient();
		var response = await client.GetAsync(_fixture.WithAdminToken(route));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;
	}

	private static void AssertCountsAreConsistent(EcosystemGrowthTrendPoint counts)
	{
		Assert.Equal(1, counts.PeriodStart.Day);
		Assert.Equal(counts.PeriodStart.AddMonths(1).AddDays(-1), counts.PeriodEnd);
		Assert.InRange(counts.DaysWithData, 0, counts.PeriodEnd.Day);
		Assert.InRange(counts.DaysWithFeedData, 0, counts.PeriodEnd.Day);
		Assert.True(counts.DatasetsAdded is null || counts.DatasetsAdded <= counts.DatasetsCrawled);
		// Both come from the same baseline, so they are null together or not at all.
		Assert.Equal(counts.DatasetsAdded is null, counts.DatasetsRemoved is null);

		int?[] activity =
		[
			counts.DatasetsWithFuture, counts.DatasetsWithoutFuture, counts.DatasetsStopped,
			counts.DatasetsStoppedAllRunsFailed, counts.DatasetsNotIngested,
		];

		// The activity figures are known together or not at all, and unknown exactly when there is no feed data.
		Assert.All(activity, a => Assert.Equal(counts.DaysWithFeedData == 0, a is null));
		if (counts.DaysWithFeedData == 0)
		{
			return;
		}

		var notIngested = counts.DatasetsNotIngested!.Value;
		var ingested = counts.DatasetsCrawled - notIngested;
		var stopped = counts.DatasetsStopped!.Value;

		// With future, without future and not ingested partition the crawled datasets.
		Assert.InRange(notIngested, 0, counts.DatasetsCrawled);
		Assert.Equal(ingested, counts.DatasetsWithFuture + counts.DatasetsWithoutFuture);
		// Stopped is judged only on what was ingested.
		Assert.InRange(stopped, 0, ingested);
		Assert.InRange(counts.DatasetsStoppedAllRunsFailed!.Value, 0, stopped);
	}

	#region Trend

	[Fact]
	public async Task Trend_PointsAreConsecutiveCompleteMonthsOldestFirst()
	{
		var page = await Trend();

		Assert.NotEmpty(page.Data);
		Assert.InRange(page.Data.Count, 1, 12);

		for (var i = 1; i < page.Data.Count; i++)
		{
			Assert.Equal(page.Data[i - 1].PeriodStart.AddMonths(1), page.Data[i].PeriodStart);
		}
	}

	[Fact]
	public async Task Trend_OnlyTheLastPointCanBeInProgress()
	{
		var page = await Trend();

		Assert.All(page.Data.SkipLast(1), p => Assert.False(p.InProgress));
		Assert.All(page.Data.Where(p => !p.InProgress), p => Assert.True(p.PeriodEnd <= page.Meta.SnapshotDate));
	}

	[Fact]
	public async Task Trend_AnInProgressPointHasAtLeastFiveDaysAndEndsAfterTheSnapshot()
	{
		var page = await Trend();
		var last = page.Data[^1];

		if (last.InProgress)
		{
			Assert.True(last.PeriodEnd > page.Meta.SnapshotDate);
			Assert.InRange(last.DaysWithData, 5, page.Meta.SnapshotDate.Day);
		}
		else
		{
			// No point in progress: the series must end at the last complete month, leaving nothing out.
			Assert.True(last.PeriodStart.AddMonths(2).AddDays(-1) > page.Meta.SnapshotDate);
		}
	}

	/// <summary>
	/// <c>as_of</c> ten days into a month: that month is in progress, and its figures come only from the
	/// days up to <c>as_of</c> — later data in the table must not leak in.
	/// </summary>
	[Fact]
	public async Task Trend_AsOfMidMonth_ReportsTheMonthInProgressFromItsFirstDaysOnly()
	{
		var latest = await Trend();
		var month = latest.Data.Last(p => !p.InProgress).PeriodStart;
		var asOf = month.AddDays(9);

		var page = await Trend($"?as_of={asOf:yyyy-MM-dd}");
		var last = page.Data[^1];

		Assert.Equal(month, last.PeriodStart);
		Assert.True(last.InProgress);
		Assert.InRange(last.DaysWithData, 5, 10);
	}

	[Fact]
	public async Task Trend_AsOfTooEarlyInTheMonth_EndsAtTheMonthBefore()
	{
		var latest = await Trend();
		var month = latest.Data.Last(p => !p.InProgress).PeriodStart;

		var page = await Trend($"?as_of={month.AddDays(2):yyyy-MM-dd}");

		Assert.Equal(month.AddMonths(-1), page.Data[^1].PeriodStart);
		Assert.False(page.Data[^1].InProgress);
	}

	[Fact]
	public async Task Trend_CountsAreConsistentWithinEveryPoint()
	{
		var page = await Trend();

		Assert.All(page.Data, AssertCountsAreConsistent);
	}

	/// <summary>
	/// The first point is the only one that can lack a baseline: every later one has the point before it.
	/// </summary>
	[Fact]
	public async Task Trend_OnlyTheFirstPointCanLackABaseline()
	{
		var page = await Trend();

		Assert.All(page.Data.Skip(1), p =>
		{
			Assert.NotNull(p.DatasetsAdded);
			Assert.NotNull(p.DatasetsRemoved);
		});
	}

	/// <summary>
	/// Datasets crawled this month = last month's, minus the removed, plus the added. The identity that
	/// proves added and removed are measured against the very point before.
	/// </summary>
	[Fact]
	public async Task Trend_CrawledReconcilesWithAddedAndRemoved()
	{
		var page = await Trend();

		for (var i = 1; i < page.Data.Count; i++)
		{
			var previous = page.Data[i - 1];
			var current = page.Data[i];

			Assert.Equal(
				previous.DatasetsCrawled - current.DatasetsRemoved + current.DatasetsAdded,
				current.DatasetsCrawled);
		}
	}

	[Fact]
	public async Task Trend_MonthsControlsTheLength()
	{
		var page = await Trend("?months=1");

		Assert.Single(page.Data);
	}

	[Fact]
	public async Task Trend_AsOf_MovesTheWindowBack()
	{
		var latest = await Trend();
		// The last day of the month before the latest point: a complete month, with none in progress.
		var asOf = latest.Data[^1].PeriodStart.AddDays(-1);

		var earlier = await Trend($"?as_of={asOf:yyyy-MM-dd}");

		Assert.Equal(asOf, earlier.Meta.SnapshotDate);
		Assert.Equal(latest.Data[^1].PeriodStart.AddMonths(-1), earlier.Data[^1].PeriodStart);
		Assert.False(earlier.Data[^1].InProgress);
	}

	[Fact]
	public async Task Trend_AsOfBeforeTheHistory_IsEmpty()
	{
		var page = await Trend("?as_of=2020-01-15");

		Assert.Empty(page.Data);
		Assert.Equal(0, page.Meta.Total);
	}

	[Fact]
	public async Task Trend_PagingArgumentsAreClamped()
	{
		var page = await Trend("?page=0&page_size=99999&months=99999");

		Assert.Equal(1, page.Meta.Page);
		Assert.Equal(1000, page.Meta.PageSize);
		Assert.InRange(page.Data.Count, 1, 60);
	}

	#endregion

	#region Lists

	/// <summary>Every list paired with the count it names, by field name.</summary>
	private static IEnumerable<(string Name, int? Count, IReadOnlyList<EcosystemGrowthDataset>? List)> Lists(
		EcosystemGrowthTrendPoint p) =>
	[
		("datasets_added", p.DatasetsAdded, p.DatasetsAddedList),
		("datasets_removed", p.DatasetsRemoved, p.DatasetsRemovedList),
		("datasets_without_future", p.DatasetsWithoutFuture, p.DatasetsWithoutFutureList),
		("datasets_stopped", p.DatasetsStopped, p.DatasetsStoppedList),
		("datasets_stopped_all_runs_failed", p.DatasetsStoppedAllRunsFailed, p.DatasetsStoppedAllRunsFailedList),
		("datasets_not_ingested", p.DatasetsNotIngested, p.DatasetsNotIngestedList),
	];

	private static HashSet<string> Urls(IReadOnlyList<EcosystemGrowthDataset>? list) =>
		(list ?? []).Select(d => d.DatasetUrl).ToHashSet(StringComparer.Ordinal);

	[Fact]
	public async Task Lists_HaveExactlyAsManyEntriesAsTheirCountAndAreNullTogetherWithIt()
	{
		var page = await Trend("?months=60");

		Assert.All(page.Data, p => Assert.All(Lists(p), l =>
		{
			Assert.Equal(l.Count is null, l.List is null);
			Assert.Equal(l.Count ?? 0, l.List?.Count ?? 0);
		}));
	}

	[Fact]
	public async Task Lists_NameEveryDatasetOnceInOrder()
	{
		var page = await Trend();

		foreach (var point in page.Data)
		{
			foreach (var (_, _, list) in Lists(point))
			{
				var entries = list ?? [];
				Assert.All(entries, d =>
				{
					Assert.False(string.IsNullOrWhiteSpace(d.DatasetUrl));
					Assert.False(string.IsNullOrWhiteSpace(d.DatasetName));
					Assert.StartsWith("pub_", d.PublisherId);
				});
				Assert.Equal(entries.Count, Urls(entries).Count);

				var ordered = entries
					.OrderBy(d => d.DatasetName, StringComparer.OrdinalIgnoreCase)
					.ThenBy(d => d.DatasetUrl, StringComparer.Ordinal)
					.Select(d => d.DatasetUrl);
				Assert.Equal(ordered, entries.Select(d => d.DatasetUrl));
			}
		}
	}

	/// <summary>The lists nest exactly as the counts' definitions say they do.</summary>
	[Fact]
	public async Task Lists_NestAsTheirDefinitionsSay()
	{
		var page = await Trend();

		foreach (var point in page.Data)
		{
			var stopped = Urls(point.DatasetsStoppedList);
			var withoutFuture = Urls(point.DatasetsWithoutFutureList);
			var allRunsFailed = Urls(point.DatasetsStoppedAllRunsFailedList);
			var notIngested = Urls(point.DatasetsNotIngestedList);

			Assert.Subset(stopped, allRunsFailed);
			// Nothing was read for a dataset not ingested, so it is in none of the activity lists.
			Assert.Empty(notIngested.Intersect(stopped));
			Assert.Empty(notIngested.Intersect(withoutFuture));

			// Opposite sides of one comparison can never share a dataset.
			Assert.Empty(Urls(point.DatasetsAddedList).Intersect(Urls(point.DatasetsRemovedList)));
		}
	}

	/// <summary>A dataset removed this month was listed last month, so the two points cannot both add it.</summary>
	[Fact]
	public async Task Lists_ADatasetIsNeverAddedInTwoConsecutiveMonths()
	{
		var page = await Trend();

		for (var i = 1; i < page.Data.Count; i++)
		{
			Assert.Empty(Urls(page.Data[i - 1].DatasetsAddedList).Intersect(Urls(page.Data[i].DatasetsAddedList)));
		}
	}

	#endregion
}
