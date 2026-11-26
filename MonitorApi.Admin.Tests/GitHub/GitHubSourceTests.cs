using System.Net;
using Microsoft.Extensions.Options;
using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.GitHub;

/// <summary>
/// <see cref="GitHubSource"/> against a canned HTTP handler: which URLs it asks for, how it pages, the
/// creation-date window, and the daily cache. No network.
/// </summary>
public class GitHubSourceTests
{
	private static readonly DateTime Since = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

	/// <summary>Answers each request from a path → (body, Link header) map and records what was asked for.</summary>
	private sealed class CannedHandler(Dictionary<string, (string Body, string? Link)> pages) : HttpMessageHandler
	{
		public List<string> Requested { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var path = request.RequestUri!.PathAndQuery.TrimStart('/');
			Requested.Add(path);

			if (!pages.TryGetValue(path, out var page))
			{
				return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
			}

			var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(page.Body) };
			if (page.Link is not null)
			{
				response.Headers.Add("Link", page.Link);
			}
			return Task.FromResult(response);
		}
	}

	private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
	{
		public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = GitHubSource.ApiRoot };
	}

	private static (GitHubSource Source, CannedHandler Handler) Source(
		Dictionary<string, (string, string?)> pages,
		GitHubOptions? options = null)
	{
		var handler = new CannedHandler(pages);
		return (new GitHubSource(new Factory(handler), Options.Create(options ?? new GitHubOptions())), handler);
	}

	private static string Pr(int number, string createdAt) =>
		$$"""{ "number": {{number}}, "title": "PR {{number}}", "html_url": "u", "user": { "login": "p", "type": "User" }, "created_at": "{{createdAt}}" }""";

	private static string Issue(int number, string createdAt) =>
		$$"""{ "number": {{number}}, "title": "I {{number}}", "html_url": "u", "user": { "login": "p", "type": "User" }, "state": "open", "created_at": "{{createdAt}}" }""";

	private const string PullsPage1 = "repos/o/r/pulls?state=all&sort=created&direction=desc&per_page=100";
	private const string PullsPage2 = "repos/o/r/pulls?page=2";

	[Fact]
	public async Task GetPullRequests_FollowsLinks_AndStopsOnceThePageReachesPastTheWindow()
	{
		var (source, handler) = Source(new()
		{
			[PullsPage1] = ($"[{Pr(3, "2026-03-01T00:00:00Z")}, {Pr(2, "2026-02-01T00:00:00Z")}]",
				"<https://api.github.com/repos/o/r/pulls?page=2>; rel=\"next\""),
			[PullsPage2] = ($"[{Pr(1, "2025-12-01T00:00:00Z")}]",
				"<https://api.github.com/repos/o/r/pulls?page=3>; rel=\"next\""),
		});

		var prs = await source.GetPullRequests("o/r", Since);

		Assert.Equal([3, 2], prs.Select(p => p.Number));
		// Page 2 reached before the window, so page 3 was never requested.
		Assert.Equal([PullsPage1, PullsPage2], handler.Requested);
	}

	[Fact]
	public async Task GetIssues_PassesSinceAndCutsOnCreation()
	{
		const string path = "repos/o/r/issues?state=all&since=2026-01-01T00%3A00%3A00Z&per_page=100";
		var (source, _) = Source(new()
		{
			[path] = ($"[{Issue(2, "2026-01-05T00:00:00Z")}, {Issue(1, "2025-06-01T00:00:00Z")}]", null),
		});

		var issues = await source.GetIssues("o/r", Since);

		Assert.Equal([2], issues.Select(i => i.Number));
		Assert.All(issues, i => Assert.Equal("o/r", i.Repo));
	}

	[Fact]
	public async Task GetPullRequestInteractions_CombinesCommentsReviewsAndReviewComments()
	{
		var (source, handler) = Source(new()
		{
			["repos/o/r/issues/7/comments?per_page=100"] = ("""[{ "user": { "login": "a" }, "created_at": "2026-01-02T00:00:00Z" }]""", null),
			["repos/o/r/pulls/7/reviews?per_page=100"] = ("""[{ "user": { "login": "b" }, "state": "COMMENTED", "submitted_at": "2026-01-03T00:00:00Z" }]""", null),
			["repos/o/r/pulls/7/comments?per_page=100"] = ("""[{ "user": { "login": "c" }, "created_at": "2026-01-04T00:00:00Z" }]""", null),
		});

		var interactions = await source.GetPullRequestInteractions("o/r", 7);

		Assert.Equal(["a", "b", "c"], interactions.Select(i => i.Login));
		Assert.Equal(3, handler.Requested.Count);
	}

	[Fact]
	public async Task FailedRequest_ThrowsHttpRequestException()
	{
		var (source, _) = Source([]);

		await Assert.ThrowsAsync<HttpRequestException>(() => source.GetIssues("o/missing", Since));
	}

	[Fact]
	public async Task GetToolingIssues_ReadsEachConfiguredRepoOnce_AndCaches()
	{
		// The cutoff moves with the clock, so the exact URL is not known up front: answer anything with [].
		var handler = new AnyPathHandler();
		var source = new GitHubSource(
			new Factory(handler),
			Options.Create(new GitHubOptions { ToolingRepositories = ["o/a", " o/a ", "O/A", "", "o/b"] }));

		var first = await source.GetToolingIssues();
		var second = await source.GetToolingIssues();

		Assert.Same(first, second);
		Assert.Equal(2, handler.Requested.Count);
		Assert.StartsWith("repos/o/a/issues?", handler.Requested[0]);
		Assert.StartsWith("repos/o/b/issues?", handler.Requested[1]);
	}

	/// <summary>Answers every request with an empty list, recording the path.</summary>
	private sealed class AnyPathHandler : HttpMessageHandler
	{
		public List<string> Requested { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requested.Add(request.RequestUri!.PathAndQuery.TrimStart('/'));
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
		}
	}
}
