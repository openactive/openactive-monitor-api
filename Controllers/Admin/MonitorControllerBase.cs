using Microsoft.Extensions.Options;
using MonitorApi.Services.Admin;

namespace MonitorApi.Controllers.Admin;

/// <summary>
/// Base class for admin controllers that run the feed-health monitors, holding the loading of the
/// <c>opportunity_ingestion</c> publishing history those monitors all detect against.
/// </summary>
/// <remarks>
/// Sits between <see cref="AdminControllerBase"/> and the monitor controllers so that the per-monitor
/// endpoints and the cross-monitor summary read the same history the same way, rather than each
/// controller growing its own copy of the window arithmetic.
/// </remarks>
public abstract class MonitorControllerBase(IOptions<BigQueryOptions> bigQueryOptions, IOptions<ApiOptions> apiOptions)
	: AdminControllerBase(bigQueryOptions, apiOptions)
{
	/// <summary>
	/// The day to evaluate the monitors against: the caller's <paramref name="asOf"/>, else the latest
	/// day in the ingestion table. Returns <c>null</c> when the table is empty.
	/// </summary>
	protected async Task<DateOnly?> ResolveSnapshotDate(DateOnly? asOf)
	{
		if (asOf is not null)
		{
			return asOf;
		}

		var row = await QuerySingle(IngestionHistoryQuery.SnapshotDateSql(Fq(Tables.OpportunityIngestion)));
		return row?.GetValueOrDefault("snapshot_date") is DateTime snapshot
			? DateOnly.FromDateTime(snapshot)
			: null;
	}

	/// <summary>Loads per-feed publishing history for the window the monitors need.</summary>
	/// <param name="snapshotDate">The day the analysis runs against; the window ends here.</param>
	/// <param name="historyDays">Days of publishing history to load for detection.</param>
	/// <param name="trendDays">
	/// Trailing days for which the daily <c>updated</c> counts are also loaded, to fill the per-incident
	/// trend column.
	/// </param>
	/// <param name="ignoreFirstIngestionDate">
	/// When set, drops the earliest <c>ingestion_date</c> in the table so the initial bulk data load is
	/// not counted as a day the feeds published.
	/// </param>
	protected async Task<List<FeedIngestionHistory>> LoadHistories(DateOnly snapshotDate, int historyDays, int trendDays, bool ignoreFirstIngestionDate = false)
	{
		var rows = await Query(
			IngestionHistoryQuery.HistorySql(Fq(Tables.OpportunityIngestion), ignoreFirstIngestionDate),
			IngestionHistoryQuery.HistoryParameters(
				snapshotDate.AddDays(-historyDays),
				snapshotDate,
				snapshotDate.AddDays(-(trendDays - 1))));

		return await rows.Select(IngestionHistoryQuery.ParseHistory).ToListAsync();
	}

	/// <summary>
	/// Loads per-feed daily ingestion status for the window the error monitors need — the sibling of
	/// <see cref="LoadHistories"/> for monitors that detect on the outcome of each run rather than on
	/// how much it published.
	/// </summary>
	/// <param name="snapshotDate">The day the analysis runs against; the window ends here.</param>
	/// <param name="historyDays">Days of status history to load before <paramref name="snapshotDate"/>.</param>
	/// <remarks>
	/// The earliest ingestion date is kept, unlike in <see cref="LoadHistories"/>: the initial bulk load
	/// inflates what a feed published that day, but the status it reported is a real ingestion outcome.
	/// </remarks>
	protected async Task<List<FeedStatusHistory>> LoadStatusHistories(DateOnly snapshotDate, int historyDays)
	{
		var rows = await Query(
			IngestionStatusQuery.StatusHistorySql(Fq(Tables.OpportunityIngestion)),
			IngestionStatusQuery.StatusHistoryParameters(snapshotDate.AddDays(-historyDays), snapshotDate));

		return await rows.Select(IngestionStatusQuery.ParseStatusHistory).ToListAsync();
	}
}
