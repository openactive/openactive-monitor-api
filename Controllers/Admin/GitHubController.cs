using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MonitorApi.Models.Admin;
using MonitorApi.Services.Admin;

namespace MonitorApi.Controllers.Admin;

/// <summary>
/// Steward responsiveness on GitHub for the admin dashboard: dataset addition requests on the data
/// catalog repository, and community issues on the OpenActive tooling repositories. Every endpoint
/// requires the admin token as the <c>token</c> query parameter.
/// </summary>
/// <remarks>
/// Reads the GitHub REST API rather than BigQuery, through <see cref="GitHubSource"/>, which holds each
/// answer until the next daily refresh. Which repositories are read and who the stewards are come from
/// the <c>GitHub</c> configuration section (<see cref="GitHubOptions"/>). The steward list is a secret
/// and is never committed.
/// </remarks>
public class GitHubController(
	IOptions<BigQueryOptions> bigQueryOptions,
	IOptions<ApiOptions> apiOptions,
	IOptions<GitHubOptions> gitHubOptions,
	GitHubSource source)
	: AdminControllerBase(bigQueryOptions, apiOptions)
{
	private readonly GitHubOptions gitHub = gitHubOptions.Value;
	private readonly GitHubSource source = source;

	/// <summary>
	/// Dataset Addition Requests
	/// </summary>
	/// <remarks>
	/// Pull requests on the data catalog repository (<c>openactive/data-catalogs</c> by default). This
	/// is how publishers ask for a dataset to be added to the catalog. Each row shows how quickly the
	/// stewards answered and how quickly they finished. Newest first.
	///
	/// The things worth knowing:
	///
	/// - **Only community requests count.** Pull requests opened by a steward (internal edits) or by a
	///	  bot account are left out. So is anything opened more than a year ago (<c>GitHub:LookbackDays</c>,
	///	  default 365).
	/// - **`status`** is <c>merged</c> when the pull request was merged, <c>closed</c> when it was closed
	///	  without merging, and <c>open</c> otherwise.
	/// - **`first_contact_at`** is the earliest comment, review or inline review comment by a steward.
	///	  Assigning a steward or requesting their review is not contact. Neither is closing without a
	///	  word. It is <c>null</c> until a steward writes something.
	/// - **`completed_at`** is the merge time, or else the close time. It is <c>null</c> while open.
	/// - **`stewards`** lists, by display name, the stewards who commented, reviewed, were assigned or
	///	  were asked to review. Who pressed merge is not recorded.
	/// - **Durations are in hours**, to one decimal place, measured from when the pull request was opened.
	/// - **`summary`** describes every row the filters matched, not just this page. Its medians skip
	///	  rows that have no value yet.
	///
	/// This is not a monitor: there is no <c>as_of</c> and no trend endpoint. <c>meta.snapshot_date</c>
	/// is the UTC date GitHub was read.
	///
	/// Works without a steward list (<c>GitHub:Stewards</c> empty): every pull request then counts as a
	/// community request, <c>first_contact_at</c> is always <c>null</c> and <c>stewards</c> is always
	/// empty. Answers <c>502</c> when GitHub cannot be reached or refuses the request (a rate limit, for
	/// instance). Successful results are cached until the next daily refresh,
	/// varying by all query parameters.
	/// </remarks>
	/// <param name="page">One-based page number. Default <c>1</c>.</param>
	/// <param name="page_size">Rows per page. Default <c>500</c>, capped at <c>1000</c>.</param>
	/// <param name="status">One or more of <c>open</c>, <c>closed</c>, <c>merged</c>, matched ignoring case. Accepts repeated (<c>?status=open&amp;status=merged</c>) or comma-separated (<c>?status=open,merged</c>) values.</param>
	[HttpGet("catalog-requests")]
	[ProducesResponseType(typeof(AdminSummarisedPage<CatalogRequestRow, CatalogRequestSummary>), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status502BadGateway)]
	public async Task<ActionResult<AdminSummarisedPage<CatalogRequestRow, CatalogRequestSummary>>> CatalogRequestsList(
		int page = 1,
		int page_size = DefaultPageSize,
		[FromQuery] string[]? status = null)
	{
		var stewards = Stewards();

		GitHubSnapshot<GitHubPullRequestActivity> snapshot;
		try
		{
			snapshot = await source.GetCatalogPullRequests(HttpContext.RequestAborted);
		}
		catch (Exception e) when (IsUpstreamFailure(e))
		{
			return Unavailable("GitHub", e);
		}

		var requests = CatalogRequests.Filter(
			CatalogRequests.Build(snapshot.Items, stewards, source.Cutoff(snapshot.FetchedAt)),
			NormaliseMultiValue(status));

		var figures = CatalogRequests.Summarise(requests);
		var summary = new CatalogRequestSummary
		{
			Total = figures.Total,
			Open = figures.Open,
			Merged = figures.Merged,
			Closed = figures.Closed,
			AwaitingFirstContact = figures.AwaitingFirstContact,
			MedianFirstContactHours = figures.MedianFirstContactHours,
			MedianCompletionHours = figures.MedianCompletionHours,
		};

		var rows = requests.Select(r => new CatalogRequestRow
		{
			Number = r.Number,
			Title = r.Title,
			Url = r.Url,
			AuthorLogin = r.AuthorLogin,
			CreatedAt = r.CreatedAt,
			Status = r.Status.ToString().ToLowerInvariant(),
			Stewards = r.Stewards,
			FirstContactAt = r.FirstContactAt,
			FirstContactHours = r.FirstContactHours,
			CompletedAt = r.CompletedAt,
			CompletionHours = r.CompletionHours,
		}).ToList();

		return Ok(PaginateWithSummary(rows, summary, page, page_size, DateOnly.FromDateTime(snapshot.FetchedAt)));
	}

	/// <summary>
	/// Tooling Issues
	/// </summary>
	/// <remarks>
	/// Issues raised by the community on the OpenActive tooling repositories
	/// (<c>GitHub:ToolingRepositories</c>), and who is handling them. Newest first.
	///
	/// The things worth knowing:
	///
	/// - **Only community issues count.** Issues opened by a steward or by a bot account are left out.
	///	  So is anything opened more than a year ago (<c>GitHub:LookbackDays</c>, default 365). Pull
	///	  requests are never listed.
	/// - **`assignees`** shows each steward by display name, and anyone else by GitHub login. It is
	///	  empty when nobody is assigned.
	/// - **`closed_at`** is <c>null</c> while the issue is open.
	/// - **An empty list is a real answer.** These repositories receive few issues, so <c>data</c> may
	///	  well be empty.
	///
	/// This is not a monitor: there is no <c>as_of</c> and no trend endpoint. <c>meta.snapshot_date</c>
	/// is the UTC date GitHub was read.
	///
	/// Works without a steward list: every issue then counts as a community issue and assignees are shown
	/// by login. Answers <c>502</c> when GitHub cannot be reached or refuses the request. Successful results are cached until the next daily refresh, varying by all
	/// query parameters.
	/// </remarks>
	/// <param name="page">One-based page number. Default <c>1</c>.</param>
	/// <param name="page_size">Rows per page. Default <c>500</c>, capped at <c>1000</c>.</param>
	/// <param name="repo">One or more repositories, as <c>owner/name</c> or just <c>name</c>, matched ignoring case. Accepts repeated (<c>?repo=a&amp;repo=b</c>) or comma-separated (<c>?repo=a,b</c>) values.</param>
	/// <param name="status">One or both of <c>open</c>, <c>closed</c>, matched ignoring case. Same repeated/comma-separated forms.</param>
	[HttpGet("tooling-issues")]
	[ProducesResponseType(typeof(AdminPage<ToolingIssueRow>), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status502BadGateway)]
	public async Task<ActionResult<AdminPage<ToolingIssueRow>>> ToolingIssuesList(
		int page = 1,
		int page_size = DefaultPageSize,
		[FromQuery] string[]? repo = null,
		[FromQuery] string[]? status = null)
	{
		var stewards = Stewards();

		GitHubSnapshot<GitHubIssue> snapshot;
		try
		{
			snapshot = await source.GetToolingIssues(HttpContext.RequestAborted);
		}
		catch (Exception e) when (IsUpstreamFailure(e))
		{
			return Unavailable("GitHub", e);
		}

		var issues = ToolingIssues.Filter(
			ToolingIssues.Build(snapshot.Items, stewards, source.Cutoff(snapshot.FetchedAt)),
			NormaliseMultiValue(repo),
			NormaliseMultiValue(status));

		var rows = issues.Select(i => new ToolingIssueRow
		{
			Repo = i.Repo,
			Number = i.Number,
			Title = i.Title,
			Url = i.Url,
			AuthorLogin = i.AuthorLogin,
			CreatedAt = i.CreatedAt,
			Status = i.IsOpen ? "open" : "closed",
			Assignees = i.Assignees,
			ClosedAt = i.ClosedAt,
		}).ToList();

		return Ok(Paginate(rows, page, page_size, DateOnly.FromDateTime(snapshot.FetchedAt)));
	}

	#region Utilities

	private StewardDirectory Stewards() =>
		new(gitHub.Stewards.Select(s => (s.Login, s.Name)));

	#endregion
}
