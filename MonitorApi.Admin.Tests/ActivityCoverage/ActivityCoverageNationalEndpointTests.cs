using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MonitorApi.Models.Admin;

namespace MonitorApi.Admin.Tests.ActivityCoverage;

/// <summary>
/// Live-data tests for <c>/admin/activity-coverage/summary</c> and
/// <c>/admin/activity-coverage/top-activities</c>. The counting rules are pinned by
/// <see cref="ActivityCoverageStatsTests"/> and <see cref="ActivityRankTests"/>; these check the wiring,
/// the envelopes, and invariants that hold whatever the data is on the day, including agreement with the
/// per-district map. Never absolute counts.
/// </summary>
public class ActivityCoverageNationalEndpointTests(AdminApiFixture fixture) : IClassFixture<AdminApiFixture>
{
	private const string SummaryRoute = "/admin/activity-coverage/summary";
	private const string TopRoute = "/admin/activity-coverage/top-activities";
	private const string MapRoute = "/admin/activity-coverage";

	private readonly AdminApiFixture _fixture = fixture;

	private static readonly JsonSerializerOptions JsonOptions =
		new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

	private async Task<T> Get<T>(string route)
	{
		using var client = _fixture.CreateClient();
		var response = await client.GetAsync(_fixture.WithAdminToken(route));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;
	}

	private Task<AdminDocument<ActivityCoverageSummary>> Summary(string query = "") =>
		Get<AdminDocument<ActivityCoverageSummary>>(SummaryRoute + query);

	private Task<AdminPage<TopActivityRow>> Top(string query = "") =>
		Get<AdminPage<TopActivityRow>>(TopRoute + query);

	[Fact]
	public async Task Summary_ReturnsASingleDocument()
	{
		var document = await Summary();

		Assert.NotNull(document.Data);
		Assert.Equal(1, document.Meta.Page);
		Assert.Equal(1, document.Meta.Total);
		Assert.True(document.Meta.SnapshotDate > DateOnly.MinValue);
	}

	[Fact]
	public async Task Summary_FiguresNestAndPercentagesAreInRange()
	{
		var s = (await Summary()).Data;

		Assert.InRange(s.LinkedOpportunities, 0, s.LabelledOpportunities);
		Assert.InRange(s.LabelledOpportunities, 0, s.ActivityOpportunities);
		Assert.InRange(s.ActivitiesOffered, 0, s.VocabularySize);
		Assert.True(s.VocabularySize > 0);

		if (s.ActivityOpportunities > 0)
		{
			Assert.InRange(s.LabelledPercentage!.Value, 0, 100);
			Assert.InRange(s.LinkedPercentage!.Value, 0, s.LabelledPercentage.Value);
		}
	}

	[Fact]
	public async Task Summary_AStricterThresholdNeverRaisesLinkedOrOffered()
	{
		var loose = (await Summary()).Data;
		var strict = (await Summary("?min_similarity=1")).Data;

		Assert.Equal(loose.ActivityOpportunities, strict.ActivityOpportunities);
		Assert.Equal(loose.LabelledOpportunities, strict.LabelledOpportunities);
		Assert.True(strict.LinkedOpportunities <= loose.LinkedOpportunities);
		Assert.True(strict.ActivitiesOffered <= loose.ActivitiesOffered);
	}

	[Fact]
	public async Task Summary_ActivitiesOfferedMatchesTheRankingLength()
	{
		var summary = (await Summary()).Data;
		var top = await Top("?page_size=1");

		Assert.Equal(summary.ActivitiesOffered, top.Meta.Total);
	}

	[Fact]
	public async Task Top_DefaultsToTheTopTen()
	{
		var page = await Top();

		Assert.Equal(10, page.Meta.PageSize);
		Assert.InRange(page.Data.Count, 0, 10);
		Assert.Equal(Enumerable.Range(1, page.Data.Count), page.Data.Select(r => r.Rank));
	}

	[Fact]
	public async Task Top_IsRankedByLargestCountThenLabel()
	{
		var page = await Top("?page_size=1000");

		Assert.All(page.Data, row =>
		{
			Assert.StartsWith("https://openactive.io/activity-list#", row.ActivityId);
			Assert.True(row.OpportunityCount > 0);
			Assert.True(row.DistrictCount > 0);
		});

		var expected = page.Data
			.OrderByDescending(r => r.OpportunityCount)
			.ThenBy(r => r.Activity, StringComparer.Ordinal)
			.Select(r => r.ActivityId);
		Assert.Equal(expected, page.Data.Select(r => r.ActivityId));
		Assert.Equal(page.Data.Count, page.Data.Select(r => r.ActivityId).Distinct().Count());
	}

	[Fact]
	public async Task Top_RanksContinueAcrossPages()
	{
		var first = await Top();
		if (first.Meta.Total <= 10)
		{
			return;
		}

		var second = await Top("?page=2");

		Assert.Equal(11, second.Data[0].Rank);
		Assert.True(second.Data[0].OpportunityCount <= first.Data[^1].OpportunityCount);
	}

	[Fact]
	public async Task Top_TheLeaderAgreesWithTheMap()
	{
		var top = await Top("?page_size=1");
		if (top.Data.Count == 0)
		{
			return;
		}

		var leader = top.Data[0];
		var map = await Get<AdminPage<ActivityCoverageRow>>(
			$"{MapRoute}?activity={Uri.EscapeDataString(leader.ActivityId)}&page_size=1000");

		Assert.Equal(leader.OpportunityCount, map.Data.Sum(r => r.OpportunityCount));
		Assert.Equal(leader.DistrictCount, map.Meta.Total);
	}
}
