namespace MonitorApi.Models.Admin;

/// <summary>
/// One captured measurement: the writable half of a metric snapshot, and the shape a caller posts.
/// </summary>
/// <remarks>
/// A row is a <em>source field</em>, not a finished measure. The monthly KPI report computes its
/// figures from these rows, so a measure whose definition later changes can be recomputed for every
/// period already captured. Storing the finished percentage instead would make that impossible.
/// </remarks>
public class MetricSnapshotWrite
{
	/// <summary>The month this figure describes, given as its first day, e.g. <c>2026-11-01</c>.</summary>
	public required DateOnly Period { get; init; }

	/// <summary>Where the figure came from, e.g. <c>feed_quality</c>, <c>activity_coverage</c>, <c>summary</c>.</summary>
	public required string Source { get; init; }

	/// <summary>The field within that source, e.g. <c>completeness_location</c>, <c>labelled_percentage</c>.</summary>
	public required string Field { get; init; }

	/// <summary>The figure itself. Counts are whole numbers carried in the same column as rates.</summary>
	public double? Value { get; init; }

	/// <summary>
	/// What <see cref="Value"/> is out of, where it is a rate or a volume-weighted mean — the feed count
	/// behind a completeness figure, for instance. Null for a plain count.
	/// </summary>
	public double? Denominator { get; init; }

	/// <summary>
	/// The <c>meta.snapshot_date</c> of the response this figure was read from, so a period can be
	/// traced back to the day's data that produced it.
	/// </summary>
	public DateOnly? SnapshotDate { get; init; }
}

/// <summary>One captured measurement as the API returns it.</summary>
public sealed class MetricSnapshotRow : MetricSnapshotWrite
{
	/// <summary>When the row was written, UTC. Set by BigQuery on insert, never by the caller.</summary>
	public DateTime? CapturedAt { get; init; }
}
