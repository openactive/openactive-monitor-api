namespace MonitorApi.Models.Admin;

/// <summary>
/// Headline figures for the admin dashboard's landing page: how much of the estate is monitored, how
/// much of it is currently unhealthy, and how each monitor is trending.
/// </summary>
public sealed class AdminSummary
{
	/// <summary>
	/// Publishers covered by monitoring. Counted as datasets — one dataset per publisher — so this
	/// always equals <see cref="Datasets"/>.
	/// </summary>
	public required long PublishersMonitored { get; init; }

	/// <summary>
	/// Publishers with at least one feed that failed ingestion today, counted as distinct datasets with
	/// an <c>ERROR</c> row in <c>opportunity_ingestion</c> dated today or later.
	/// </summary>
	public required long PublishersWithIssues { get; init; }

	/// <summary>
	/// Incidents open across all monitors. Always <c>null</c>: incidents are derived per request rather
	/// than tracked, so there is no cross-monitor total to report yet.
	/// </summary>
	public required long? OpenIncidents { get; init; }

	/// <summary>
	/// Open incidents past their escalation threshold across all monitors. Always <c>null</c>, for the
	/// same reason as <see cref="OpenIncidents"/>; the per-monitor figure is on each
	/// <see cref="MonitorSummary"/>.
	/// </summary>
	public required long? PastThreshold { get; init; }

	/// <summary>Feeds covered by monitoring, from the latest <c>feed_ingestion</c> run.</summary>
	public required long Feeds { get; init; }

	/// <summary>Datasets covered by monitoring, from the latest <c>feed_ingestion</c> run.</summary>
	public required long Datasets { get; init; }

	/// <summary>One entry per monitor, with its current counts and a short sparkline.</summary>
	public required IReadOnlyList<MonitorSummary> Monitors { get; init; }

	/// <summary>
	/// Day-on-day change in the monitors' open counts — the sum across <see cref="Monitors"/> of the
	/// latest day's open count minus the previous day's. Positive means things got worse.
	/// <c>null</c> when no monitor has a previous day to compare against.
	/// </summary>
	public required int? PublishersWithIssuesDelta { get; init; }

	/// <summary>
	/// Day-on-day change in <see cref="OpenIncidents"/>. Always <c>null</c> while that figure is.
	/// </summary>
	public required int? OpenIncidentsDelta { get; init; }

	/// <summary>
	/// Day-on-day change in the monitors' past-threshold counts, summed across <see cref="Monitors"/>.
	/// <c>null</c> when no monitor has a previous day to compare against.
	/// </summary>
	public required int? PastThresholdDelta { get; init; }
}

/// <summary>One monitor's current state on the dashboard summary.</summary>
public sealed class MonitorSummary
{
	/// <summary>Identifier of the monitor, matching the <c>monitor_id</c> on its incidents.</summary>
	public required string MonitorId { get; init; }

	/// <summary>Incidents open on the snapshot day — the last value of <see cref="Sparkline"/>.</summary>
	public required int Count { get; init; }

	/// <summary>Subset of <see cref="Count"/> that has passed the monitor's escalation threshold.</summary>
	public required int PastThresholdCount { get; init; }

	/// <summary>
	/// Open counts over the trailing week, oldest first, ending on the snapshot day. Shorter than seven
	/// entries only when less history is available.
	/// </summary>
	public required IReadOnlyList<int> Sparkline { get; init; }
}
