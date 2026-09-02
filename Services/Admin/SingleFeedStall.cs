namespace MonitorApi.Services.Admin;

/// <summary>
/// A detected single-feed stall, before feed metadata is attached.
/// </summary>
public sealed record SingleFeedStall
{
	public required string FeedId { get; init; }

	public required string DatasetId { get; init; }

	/// <summary>Last day the feed published anything — the day it went quiet.</summary>
	public required DateOnly LastPublished { get; init; }

	/// <summary>Consecutive silent days as of the snapshot date.</summary>
	public required int ConsecutiveDays { get; init; }

	/// <summary>Whether <see cref="ConsecutiveDays"/> has reached the past-threshold limit.</summary>
	public required bool PastThreshold { get; init; }

	/// <summary>
	/// Daily <c>updated</c> counts over the trailing trend window, oldest first. A <c>null</c> entry
	/// means no ingestion run recorded that day; a zero means the feed was polled and published nothing.
	/// </summary>
	public required IReadOnlyList<long?> Trend { get; init; }
}

/// <summary>One day of the single-feed stall trend.</summary>
public sealed record SingleFeedStallTrendPoint(DateOnly Date, int OpenCount, int PastThresholdCount);
