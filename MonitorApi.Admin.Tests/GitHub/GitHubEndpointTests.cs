using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MonitorApi.Models.Admin;

namespace MonitorApi.Admin.Tests.GitHub;

/// <summary>
/// Live tests for <c>/admin/catalog-requests</c> and <c>/admin/tooling-issues</c>, against GitHub
/// itself. The rules are pinned by <see cref="CatalogRequestsTests"/>, <see cref="ToolingIssuesTests"/>
/// and <see cref="GitHubParserTests"/>; these check the wiring and the invariants that hold whatever
/// GitHub says on the day.
/// </summary>
/// <remarks>
/// Both endpoints share one class, and so one fixture and one <c>GitHubSource</c>, so a run reads
/// GitHub once. GitHub is read anonymously, at 60 requests an hour per IP, so running these several
/// times within an hour can exhaust the limit and fail with <c>502</c>.
///
/// Never assert that a row exists: the tooling repositories have had no community issues in the last
/// year, and the catalog sees a handful of pull requests.
/// </remarks>
public class GitHubEndpointTests(AdminApiFixture fixture) : IClassFixture<AdminApiFixture>
{
	private const string CatalogRoute = "/admin/catalog-requests";
	private const string IssuesRoute = "/admin/tooling-issues";

	private static readonly string[] CatalogStatuses = ["open", "closed", "merged"];

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

	private Task<AdminSummarisedPage<CatalogRequestRow, CatalogRequestSummary>> GetCatalog(string query = "") =>
		Get<AdminSummarisedPage<CatalogRequestRow, CatalogRequestSummary>>(CatalogRoute + query);

	private Task<AdminPage<ToolingIssueRow>> GetIssues(string query = "") =>
		Get<AdminPage<ToolingIssueRow>>(IssuesRoute + query);

	#region Catalog requests

	[Fact]
	public async Task Catalog_ReturnsTheDocumentedEnvelope()
	{
		var page = await GetCatalog();

		Assert.NotNull(page.Data);
		Assert.NotNull(page.Summary);
		Assert.Equal(1, page.Meta.Page);
		Assert.Equal(500, page.Meta.PageSize);
		Assert.Equal(page.Meta.Total, page.Data.Count);
		Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), page.Meta.SnapshotDate);
	}

	[Fact]
	public async Task Catalog_SummaryDescribesTheWholeResult()
	{
		var page = await GetCatalog();
		var summary = page.Summary;

		Assert.Equal(page.Meta.Total, summary.Total);
		Assert.Equal(summary.Total, summary.Open + summary.Merged + summary.Closed);
		Assert.InRange(summary.AwaitingFirstContact, 0, summary.Open);
		Assert.Equal(page.Data.Count(r => r.Status == "open"), summary.Open);
		Assert.Equal(page.Data.Count(r => r.Status == "merged"), summary.Merged);
	}

	[Fact]
	public async Task Catalog_EveryRowIsInternallyConsistent()
	{
		var page = await GetCatalog();
		var cutoff = DateTime.UtcNow.AddDays(-366);

		Assert.All(page.Data, row =>
		{
			Assert.Contains(row.Status, CatalogStatuses);
			Assert.StartsWith("https://github.com/", row.Url);
			Assert.True(row.CreatedAt >= cutoff, $"#{row.Number} was opened {row.CreatedAt:O}");
			Assert.Equal(row.Status == "open", row.CompletedAt is null);
			Assert.Equal(row.CompletedAt is null, row.CompletionHours is null);
			Assert.Equal(row.FirstContactAt is null, row.FirstContactHours is null);
			Assert.True(row.CompletedAt is null || row.CompletedAt >= row.CreatedAt);
			Assert.True(row.FirstContactAt is null || row.FirstContactAt >= row.CreatedAt);
			Assert.True(row.FirstContactHours is null or >= 0);
			Assert.True(row.CompletionHours is null or >= 0);
			Assert.Equal(row.Stewards.Order(StringComparer.Ordinal), row.Stewards);
		});
	}

	[Fact]
	public async Task Catalog_IsNewestFirst()
	{
		var page = await GetCatalog();

		Assert.Equal(page.Data.OrderByDescending(r => r.CreatedAt).Select(r => r.Number), page.Data.Select(r => r.Number));
	}

	[Fact]
	public async Task Catalog_StatusFilter_ReturnsASubset()
	{
		var all = await GetCatalog();
		var open = await GetCatalog("?status=OPEN");
		var openOrMerged = await GetCatalog("?status=open,merged");

		Assert.All(open.Data, r => Assert.Equal("open", r.Status));
		Assert.Equal(all.Summary.Open, open.Meta.Total);
		Assert.Equal(open.Summary.Open, open.Summary.Total);
		Assert.Equal(all.Summary.Open + all.Summary.Merged, openOrMerged.Meta.Total);
	}

	[Fact]
	public async Task Catalog_PagesAreDisjointAndSummaryDoesNotMove()
	{
		var all = await GetCatalog();
		var first = await GetCatalog("?page_size=1&page=1");
		var second = await GetCatalog("?page_size=1&page=2");

		Assert.Equal(all.Meta.Total, first.Meta.Total);
		Assert.Equal(all.Summary.Total, second.Summary.Total);
		Assert.True(first.Data.Count <= 1 && second.Data.Count <= 1);
		Assert.Empty(first.Data.Select(r => r.Number).Intersect(second.Data.Select(r => r.Number)));
	}

	[Fact]
	public async Task Catalog_PageSizeIsClamped()
	{
		Assert.Equal(1000, (await GetCatalog("?page_size=5000")).Meta.PageSize);
		Assert.Equal(1, (await GetCatalog("?page_size=0")).Meta.PageSize);
	}

	#endregion

	#region Tooling issues

	[Fact]
	public async Task Issues_ReturnsTheDocumentedEnvelope()
	{
		var page = await GetIssues();

		Assert.NotNull(page.Data);
		Assert.Equal(1, page.Meta.Page);
		Assert.Equal(page.Meta.Total, page.Data.Count);
		Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), page.Meta.SnapshotDate);
	}

	[Fact]
	public async Task Issues_EveryRowIsInternallyConsistent()
	{
		var page = await GetIssues();
		var cutoff = DateTime.UtcNow.AddDays(-366);

		Assert.All(page.Data, row =>
		{
			Assert.Contains(row.Status, new[] { "open", "closed" });
			Assert.Contains('/', row.Repo);
			Assert.Contains("/issues/", row.Url);
			Assert.True(row.CreatedAt >= cutoff);
			Assert.Equal(row.Status == "open", row.ClosedAt is null);
			Assert.NotNull(row.Assignees);
		});
		Assert.Equal(page.Data.OrderByDescending(r => r.CreatedAt).Select(r => r.Url), page.Data.Select(r => r.Url));
	}

	[Fact]
	public async Task Issues_FiltersReturnSubsets()
	{
		var all = await GetIssues();
		var closed = await GetIssues("?status=closed");
		var oneRepo = await GetIssues("?repo=rpde-validator");

		Assert.All(closed.Data, r => Assert.Equal("closed", r.Status));
		Assert.All(oneRepo.Data, r => Assert.Equal("openactive/rpde-validator", r.Repo));
		Assert.True(closed.Meta.Total <= all.Meta.Total);
		Assert.True(oneRepo.Meta.Total <= all.Meta.Total);
	}

	#endregion
}
