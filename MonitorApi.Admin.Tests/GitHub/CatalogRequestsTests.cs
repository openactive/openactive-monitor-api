using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.GitHub;

/// <summary>
/// The rules behind <c>/admin/catalog-requests</c>: which pull requests count, their status, first
/// contact, completion and the summary figures. Hand-written input, exact answers, no network.
/// </summary>
public class CatalogRequestsTests
{
	private static readonly DateTime Opened = new(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);
	private static readonly DateTime Cutoff = new(2025, 11, 24, 0, 0, 0, DateTimeKind.Utc);

	private static readonly StewardDirectory Stewards = new([("steward-a", "Alice"), ("Steward-B", "Bob")]);

	private static GitHubPullRequestActivity Pr(
		int number = 1,
		string author = "publisher",
		bool authorIsBot = false,
		DateTime? createdAt = null,
		DateTime? closedAt = null,
		DateTime? mergedAt = null,
		string[]? assignees = null,
		string[]? reviewers = null,
		params GitHubInteraction[] interactions) =>
		new(
			new GitHubPullRequest(
				number,
				$"PR {number}",
				$"https://github.com/openactive/data-catalogs/pull/{number}",
				new GitHubUser(author, authorIsBot),
				createdAt ?? Opened,
				closedAt,
				mergedAt,
				assignees ?? [],
				reviewers ?? []),
			interactions);

	private static CatalogRequest Single(GitHubPullRequestActivity pr) =>
		Assert.Single(CatalogRequests.Build([pr], Stewards, Cutoff));

	#region Which pull requests count

	[Fact]
	public void StewardAndBotAuthors_AreExcluded()
	{
		var built = CatalogRequests.Build(
			[Pr(1), Pr(2, author: "steward-a"), Pr(3, author: "STEWARD-B"), Pr(4, author: "renovate[bot]", authorIsBot: true)],
			Stewards,
			Cutoff);

		Assert.Equal([1], built.Select(r => r.Number));
	}

	[Fact]
	public void Cutoff_IsInclusive()
	{
		var built = CatalogRequests.Build(
			[Pr(1, createdAt: Cutoff), Pr(2, createdAt: Cutoff.AddSeconds(-1))],
			Stewards,
			Cutoff);

		Assert.Equal([1], built.Select(r => r.Number));
	}

	[Fact]
	public void NewestFirst()
	{
		var built = CatalogRequests.Build(
			[Pr(1, createdAt: Opened), Pr(2, createdAt: Opened.AddDays(2)), Pr(3, createdAt: Opened.AddDays(1))],
			Stewards,
			Cutoff);

		Assert.Equal([2, 3, 1], built.Select(r => r.Number));
	}

	#endregion

	#region Status and completion

	[Fact]
	public void Open_HasNoCompletion()
	{
		var request = Single(Pr());

		Assert.Equal(CatalogRequestStatus.Open, request.Status);
		Assert.Null(request.CompletedAt);
		Assert.Null(request.CompletionHours);
	}

	[Fact]
	public void Merged_TakesPrecedenceOverClosed()
	{
		var request = Single(Pr(closedAt: Opened.AddHours(30), mergedAt: Opened.AddHours(30)));

		Assert.Equal(CatalogRequestStatus.Merged, request.Status);
		Assert.Equal(Opened.AddHours(30), request.CompletedAt);
		Assert.Equal(30.0, request.CompletionHours);
	}

	[Fact]
	public void ClosedWithoutMerge_IsClosed()
	{
		var request = Single(Pr(closedAt: Opened.AddMinutes(90)));

		Assert.Equal(CatalogRequestStatus.Closed, request.Status);
		Assert.Equal(1.5, request.CompletionHours);
	}

	#endregion

	#region First contact and stewards

	[Fact]
	public void FirstContact_IsTheEarliestStewardInteraction()
	{
		var request = Single(Pr(interactions:
		[
			new("publisher", Opened.AddMinutes(1)),
			new("steward-a", Opened.AddHours(5)),
			new("steward-b", Opened.AddHours(2)),
			new("someone-else", Opened.AddHours(1)),
		]));

		Assert.Equal(Opened.AddHours(2), request.FirstContactAt);
		Assert.Equal(2.0, request.FirstContactHours);
	}

	[Fact]
	public void NoStewardReply_LeavesFirstContactNull()
	{
		var request = Single(Pr(
			assignees: ["steward-a"],
			reviewers: ["steward-b"],
			interactions: [new("publisher", Opened.AddHours(1))]));

		Assert.Null(request.FirstContactAt);
		Assert.Null(request.FirstContactHours);
	}

	[Fact]
	public void Stewards_CoverRepliesAssigneesAndReviewers_ByNameSortedOnce()
	{
		var request = Single(Pr(
			assignees: ["steward-a", "not-a-steward"],
			reviewers: ["STEWARD-B"],
			interactions: [new("steward-a", Opened.AddHours(1)), new("Steward-A", Opened.AddHours(2))]));

		Assert.Equal(["Alice", "Bob"], request.Stewards);
	}

	[Fact]
	public void Stewards_EmptyWhenNoneEngaged() => Assert.Empty(Single(Pr()).Stewards);

	#endregion

	#region Summary and filter

	[Fact]
	public void Summarise_CountsAndMedians()
	{
		var requests = CatalogRequests.Build(
		[
			Pr(1),                                                        // open, no reply
			Pr(2, interactions: [new("steward-a", Opened.AddHours(4))]), // open, replied
			Pr(3, mergedAt: Opened.AddHours(10), interactions: [new("steward-a", Opened.AddHours(1))]),
			Pr(4, closedAt: Opened.AddHours(20), interactions: [new("steward-b", Opened.AddHours(3))]),
		], Stewards, Cutoff);

		var figures = CatalogRequests.Summarise(requests);

		Assert.Equal(new CatalogRequestFigures(
			Total: 4,
			Open: 2,
			Merged: 1,
			Closed: 1,
			AwaitingFirstContact: 1,
			MedianFirstContactHours: 3.0, // 1, 3, 4
			MedianCompletionHours: 15.0), // 10, 20
			figures);
	}

	[Fact]
	public void Summarise_Empty_HasNullMedians()
	{
		var figures = CatalogRequests.Summarise([]);

		Assert.Equal(0, figures.Total);
		Assert.Null(figures.MedianFirstContactHours);
		Assert.Null(figures.MedianCompletionHours);
	}

	[Theory]
	[InlineData(new double[] { 5 }, 5.0)]
	[InlineData(new double[] { 3, 1, 2 }, 2.0)]
	[InlineData(new double[] { 4, 1, 3, 2 }, 2.5)]
	public void Median_OddAndEven(double[] values, double expected) =>
		Assert.Equal(expected, CatalogRequests.Median(values.Select(v => (double?)v)));

	[Fact]
	public void Filter_ByStatusIgnoringCase()
	{
		var requests = CatalogRequests.Build(
			[Pr(1), Pr(2, mergedAt: Opened.AddDays(1)), Pr(3, closedAt: Opened.AddDays(1))],
			Stewards,
			Cutoff);

		Assert.Equal([1, 2], CatalogRequests.Filter(requests, ["OPEN", "merged"]).Select(r => r.Number).Order());
		Assert.Equal(3, CatalogRequests.Filter(requests, []).Count);
	}

	#endregion

	[Fact]
	public void StewardDirectory_IgnoresBlankLogins_AndFallsBackToLoginForBlankNames()
	{
		var directory = new StewardDirectory([("", "Nobody"), ("  steward-c ", " ")]);

		Assert.True(directory.IsSteward("STEWARD-C"));
		Assert.Equal("steward-c", directory.DisplayName("steward-c"));
		Assert.False(directory.IsSteward(""));
		Assert.False(new StewardDirectory([]).IsSteward("anyone"));
	}

	/// <summary>
	/// The steward list is empty by default, and the endpoints still answer: every author is community
	/// and nobody's reply is a steward's.
	/// </summary>
	[Fact]
	public void NoStewards_EveryPullRequestCounts_WithNoFirstContact()
	{
		var built = CatalogRequests.Build(
			[Pr(1, author: "steward-a", assignees: ["steward-a"], interactions: [new("steward-b", Opened.AddHours(1))])],
			new StewardDirectory([]),
			Cutoff);

		var request = Assert.Single(built);
		Assert.Null(request.FirstContactAt);
		Assert.Empty(request.Stewards);
	}
}
