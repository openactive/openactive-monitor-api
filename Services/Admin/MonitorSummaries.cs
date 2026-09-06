namespace MonitorApi.Services.Admin;

/// <summary>One day of a monitor's open/past-threshold counts, as the dashboard summary consumes them.</summary>
/// <remarks>
/// Deliberately monitor-agnostic: every monitor that reports a daily trend collapses to this shape, so
/// the summary does not have to know what any one of them detects.
/// </remarks>
public sealed record MonitorTrendPoint(DateOnly Date, int OpenCount, int PastThresholdCount);

/// <summary>
/// One monitor's contribution to the dashboard summary, reduced from its daily trend.
/// </summary>
/// <param name="MonitorId">Identifier of the monitor.</param>
/// <param name="Count">Open incidents on the most recent day of the trend.</param>
/// <param name="PastThresholdCount">Subset of <paramref name="Count"/> past the escalation threshold.</param>
/// <param name="Sparkline">Trailing open counts, oldest first, ending on the most recent day.</param>
/// <param name="CountDelta">
/// Change in open count against the previous day, or <c>null</c> when the trend has only one day.
/// </param>
/// <param name="PastThresholdDelta">The same comparison for the past-threshold count.</param>
public sealed record MonitorSummarySnapshot(
	string MonitorId,
	int Count,
	int PastThresholdCount,
	IReadOnlyList<int> Sparkline,
	int? CountDelta,
	int? PastThresholdDelta);

/// <summary>
/// Reduces a monitor's daily trend to the figures the dashboard summary shows. Pure — no BigQuery, no
/// ASP.NET — so the arithmetic is unit tested without credentials.
/// </summary>
public static class MonitorSummaries
{
	/// <summary>Trailing days shown in a monitor's sparkline.</summary>
	public const int SparklineDays = 7;

	/// <summary>
	/// Summarises one monitor, or returns <c>null</c> when <paramref name="trend"/> is empty and there is
	/// therefore nothing to report.
	/// </summary>
	/// <param name="monitorId">Identifier echoed onto the summary.</param>
	/// <param name="trend">The monitor's daily counts, oldest first.</param>
	/// <param name="sparklineDays">Trailing days to keep in the sparkline. Defaults to <see cref="SparklineDays"/>.</param>
	public static MonitorSummarySnapshot? Summarise(
		string monitorId,
		IReadOnlyList<MonitorTrendPoint> trend,
		int sparklineDays = SparklineDays)
	{
		if (trend.Count == 0)
		{
			return null;
		}

		var latest = trend[^1];
		var previous = trend.Count >= 2 ? trend[^2] : null;

		var sparkline = trend
			.Skip(Math.Max(0, trend.Count - Math.Max(1, sparklineDays)))
			.Select(p => p.OpenCount)
			.ToList();

		return new MonitorSummarySnapshot(
			monitorId,
			latest.OpenCount,
			latest.PastThresholdCount,
			sparkline,
			previous is null ? null : latest.OpenCount - previous.OpenCount,
			previous is null ? null : latest.PastThresholdCount - previous.PastThresholdCount);
	}

	/// <summary>
	/// Adds a delta up across monitors for the dashboard's headline figures. <c>null</c> when no monitor
	/// had a previous day to compare against; monitors that individually lack one are skipped rather
	/// than counted as zero.
	/// </summary>
	public static int? TotalDelta(
		IEnumerable<MonitorSummarySnapshot> monitors,
		Func<MonitorSummarySnapshot, int?> select)
	{
		var deltas = monitors.Select(select).OfType<int>().ToList();
		return deltas.Count == 0 ? null : deltas.Sum();
	}
}
