namespace MonitorApi.Models.Admin;

// Wire contracts for the GitHub stewardship endpoints (/admin/catalog-requests,
// /admin/tooling-issues), kept together in one file.

/// <summary>
/// One dataset addition request: a pull request on the data catalog repository opened by someone
/// other than a steward, with how quickly the stewards answered and finished it.
/// </summary>
public sealed class CatalogRequestRow
{
	/// <summary>Pull request number.</summary>
	public required int Number { get; init; }

	/// <summary>Pull request title.</summary>
	public required string Title { get; init; }

	/// <summary>Link to the pull request on GitHub.</summary>
	public required string Url { get; init; }

	/// <summary>GitHub login of whoever opened it.</summary>
	public required string? AuthorLogin { get; init; }

	/// <summary>When it was opened. UTC.</summary>
	public required DateTime CreatedAt { get; init; }

	/// <summary><c>open</c>, <c>closed</c> (closed without merging) or <c>merged</c>.</summary>
	public required string Status { get; init; }

	/// <summary>
	/// Display names of the stewards involved: anyone who commented, reviewed, was assigned or was asked
	/// to review. Sorted. Empty when no steward has engaged yet.
	/// </summary>
	public required IReadOnlyList<string> Stewards { get; init; }

	/// <summary>
	/// When a steward first replied: the earliest comment, review or review comment by a steward. UTC.
	/// <c>null</c> when no steward has replied.
	/// </summary>
	public required DateTime? FirstContactAt { get; init; }

	/// <summary>Hours from opening to <see cref="FirstContactAt"/>, to one decimal place.</summary>
	public required double? FirstContactHours { get; init; }

	/// <summary>When it was merged, or closed without merging. UTC. <c>null</c> while open.</summary>
	public required DateTime? CompletedAt { get; init; }

	/// <summary>Hours from opening to <see cref="CompletedAt"/>, to one decimal place.</summary>
	public required double? CompletionHours { get; init; }
}

/// <summary>
/// Figures across every dataset addition request the filters matched, not just the page returned.
/// <c>open + merged + closed = total</c> always holds.
/// </summary>
public sealed class CatalogRequestSummary
{
	/// <summary>Requests matched.</summary>
	public required int Total { get; init; }

	/// <summary>Of those, still open.</summary>
	public required int Open { get; init; }

	/// <summary>Of those, merged: the dataset was added to the catalog.</summary>
	public required int Merged { get; init; }

	/// <summary>Of those, closed without merging.</summary>
	public required int Closed { get; init; }

	/// <summary>Open requests no steward has replied to yet.</summary>
	public required int AwaitingFirstContact { get; init; }

	/// <summary>
	/// Median hours to a steward's first reply, across requests that have had one. <c>null</c> when none have.
	/// </summary>
	public required double? MedianFirstContactHours { get; init; }

	/// <summary>
	/// Median hours to merge or close, across requests that are finished. <c>null</c> when none are.
	/// </summary>
	public required double? MedianCompletionHours { get; init; }
}

/// <summary>One issue raised by someone other than a steward on an OpenActive tooling repository.</summary>
public sealed class ToolingIssueRow
{
	/// <summary>The repository, as <c>owner/name</c>.</summary>
	public required string Repo { get; init; }

	/// <summary>Issue number.</summary>
	public required int Number { get; init; }

	/// <summary>Issue title.</summary>
	public required string Title { get; init; }

	/// <summary>Link to the issue on GitHub.</summary>
	public required string Url { get; init; }

	/// <summary>GitHub login of whoever opened it.</summary>
	public required string? AuthorLogin { get; init; }

	/// <summary>When it was opened. UTC.</summary>
	public required DateTime CreatedAt { get; init; }

	/// <summary><c>open</c> or <c>closed</c>.</summary>
	public required string Status { get; init; }

	/// <summary>
	/// Who it is assigned to: a steward's display name, or the GitHub login of an assignee who is not a
	/// steward. Empty when unassigned.
	/// </summary>
	public required IReadOnlyList<string> Assignees { get; init; }

	/// <summary>When it was closed. UTC. <c>null</c> while open.</summary>
	public required DateTime? ClosedAt { get; init; }
}
