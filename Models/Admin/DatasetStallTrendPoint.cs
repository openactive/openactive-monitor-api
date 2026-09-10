namespace MonitorApi.Models.Admin;

/// <summary>Open dataset-wide stall counts on one day.</summary>
/// <remarks>
/// The same shape as <see cref="StallTrendPoint"/> and <see cref="IngestionErrorTrendPoint"/>, kept as
/// its own type so each monitor's series is named after the monitor in the OpenAPI document and can
/// gain a monitor-specific field without disturbing the others.
/// </remarks>
public sealed class DatasetStallTrendPoint
{
	public required DateOnly Date { get; init; }

	/// <summary>Dataset-wide stalls open on this day.</summary>
	public required int OpenCount { get; init; }

	/// <summary>Subset of <see cref="OpenCount"/> that had passed the escalation threshold.</summary>
	public required int PastThresholdCount { get; init; }
}
