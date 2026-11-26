using Microsoft.Extensions.Options;

namespace MonitorApi.Services.Admin;

/// <summary>What one fetch from GitHub returned, and when it was made.</summary>
public sealed record GitHubSnapshot<T>(IReadOnlyList<T> Items, DateTime FetchedAt);

/// <summary>
/// Reads pull requests, issues and their discussion from the GitHub REST API.
/// </summary>
/// <remarks>
/// Two layers:
///
/// <list type="bullet">
/// <item><b>Repository calls</b> — <see cref="GetPullRequests"/>,
/// <see cref="GetIssues(string, DateTime, CancellationToken)"/>,
/// <see cref="GetIssueComments"/>, <see cref="GetReviews"/>, <see cref="GetReviewComments"/> and the
/// composites <see cref="GetPullRequestInteractions"/> / <see cref="GetPullRequestActivity"/>. They take
/// any public repository, are <b>not cached</b>, and each one goes to GitHub. Use them to build a new
/// view; do not call them per request.</item>
/// <item><b>Dashboard views</b> — <see cref="GetCatalogPullRequests"/> and
/// <see cref="GetToolingIssues"/>. They read the repositories in <see cref="GitHubOptions"/> through the
/// calls above and hold the answer until the next daily refresh. Controllers call these.</item>
/// </list>
///
/// Built the same way as <see cref="ActivePlacesSource"/>, and impure for the same reason: every byte of
/// network handling is here, so <see cref="GitHubParser"/> and the rules in <see cref="CatalogRequests"/>
/// / <see cref="ToolingIssues"/> stay testable from literals. It holds no rules beyond the creation-date
/// window it is asked for.
///
/// <b>Why the views cache.</b> Output cache entries vary by the whole query string, and each miss would
/// otherwise make a dozen or more GitHub calls. GitHub is read anonymously, which allows 60 calls an
/// hour per IP, shared with whatever else runs on that IP. Each view is shared by every request and
/// expires at 07:00 UTC like the responses built from it, so a caller walking the pages sees one
/// snapshot.
///
/// A singleton. Per view, one caller fetches at a time and the rest wait for that result. A failure is
/// not cached, so the next request retries.
/// </remarks>
public sealed class GitHubSource(IHttpClientFactory httpClientFactory, IOptions<GitHubOptions> options)
{
	/// <summary>Name the <see cref="HttpClient"/> for this source is registered under.</summary>
	public const string HttpClientName = "github";

	/// <summary>Where the REST API lives. Set as the client's base address in <c>Program.cs</c>.</summary>
	public static readonly Uri ApiRoot = new("https://api.github.com/");

	/// <summary>
	/// Upper bound on list pages followed per call, at 100 items a page. A guard against a runaway
	/// <c>Link</c> chain, far above anything the OpenActive repositories hold.
	/// </summary>
	private const int MaxPages = 20;

	private readonly GitHubOptions options = options.Value;

	private readonly DailyCache<GitHubSnapshot<GitHubPullRequestActivity>> catalogPullRequests = new();
	private readonly DailyCache<GitHubSnapshot<GitHubIssue>> toolingIssues = new();

	/// <summary>The start of the lookback window for a fetch made at <paramref name="now"/>.</summary>
	public DateTime Cutoff(DateTime now) => now.AddDays(-Math.Max(1, options.LookbackDays));

	#region Dashboard views (cached)

	/// <summary>
	/// Pull requests on <see cref="GitHubOptions.CatalogRepository"/> opened inside the lookback window,
	/// each with every comment, review and review comment on it. Cached until the next daily refresh.
	/// </summary>
	/// <exception cref="HttpRequestException">GitHub could not be reached or refused the request.</exception>
	/// <exception cref="InvalidDataException">GitHub's answer could not be read.</exception>
	public Task<GitHubSnapshot<GitHubPullRequestActivity>> GetCatalogPullRequests(CancellationToken cancellationToken = default) =>
		catalogPullRequests.GetOrFetch(async () =>
		{
			var now = DateTime.UtcNow;
			var activity = await GetPullRequestActivity(options.CatalogRepository, Cutoff(now), cancellationToken);
			return new GitHubSnapshot<GitHubPullRequestActivity>(activity, now);
		}, cancellationToken);

	/// <summary>
	/// Issues (not pull requests) opened inside the lookback window on every repository in
	/// <see cref="GitHubOptions.ToolingRepositories"/>. Cached until the next daily refresh.
	/// </summary>
	/// <exception cref="HttpRequestException">GitHub could not be reached or refused the request.</exception>
	/// <exception cref="InvalidDataException">GitHub's answer could not be read.</exception>
	public Task<GitHubSnapshot<GitHubIssue>> GetToolingIssues(CancellationToken cancellationToken = default) =>
		toolingIssues.GetOrFetch(async () =>
		{
			var now = DateTime.UtcNow;
			var issues = await GetIssues(Repositories(options.ToolingRepositories), Cutoff(now), cancellationToken);
			return new GitHubSnapshot<GitHubIssue>(issues, now);
		}, cancellationToken);

	#endregion

	#region Repository calls (uncached)

	/// <summary>
	/// Pull requests on <paramref name="repo"/> opened on or after <paramref name="createdSince"/>, in
	/// any state, newest first. Not cached.
	/// </summary>
	/// <param name="repo">The repository, as <c>owner/name</c>.</param>
	/// <param name="createdSince">Earliest creation time to include. UTC.</param>
	/// <param name="cancellationToken">Cancels the fetch.</param>
	public async Task<List<GitHubPullRequest>> GetPullRequests(string repo, DateTime createdSince, CancellationToken cancellationToken = default)
	{
		// Newest first, so paging can stop at the first page that reaches past the window.
		var listed = await ReadPages(
			$"repos/{repo}/pulls?state=all&sort=created&direction=desc&per_page=100",
			GitHubParser.ParsePullRequests,
			page => page.Count > 0 && page[^1].CreatedAt < createdSince,
			cancellationToken);

		return listed.Where(pr => pr.CreatedAt >= createdSince).ToList();
	}

	/// <summary>
	/// Issues (never pull requests) on <paramref name="repo"/> opened on or after
	/// <paramref name="createdSince"/>, in any state. Not cached.
	/// </summary>
	/// <remarks>
	/// GitHub's <c>since</c> filters on the last <em>update</em>, which narrows the download; the cut on
	/// creation is applied here.
	/// </remarks>
	/// <param name="repo">The repository, as <c>owner/name</c>.</param>
	/// <param name="createdSince">Earliest creation time to include. UTC.</param>
	/// <param name="cancellationToken">Cancels the fetch.</param>
	public async Task<List<GitHubIssue>> GetIssues(string repo, DateTime createdSince, CancellationToken cancellationToken = default)
	{
		var since = Uri.EscapeDataString(createdSince.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));

		var listed = await ReadPages(
			$"repos/{repo}/issues?state=all&since={since}&per_page=100",
			json => GitHubParser.ParseIssues(json, repo),
			null,
			cancellationToken);

		return listed.Where(issue => issue.CreatedAt >= createdSince).ToList();
	}

	/// <summary>
	/// <see cref="GetIssues(string, DateTime, CancellationToken)"/> across several repositories, one
	/// after another, concatenated in the order given. Not cached.
	/// </summary>
	public async Task<List<GitHubIssue>> GetIssues(IEnumerable<string> repos, DateTime createdSince, CancellationToken cancellationToken = default)
	{
		var all = new List<GitHubIssue>();
		foreach (var repo in repos)
		{
			all.AddRange(await GetIssues(repo, createdSince, cancellationToken));
		}
		return all;
	}

	/// <summary>
	/// The conversation comments on issue or pull request <paramref name="number"/> — the ones under the
	/// description, not inline on the code. Not cached.
	/// </summary>
	public Task<List<GitHubInteraction>> GetIssueComments(string repo, int number, CancellationToken cancellationToken = default) =>
		ReadPages($"repos/{repo}/issues/{number}/comments?per_page=100", GitHubParser.ParseComments, null, cancellationToken);

	/// <summary>Submitted reviews on pull request <paramref name="number"/>. Pending reviews are left out. Not cached.</summary>
	public Task<List<GitHubInteraction>> GetReviews(string repo, int number, CancellationToken cancellationToken = default) =>
		ReadPages($"repos/{repo}/pulls/{number}/reviews?per_page=100", GitHubParser.ParseReviews, null, cancellationToken);

	/// <summary>Inline review comments on the code of pull request <paramref name="number"/>. Not cached.</summary>
	public Task<List<GitHubInteraction>> GetReviewComments(string repo, int number, CancellationToken cancellationToken = default) =>
		ReadPages($"repos/{repo}/pulls/{number}/comments?per_page=100", GitHubParser.ParseComments, null, cancellationToken);

	/// <summary>
	/// Everything anyone said on pull request <paramref name="number"/>: conversation comments, reviews
	/// and inline review comments, in that order. Three calls. Not cached.
	/// </summary>
	public async Task<List<GitHubInteraction>> GetPullRequestInteractions(string repo, int number, CancellationToken cancellationToken = default)
	{
		var interactions = new List<GitHubInteraction>();
		interactions.AddRange(await GetIssueComments(repo, number, cancellationToken));
		interactions.AddRange(await GetReviews(repo, number, cancellationToken));
		interactions.AddRange(await GetReviewComments(repo, number, cancellationToken));
		return interactions;
	}

	/// <summary>
	/// <see cref="GetPullRequests"/> with <see cref="GetPullRequestInteractions"/> for each one: one call
	/// for the list plus three per pull request. Not cached.
	/// </summary>
	public async Task<List<GitHubPullRequestActivity>> GetPullRequestActivity(string repo, DateTime createdSince, CancellationToken cancellationToken = default)
	{
		var activity = new List<GitHubPullRequestActivity>();
		foreach (var pr in await GetPullRequests(repo, createdSince, cancellationToken))
		{
			activity.Add(new GitHubPullRequestActivity(pr, await GetPullRequestInteractions(repo, pr.Number, cancellationToken)));
		}
		return activity;
	}

	#endregion

	#region Plumbing

	/// <summary>Configured repository names, trimmed, blanks dropped, each once ignoring case.</summary>
	private static IEnumerable<string> Repositories(IEnumerable<string> configured) =>
		configured
			.Where(r => !string.IsNullOrWhiteSpace(r))
			.Select(r => r.Trim())
			.Distinct(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Fetches <paramref name="firstUrl"/> (relative to <see cref="ApiRoot"/>) and follows the
	/// <c>Link: rel="next"</c> chain, parsing each page, until it ends, <paramref name="stopAfter"/> says
	/// the page just read is far enough, or <see cref="MaxPages"/> is reached.
	/// </summary>
	/// <exception cref="HttpRequestException">A page answered anything but success.</exception>
	private async Task<List<T>> ReadPages<T>(
		string firstUrl,
		Func<string, List<T>> parse,
		Func<List<T>, bool>? stopAfter,
		CancellationToken cancellationToken)
	{
		using var client = httpClientFactory.CreateClient(HttpClientName);
		client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 300));

		var results = new List<T>();
		string? url = firstUrl;

		for (var pageCount = 0; url is not null && pageCount < MaxPages; pageCount++)
		{
			using var response = await client.GetAsync(url, cancellationToken);
			response.EnsureSuccessStatusCode();

			var page = parse(await response.Content.ReadAsStringAsync(cancellationToken));
			results.AddRange(page);

			if (stopAfter?.Invoke(page) == true)
			{
				break;
			}

			url = GitHubParser.NextPageUrl(
				response.Headers.TryGetValues("Link", out var links) ? string.Join(',', links) : null);
		}

		return results;
	}

	/// <summary>
	/// One value held until the next admin refresh (07:00 UTC), with its own lock so different views
	/// never wait on each other. Only one caller fetches at a time; a failed fetch stores nothing.
	/// </summary>
	private sealed class DailyCache<T> where T : class
	{
		private readonly SemaphoreSlim gate = new(1, 1);
		private T? value;
		private DateTime expiresAt;

		public async Task<T> GetOrFetch(Func<Task<T>> fetch, CancellationToken cancellationToken)
		{
			await gate.WaitAsync(cancellationToken);
			try
			{
				if (value is not null && DateTime.UtcNow < expiresAt)
				{
					return value;
				}

				var fetched = await fetch();

				var now = DateTime.UtcNow;
				value = fetched;
				expiresAt = now + DailyRefreshCachePolicy.TimeUntilRefresh(now, DailyRefreshCachePolicy.AdminRefreshAt);
				return fetched;
			}
			finally
			{
				gate.Release();
			}
		}
	}

	#endregion
}
