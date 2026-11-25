using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.GitHub;

/// <summary>
/// The rules behind <c>/admin/tooling-issues</c>: which issues count, assignee names, ordering and
/// the filters. Hand-written input, exact answers, no network.
/// </summary>
public class ToolingIssuesTests
{
	private static readonly DateTime Opened = new(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);
	private static readonly DateTime Cutoff = new(2025, 11, 24, 0, 0, 0, DateTimeKind.Utc);

	private static readonly StewardDirectory Stewards = new([("steward-a", "Alice")]);

	private static GitHubIssue Issue(
		int number = 1,
		string repo = "openactive/rpde-validator",
		string author = "community",
		bool authorIsBot = false,
		DateTime? createdAt = null,
		bool isOpen = true,
		DateTime? closedAt = null,
		params string[] assignees) =>
		new(repo, number, $"Issue {number}", $"https://github.com/{repo}/issues/{number}",
			new GitHubUser(author, authorIsBot), createdAt ?? Opened, closedAt, isOpen, assignees);

	[Fact]
	public void StewardAndBotAuthors_AreExcluded()
	{
		var built = ToolingIssues.Build(
			[Issue(1), Issue(2, author: "Steward-A"), Issue(3, author: "dependabot[bot]", authorIsBot: true)],
			Stewards,
			Cutoff);

		Assert.Equal([1], built.Select(i => i.Number));
	}

	/// <summary>
	/// GitHub's <c>since</c> lists issues <em>updated</em> inside the window, so an old issue with a
	/// recent comment arrives too. It was not opened inside the window and must not be reported.
	/// </summary>
	[Fact]
	public void IssuesOpenedBeforeTheCutoff_AreExcluded_EvenIfRecentlyUpdated()
	{
		var built = ToolingIssues.Build([Issue(1, createdAt: Cutoff.AddDays(-30)), Issue(2, createdAt: Cutoff)], Stewards, Cutoff);

		Assert.Equal([2], built.Select(i => i.Number));
	}

	[Fact]
	public void Assignees_ShowStewardNames_OrLogins()
	{
		var issue = Assert.Single(ToolingIssues.Build([Issue(assignees: ["STEWARD-A", "contributor"])], Stewards, Cutoff));

		Assert.Equal(["Alice", "contributor"], issue.Assignees);
	}

	[Fact]
	public void ClosedAt_IsKeptForClosedIssuesOnly()
	{
		var built = ToolingIssues.Build(
			[Issue(1, isOpen: false, closedAt: Opened.AddDays(3)), Issue(2, createdAt: Opened.AddDays(1), isOpen: true, closedAt: Opened.AddDays(2))],
			Stewards,
			Cutoff);

		Assert.Null(built.Single(i => i.Number == 2).ClosedAt);
		Assert.Equal(Opened.AddDays(3), built.Single(i => i.Number == 1).ClosedAt);
	}

	[Fact]
	public void NewestFirst_ThenRepo()
	{
		var built = ToolingIssues.Build(
			[
				Issue(1, repo: "openactive/b", createdAt: Opened),
				Issue(2, repo: "openactive/a", createdAt: Opened),
				Issue(3, repo: "openactive/a", createdAt: Opened.AddDays(1)),
			],
			Stewards,
			Cutoff);

		Assert.Equal([3, 2, 1], built.Select(i => i.Number));
	}

	[Fact]
	public void NoStewards_EveryIssueCounts_AndAssigneesShowLogins()
	{
		var issue = Assert.Single(ToolingIssues.Build([Issue(author: "steward-a", assignees: ["steward-a"])], new StewardDirectory([]), Cutoff));

		Assert.Equal(["steward-a"], issue.Assignees);
	}

	[Fact]
	public void Filter_ByRepoFullOrShortName_AndStatus()
	{
		var built = ToolingIssues.Build(
			[
				Issue(1, repo: "openactive/rpde-validator"),
				Issue(2, repo: "openactive/data-models", isOpen: false, closedAt: Opened.AddDays(1)),
				Issue(3, repo: "openactive/data-models"),
			],
			Stewards,
			Cutoff);

		Assert.Equal([1], ToolingIssues.Filter(built, ["OpenActive/RPDE-Validator"], []).Select(i => i.Number));
		Assert.Equal([3, 2], ToolingIssues.Filter(built, ["data-models"], []).Select(i => i.Number));
		Assert.Equal([2], ToolingIssues.Filter(built, [], ["CLOSED"]).Select(i => i.Number));
		Assert.Equal(3, ToolingIssues.Filter(built, [], []).Count);
	}
}
