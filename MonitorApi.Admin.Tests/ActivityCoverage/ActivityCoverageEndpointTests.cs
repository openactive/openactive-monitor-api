using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MonitorApi.Models.Admin;

namespace MonitorApi.Admin.Tests.ActivityCoverage;

/// <summary>
/// Live-data tests for <c>/admin/activity-coverage</c>. Matching and aggregation are pinned by
/// <see cref="ActivityLabelMatcherTests"/> and <see cref="ActivityCoverageTests"/>; these check the
/// wiring, the envelope, the filters and the invariants that must hold whatever the data is on the day.
/// Never absolute counts.
/// </summary>
public class ActivityCoverageEndpointTests(AdminApiFixture fixture) : IClassFixture<AdminApiFixture>
{
	private const string Route = "/admin/activity-coverage";
	private const string ActivityListPrefix = "https://openactive.io/activity-list#";

	private readonly AdminApiFixture _fixture = fixture;

	private static readonly JsonSerializerOptions JsonOptions =
		new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

	private async Task<AdminPage<ActivityCoverageRow>> Get(string query = "")
	{
		using var client = _fixture.CreateClient();
		var response = await client.GetAsync(_fixture.WithAdminToken(Route + query));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return (await response.Content.ReadFromJsonAsync<AdminPage<ActivityCoverageRow>>(JsonOptions))!;
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
	public async Task EveryRowIsAVocabularyConceptWithAPositiveCount()
	{
		var page = await Get("?page_size=1000");

		Assert.All(page.Data, row =>
		{
			Assert.NotEmpty(row.DistrictCode);
			Assert.NotEmpty(row.Activity);
			Assert.StartsWith(ActivityListPrefix, row.ActivityId);
			Assert.True(row.OpportunityCount > 0, $"{row.DistrictCode}/{row.Activity} counted {row.OpportunityCount}");
		});
	}

	[Fact]
	public async Task EachDistrictAndActivityAppearsOnce()
	{
		var page = await Get("?page_size=1000");

		Assert.Equal(page.Data.Count, page.Data.Select(r => (r.DistrictCode, r.ActivityId)).Distinct().Count());
	}

	[Fact]
	public async Task OrderedByActivityThenLargestCountThenDistrict()
	{
		var page = await Get("?page_size=1000");

		var expected = page.Data
			.OrderBy(r => r.Activity, StringComparer.Ordinal)
			.ThenByDescending(r => r.OpportunityCount)
			.ThenBy(r => r.DistrictCode, StringComparer.Ordinal)
			.Select(r => (r.DistrictCode, r.ActivityId));

		Assert.Equal(expected, page.Data.Select(r => (r.DistrictCode, r.ActivityId)));
	}

	[Fact]
	public async Task PagesAreDisjointAndContinuous()
	{
		var first = await Get("?page=1&page_size=50");
		if (first.Meta.Total <= 50)
		{
			return;
		}

		var second = await Get("?page=2&page_size=50");
		var both = await Get("?page=1&page_size=100");

		Assert.Equal(first.Meta.Total, second.Meta.Total);
		Assert.Equal(
			both.Data.Select(r => (r.DistrictCode, r.ActivityId)),
			first.Data.Concat(second.Data).Select(r => (r.DistrictCode, r.ActivityId)));
	}

	[Fact]
	public async Task ActivityFilter_ReturnsOnlyThatConceptWhateverTheCase()
	{
		var page = await Get("?activity=swimming&page_size=1000");

		Assert.All(page.Data, row => Assert.Equal("Swimming", row.Activity));
		Assert.Equal(page.Data.Count, page.Data.Select(r => r.DistrictCode).Distinct().Count());
	}

	[Fact]
	public async Task ActivityFilter_NamingNoConceptReturnsNothing()
	{
		var page = await Get("?activity=not-a-real-activity");

		Assert.Empty(page.Data);
		Assert.Equal(0, page.Meta.Total);
	}

	[Fact]
	public async Task DistrictFilter_ReturnsOnlyThatDistrict()
	{
		var all = await Get("?page_size=1");
		if (all.Data.Count == 0)
		{
			return;
		}

		var district = all.Data[0].DistrictCode;
		var page = await Get($"?district={district}&page_size=1000");

		Assert.NotEmpty(page.Data);
		Assert.All(page.Data, row => Assert.Equal(district, row.DistrictCode));
	}

	[Fact]
	public async Task AStricterThresholdNeverIncreasesACount()
	{
		var loose = await Get("?activity=Swimming&page_size=1000");
		var strict = await Get("?activity=Swimming&min_similarity=1&page_size=1000");

		var looseCounts = loose.Data.ToDictionary(r => r.DistrictCode, r => r.OpportunityCount);
		Assert.All(strict.Data, row =>
			Assert.True(
				looseCounts.TryGetValue(row.DistrictCode, out var looser) && looser >= row.OpportunityCount,
				$"{row.DistrictCode}: {row.OpportunityCount} at min_similarity=1, {looser} at the default"));
	}
}
