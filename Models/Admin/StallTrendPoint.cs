namespace MonitorApi.Models.Admin;

/// <summary>Open single-feed stall counts on one day.</summary>
public sealed class StallTrendPoint
{
	public required DateOnly Date { get; init; }

	/// <summary>Stalls open on this day.</summary>
	public required int OpenCount { get; init; }

	/// <summary>Subset of <see cref="OpenCount"/> that had passed the escalation threshold.</summary>
	public required int PastThresholdCount { get; init; }
}
