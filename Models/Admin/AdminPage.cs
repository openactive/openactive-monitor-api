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
