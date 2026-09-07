namespace MonitorApi.Services.Admin;

/// <summary>
/// Tunable thresholds for the <c>single_feed_stall</c> monitor. All values are in days.
/// </summary>
public sealed record SingleFeedStallThresholds
{
	/// <summary>
	/// How far back a feed must have published at least once to be considered live enough to raise a
	/// stall for. A feed silent for longer than this is treated as retired, not stalled.
	/// </summary>
	public int LookbackDays { get; init; } = 120;

	/// <summary>Consecutive silent days after which a feed counts as stalled (an open incident).</summary>
	public int StallDays { get; init; } = 5;

	/// <summary>Consecutive silent days after which an open incident is flagged as past threshold.</summary>
	public int PastThresholdDays { get; init; } = 7;

	/// <summary>Days of history returned by the trend endpoint.</summary>
	public int TrendDays { get; init; } = 30;

	/// <summary>
	/// Length of the per-incident <c>trend</c> array — how many trailing days of <c>updated</c> counts
	/// each incident reports.
	/// </summary>
	public int IncidentTrendDays { get; init; } = 10;

	/// <summary>
	/// Days of ingestion history the queries must load to answer a request with these thresholds:
	/// the trend endpoint evaluates the lookback window at each of the last <see cref="TrendDays"/> days.
	/// </summary>
	public int RequiredHistoryDays => LookbackDays + Math.Max(TrendDays, IncidentTrendDays);

	/// <summary>
	/// Past threshold can never be looser than the stall threshold, otherwise
	/// <c>past_threshold_count</c> could exceed <c>open_count</c>.
	/// </summary>
	public int EffectivePastThresholdDays => Math.Max(PastThresholdDays, StallDays);
}
