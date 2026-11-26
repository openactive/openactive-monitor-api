using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.GitHub;

/// <summary>
/// Reading GitHub REST responses, from literals trimmed to the fields the parser uses. No network.
/// </summary>
public class GitHubParserTests
{
	[Fact]
	public void PullRequests_ReadEveryField()
	{
		const string json = """
			[{
				"number": 56,
				"title": "Add FitTrybe dataset site",
				"html_url": "https://github.com/openactive/data-catalogs/pull/56",
				"user": { "login": "publisher", "type": "User" },
				"created_at": "2026-08-17T00:00:54Z",
				"closed_at": "2026-08-20T10:00:00Z",
				"merged_at": "2026-08-20T10:00:00Z",
				"assignees": [{ "login": "steward-a" }],
				"requested_reviewers": [{ "login": "steward-b" }, { "login": "steward-c" }]
			}]
			""";

		var pr = Assert.Single(GitHubParser.ParsePullRequests(json));

		Assert.Equal(56, pr.Number);
		Assert.Equal("Add FitTrybe dataset site", pr.Title);
		Assert.Equal("https://github.com/openactive/data-catalogs/pull/56", pr.HtmlUrl);
		Assert.Equal(new GitHubUser("publisher", IsBot: false), pr.Author);
		Assert.Equal(new DateTime(2026, 8, 17, 0, 0, 54, DateTimeKind.Utc), pr.CreatedAt);
		Assert.Equal(DateTimeKind.Utc, pr.CreatedAt.Kind);
		Assert.Equal(new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc), pr.MergedAt);
		Assert.Equal(["steward-a"], pr.Assignees);
		Assert.Equal(["steward-b", "steward-c"], pr.RequestedReviewers);
	}

	[Fact]
	public void PullRequests_OpenOneHasNoCloseOrMergeTime()
	{
		const string json = """
			[{ "number": 1, "title": "t", "html_url": "u", "user": { "login": "p", "type": "User" },
			   "created_at": "2026-01-01T00:00:00Z", "closed_at": null, "merged_at": null }]
			""";

		var pr = Assert.Single(GitHubParser.ParsePullRequests(json));

		Assert.Null(pr.ClosedAt);
		Assert.Null(pr.MergedAt);
		Assert.Empty(pr.Assignees);
		Assert.Empty(pr.RequestedReviewers);
	}

	[Fact]
	public void BotAccounts_AreFlagged()
	{
		const string json = """
			[{ "number": 1, "title": "Bump", "html_url": "u", "user": { "login": "dependabot[bot]", "type": "Bot" },
			   "created_at": "2026-01-01T00:00:00Z", "state": "open" }]
			""";

		var issue = Assert.Single(GitHubParser.ParseIssues(json, "openactive/x"));

		Assert.True(issue.Author!.IsBot);
	}

	[Fact]
	public void Issues_DropPullRequestsAndReadState()
	{
		const string json = """
			[
				{ "number": 10, "title": "Bug", "html_url": "https://github.com/o/r/issues/10",
				  "user": { "login": "someone", "type": "User" }, "state": "closed",
				  "created_at": "2026-02-01T09:00:00Z", "closed_at": "2026-02-03T09:00:00Z",
				  "assignees": [{ "login": "steward-a" }] },
				{ "number": 11, "title": "A PR", "html_url": "u", "user": { "login": "someone", "type": "User" },
				  "state": "open", "created_at": "2026-02-02T09:00:00Z", "pull_request": { "url": "x" } }
			]
			""";

		var issue = Assert.Single(GitHubParser.ParseIssues(json, "o/r"));

		Assert.Equal("o/r", issue.Repo);
		Assert.Equal(10, issue.Number);
		Assert.False(issue.IsOpen);
		Assert.Equal(new DateTime(2026, 2, 3, 9, 0, 0, DateTimeKind.Utc), issue.ClosedAt);
		Assert.Equal(["steward-a"], issue.Assignees);
	}

	[Fact]
	public void Comments_ReadAuthorAndTime_AndSkipDeletedUsers()
	{
		const string json = """
			[
				{ "user": { "login": "steward-a", "type": "User" }, "created_at": "2026-08-18T13:52:34Z" },
				{ "user": null, "created_at": "2026-08-19T00:00:00Z" }
			]
			""";

		var comment = Assert.Single(GitHubParser.ParseComments(json));

		Assert.Equal(new GitHubInteraction("steward-a", new DateTime(2026, 8, 18, 13, 52, 34, DateTimeKind.Utc)), comment);
	}

	[Fact]
	public void Reviews_UseSubmittedAt_AndSkipPending()
	{
		const string json = """
			[
				{ "user": { "login": "steward-a" }, "state": "APPROVED", "submitted_at": "2026-03-01T12:00:00Z" },
				{ "user": { "login": "steward-b" }, "state": "PENDING" }
			]
			""";

		var review = Assert.Single(GitHubParser.ParseReviews(json));

		Assert.Equal(new GitHubInteraction("steward-a", new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc)), review);
	}

	[Fact]
	public void EmptyList_IsEmpty() => Assert.Empty(GitHubParser.ParseIssues("[]", "o/r"));

	[Theory]
	[InlineData("not json")]
	[InlineData("""{ "message": "API rate limit exceeded" }""")]
	[InlineData("[1, 2]")]
	[InlineData("""[{ "title": "no number", "created_at": "2026-01-01T00:00:00Z" }]""")]
	[InlineData("""[{ "number": 1, "created_at": "yesterday" }]""")]
	public void Malformed_ThrowsInvalidData(string json) =>
		Assert.Throws<InvalidDataException>(() => GitHubParser.ParsePullRequests(json));

	[Fact]
	public void NextPageUrl_FollowsRelNext()
	{
		const string header =
			"<https://api.github.com/repositories/1/issues?page=2>; rel=\"next\", " +
			"<https://api.github.com/repositories/1/issues?page=5>; rel=\"last\"";

		Assert.Equal("https://api.github.com/repositories/1/issues?page=2", GitHubParser.NextPageUrl(header));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("<https://api.github.com/x?page=1>; rel=\"prev\", <https://api.github.com/x?page=1>; rel=\"first\"")]
	public void NextPageUrl_IsNullOnTheLastPage(string? header) => Assert.Null(GitHubParser.NextPageUrl(header));
}
