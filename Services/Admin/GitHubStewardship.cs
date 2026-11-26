namespace MonitorApi.Services.Admin;

/// <summary>
/// Who the stewards are: GitHub login to display name, with logins matched ignoring case as GitHub
/// itself does.
/// </summary>
public sealed class StewardDirectory
{
	private readonly Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);

	/// <param name="stewards">Login and display name pairs. Blank logins are ignored; a blank name falls back to the login.</param>
	public StewardDirectory(IEnumerable<(string Login, string Name)> stewards)
	{
		foreach (var (login, name) in stewards)
		{
			var trimmed = login?.Trim() ?? "";
			if (trimmed.Length > 0)
			{
				names[trimmed] = string.IsNullOrWhiteSpace(name) ? trimmed : name.Trim();
			}
		}
	}

	/// <summary>Whether <paramref name="login"/> belongs to a steward.</summary>
	public bool IsSteward(string? login) => login is not null && names.ContainsKey(login);

	/// <summary>The steward's display name, or the login itself for anyone else.</summary>
	public string DisplayName(string login) => names.TryGetValue(login, out var name) ? name : login;
}

/// <summary>The state of a dataset addition request.</summary>
public enum CatalogRequestStatus
{
	Open,
	Closed,
	Merged,
}

/// <summary>A dataset addition request with the steward response figures worked out.</summary>
public sealed record CatalogRequest(
	int Number,
	string Title,
	string Url,
	string? AuthorLogin,
	DateTime CreatedAt,
	CatalogRequestStatus Status,
	IReadOnlyList<string> Stewards,
	DateTime? FirstContactAt,
	double? FirstContactHours,
	DateTime? CompletedAt,
	double? CompletionHours);

/// <summary>Counts and medians over a set of <see cref="CatalogRequest"/>s.</summary>
public sealed record CatalogRequestFigures(
	int Total,
	int Open,
	int Merged,
	int Closed,
	int AwaitingFirstContact,
	double? MedianFirstContactHours,
	double? MedianMergeHours,
	double? MedianCloseHours);

/// <summary>
/// The rules for dataset addition requests: which pull requests count, and how quickly the stewards
/// answered and finished them. Pure.
/// </summary>
public static class CatalogRequests
{
	/// <summary>
	/// Turns pull requests into requests, newest first.
	/// </summary>
	/// <remarks>
	/// A pull request counts when it was opened on or after <paramref name="cutoff"/> by someone who is
	/// neither a steward nor a bot. Steward pull requests are internal edits, not requests.
	///
	/// <list type="bullet">
	/// <item><b>Status</b>: merged when it has a merge time, else closed when it has a close time, else open.</item>
	/// <item><b>First contact</b>: the earliest comment, review or review comment by a steward other than the author.</item>
	/// <item><b>Completed</b>: the merge time, else the close time.</item>
	/// <item><b>Stewards</b>: the stewards who commented, reviewed, were assigned or were asked to review, by display name.</item>
	/// </list>
	/// </remarks>
	public static List<CatalogRequest> Build(
		IEnumerable<GitHubPullRequestActivity> activity,
		StewardDirectory stewards,
		DateTime cutoff)
	{
		return activity
			.Where(a => a.PullRequest.CreatedAt >= cutoff)
			.Where(a => a.PullRequest.Author is { } author && !author.IsBot && !stewards.IsSteward(author.Login))
			.Select(a => ToRequest(a, stewards))
			.OrderByDescending(r => r.CreatedAt)
			.ThenByDescending(r => r.Number)
			.ToList();
	}

	/// <summary>Counts each status and takes the medians across <paramref name="requests"/>.</summary>
	public static CatalogRequestFigures Summarise(IReadOnlyCollection<CatalogRequest> requests) =>
		new(
			Total: requests.Count,
			Open: requests.Count(r => r.Status == CatalogRequestStatus.Open),
			Merged: requests.Count(r => r.Status == CatalogRequestStatus.Merged),
			Closed: requests.Count(r => r.Status == CatalogRequestStatus.Closed),
			AwaitingFirstContact: requests.Count(r => r.Status == CatalogRequestStatus.Open && r.FirstContactAt is null),
			MedianFirstContactHours: Median(requests.Select(r => r.FirstContactHours)),
			// Merged and closed mean opposite outcomes, so their times are never pooled.
			MedianMergeHours: Median(requests.Where(r => r.Status == CatalogRequestStatus.Merged).Select(r => r.CompletionHours)),
			MedianCloseHours: Median(requests.Where(r => r.Status == CatalogRequestStatus.Closed).Select(r => r.CompletionHours)));

	/// <summary>Keeps the requests in any of <paramref name="statuses"/>, matched ignoring case. Empty keeps all.</summary>
	public static List<CatalogRequest> Filter(IEnumerable<CatalogRequest> requests, IReadOnlyCollection<string> statuses)
	{
		var wanted = new HashSet<string>(statuses, StringComparer.OrdinalIgnoreCase);
		return requests.Where(r => wanted.Count == 0 || wanted.Contains(r.Status.ToString())).ToList();
	}

	/// <summary>The median of the values present, to one decimal place; <c>null</c> when none are.</summary>
	public static double? Median(IEnumerable<double?> values)
	{
		var sorted = values.OfType<double>().Order().ToList();
		if (sorted.Count == 0)
		{
			return null;
		}

		var middle = sorted.Count / 2;
		var median = sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
		return Math.Round(median, 1);
	}

	private static CatalogRequest ToRequest(GitHubPullRequestActivity activity, StewardDirectory stewards)
	{
		var pr = activity.PullRequest;
		var author = pr.Author?.Login;

		var stewardInteractions = activity.Interactions
			.Where(i => stewards.IsSteward(i.Login) && !string.Equals(i.Login, author, StringComparison.OrdinalIgnoreCase))
			.ToList();

		DateTime? firstContact = stewardInteractions.Count == 0 ? null : stewardInteractions.Min(i => i.At);

		var status = pr.MergedAt is not null
			? CatalogRequestStatus.Merged
			: pr.ClosedAt is not null ? CatalogRequestStatus.Closed : CatalogRequestStatus.Open;
		var completed = pr.MergedAt ?? pr.ClosedAt;

		var involved = stewardInteractions.Select(i => i.Login)
			.Concat(pr.Assignees)
			.Concat(pr.RequestedReviewers)
			.Where(stewards.IsSteward)
			.Select(stewards.DisplayName)
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal)
			.ToList();

		return new CatalogRequest(
			pr.Number,
			pr.Title,
			pr.HtmlUrl,
			author,
			pr.CreatedAt,
			status,
			involved,
			firstContact,
			Hours(pr.CreatedAt, firstContact),
			completed,
			Hours(pr.CreatedAt, completed));
	}

	internal static double? Hours(DateTime from, DateTime? to) =>
		to is null ? null : Math.Round(Math.Max(0, (to.Value - from).TotalHours), 1);
}

/// <summary>An issue raised by the community on an OpenActive tooling repository.</summary>
public sealed record ToolingIssue(
	string Repo,
	int Number,
	string Title,
	string Url,
	string? AuthorLogin,
	DateTime CreatedAt,
	bool IsOpen,
	IReadOnlyList<string> Assignees,
	DateTime? ClosedAt);

/// <summary>The rules for tooling issues: which issues count, and who is handling them. Pure.</summary>
public static class ToolingIssues
{
	/// <summary>
	/// Keeps issues opened on or after <paramref name="cutoff"/> by someone who is neither a steward
	/// nor a bot, newest first. Assignees are shown by steward name, or by login for anyone else.
	/// </summary>
	/// <remarks>
	/// GitHub's <c>since</c> filter is on the last update, not on creation, so the creation cutoff is
	/// applied here.
	/// </remarks>
	public static List<ToolingIssue> Build(IEnumerable<GitHubIssue> issues, StewardDirectory stewards, DateTime cutoff) =>
		issues
			.Where(i => i.CreatedAt >= cutoff)
			.Where(i => i.Author is { } author && !author.IsBot && !stewards.IsSteward(author.Login))
			.Select(i => new ToolingIssue(
				i.Repo,
				i.Number,
				i.Title,
				i.HtmlUrl,
				i.Author?.Login,
				i.CreatedAt,
				i.IsOpen,
				i.Assignees.Select(stewards.DisplayName).Distinct(StringComparer.Ordinal).ToList(),
				i.IsOpen ? null : i.ClosedAt))
			.OrderByDescending(i => i.CreatedAt)
			.ThenBy(i => i.Repo, StringComparer.Ordinal)
			.ThenByDescending(i => i.Number)
			.ToList();

	/// <summary>
	/// Keeps the issues in any of <paramref name="repos"/> and any of <paramref name="statuses"/>
	/// (<c>open</c>, <c>closed</c>), each matched ignoring case. An empty filter keeps all.
	/// </summary>
	public static List<ToolingIssue> Filter(
		IEnumerable<ToolingIssue> issues,
		IReadOnlyCollection<string> repos,
		IReadOnlyCollection<string> statuses)
	{
		var wantedRepos = new HashSet<string>(repos, StringComparer.OrdinalIgnoreCase);
		var wantedStatuses = new HashSet<string>(statuses, StringComparer.OrdinalIgnoreCase);

		return issues
			.Where(i => wantedRepos.Count == 0 || wantedRepos.Contains(i.Repo) || wantedRepos.Contains(ShortName(i.Repo)))
			.Where(i => wantedStatuses.Count == 0 || wantedStatuses.Contains(i.IsOpen ? "open" : "closed"))
			.ToList();
	}

	private static string ShortName(string repo) => repo[(repo.IndexOf('/') + 1)..];
}
