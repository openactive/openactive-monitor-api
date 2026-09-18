namespace MonitorApi.Models.Admin;

/// <summary>
/// Envelope shared by every admin endpoint: a page of rows plus the metadata the dashboard needs to
/// label and paginate them.
/// </summary>
public sealed class AdminPage<T>
{
	public required IReadOnlyList<T> Data { get; init; }

	public required AdminPageMeta Meta { get; init; }
}

/// <summary>
/// Envelope for an admin endpoint that answers with a single object rather than a list — the same
/// <see cref="AdminPageMeta"/> as <see cref="AdminPage{T}"/>, so the dashboard reads <c>meta</c>
/// identically whatever it asked for. The paging fields are fixed at one row on one page.
/// </summary>
public sealed class AdminDocument<T>
{
	public required T Data { get; init; }

	public required AdminPageMeta Meta { get; init; }
}

/// <summary>
/// Envelope for an admin endpoint that answers with a page of rows <em>and</em> an aggregate describing
/// the whole result set behind them. <c>data</c> and <c>meta</c> are exactly what
/// <see cref="AdminPage{T}"/> carries; <c>summary</c> is the extra key.
/// </summary>
/// <remarks>
/// The only departure from the two-key envelope, and a deliberate one: a dashboard showing estate-wide
/// figures above a table of rows would otherwise have to call twice and hope the two answers were
/// computed from the same data. The summary is reduced from the very rows being paged through, so the
/// two can never disagree.
///
/// <c>summary</c> describes every row the request's filters matched, <b>not</b> the page returned, so
/// it does not change as the caller walks the pages.
/// </remarks>
/// <typeparam name="TRow">The row type, as in <see cref="AdminPage{T}"/>.</typeparam>
/// <typeparam name="TSummary">The aggregate describing the whole filtered result set.</typeparam>
public sealed class AdminSummarisedPage<TRow, TSummary>
{
	public required IReadOnlyList<TRow> Data { get; init; }

	public required TSummary Summary { get; init; }

	public required AdminPageMeta Meta { get; init; }
}

/// <summary>Metadata describing which page was returned and how fresh the underlying data is.</summary>
public sealed class AdminPageMeta
{
	/// <summary>The day the analysis was run against — the latest day present in the source table.</summary>
	public required DateOnly SnapshotDate { get; init; }

	/// <summary>When this response was computed. UTC.</summary>
	public required DateTime GeneratedAt { get; init; }

	/// <summary>One-based page number.</summary>
	public required int Page { get; init; }

	public required int PageSize { get; init; }

	/// <summary>Total rows across all pages, before paging.</summary>
	public required int Total { get; init; }
}
