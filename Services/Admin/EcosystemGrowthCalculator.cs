namespace MonitorApi.Services.Admin;

/// <summary>
/// One dataset's activity across one calendar month, collapsed from every feed and every ingestion run
/// it had in that month.
/// </summary>
/// <param name="Month">First day of the calendar month.</param>
/// <param name="DatasetId">The dataset URL, as <c>opportunity_ingestion.dataset_id</c> holds it.</param>
/// <param name="Changes">
/// <c>SUM(updated) + SUM(deleted)</c> over all the dataset's feeds and runs in the month, with
/// <c>NULL</c>s read as zero.
/// </param>
/// <param name="MaxFutureOpportunities">
/// The highest <c>total_future_opportunities</c> any of the dataset's feeds reported on any run in the
/// month, with <c>NULL</c> read as zero.
/// </param>
/// <param name="AllRunsFailed">Every ingestion row the dataset had in the month had <c>status = 'ERROR'</c>.</param>
public sealed record DatasetMonthActivity(
	DateOnly Month,
	string DatasetId,
	long Changes,
	long MaxFutureOpportunities,
	bool AllRunsFailed);

/// <summary>
/// A dataset listed by the catalogues on at least one crawl in the month — an entry of
/// <c>feed_ingestion.datasets</c>.
/// </summary>
/// <param name="Month">First day of the calendar month.</param>
/// <param name="DatasetId">The dataset URL.</param>
public sealed record CatalogueListing(DateOnly Month, string DatasetId);

/// <summary>How many days of the month each source holds data for.</summary>
/// <param name="Month">First day of the calendar month.</param>
/// <param name="DaysWithData">Distinct days the crawler ran (<c>feed_ingestion</c>) in the month.</param>
/// <param name="DaysWithFeedData">
/// Distinct days with any feed ingestion row (<c>opportunity_ingestion</c>) in the month. Zero before that
/// table's history begins, which is what makes the future and stopped figures unknown rather than zero.
/// </param>
public sealed record MonthCoverage(DateOnly Month, int DaysWithData, int DaysWithFeedData);

/// <summary>
/// The ecosystem as one calendar month saw it: which datasets were crawled, which came and went against
/// the month before, and which had nothing to offer or published nothing. Dataset ids only — the
/// controller hydrates names.
/// </summary>
/// <remarks>
/// Every list is ordered by dataset id, ordinally, so the result is deterministic. <see cref="Added"/> and
/// <see cref="Removed"/> are <c>null</c>, not empty, when the previous month has no data at all: with no
/// baseline there is nothing to compare with, and reporting every dataset as "added" in the first month
/// of history would read as a burst of growth that never happened.
///
/// <see cref="WithFuture"/>, <see cref="WithoutFuture"/>, <see cref="Stopped"/>,
/// <see cref="StoppedAllRunsFailed"/> and <see cref="NotIngested"/> are <c>null</c> for a month with no
/// feed ingestion data at all — before <c>opportunity_ingestion</c> begins — because nothing is known
/// about what those datasets published.
///
/// Otherwise every crawled dataset is in exactly one of <see cref="WithFuture"/>, <see cref="WithoutFuture"/>
/// and <see cref="NotIngested"/>. A dataset is <see cref="NotIngested"/> when the catalogues listed it but the
/// pipeline ingested not one of its feeds all month. That is a fact about the pipeline, not the publisher:
/// the dataset page may be blocked to the crawler, gone, or a duplicate of another dataset whose feeds are
/// ingested under that one's URL. So it says nothing about future items or publishing, and such a dataset
/// is in neither <see cref="WithoutFuture"/> nor <see cref="Stopped"/>.
///
/// <see cref="InProgress"/> marks the month the snapshot falls in, before its last day: its figures cover
/// only the days so far, and <see cref="PeriodEnd"/> is still the calendar month's last day.
/// </remarks>
public sealed record EcosystemGrowthMonth(
	DateOnly PeriodStart,
	DateOnly PeriodEnd,
	bool InProgress,
	int DaysWithData,
	int DaysWithFeedData,
	IReadOnlyList<string> Crawled,
	IReadOnlyList<string>? Added,
	IReadOnlyList<string>? Removed,
	IReadOnlyList<string>? WithFuture,
	IReadOnlyList<string>? WithoutFuture,
	IReadOnlyList<string>? Stopped,
	IReadOnlyList<string>? StoppedAllRunsFailed,
	IReadOnlyList<string>? NotIngested);

/// <summary>
/// The rules behind the ecosystem growth trend: which months are reported, and what each month says
/// about each dataset. Pure — no BigQuery, no ASP.NET — so every rule is unit tested without credentials.
/// </summary>
/// <remarks>
/// The crawl list is what the catalogues listed (<c>feed_ingestion.datasets</c>), so the figures agree
/// with that table's <c>number_of_datasets</c>. It is not the datasets that have feed ingestion rows: a
/// dataset whose feeds yield nothing is still listed and still tried, and leaving it out would report it
/// as removed from the catalogues when it never left them.
///
/// A month's crawl list is the <em>union</em> of every day in it, never one day's list. There are missing
/// days and partial crawls (2026-11-04 lists 141 datasets against the usual 173), and comparing single
/// days would report a dataset as removed because one crawl happened to miss it.
/// </remarks>
public static class EcosystemGrowthCalculator
{
	/// <summary>Months the trend returns when the caller does not say.</summary>
	public const int DefaultMonths = 12;

	/// <summary>Most months the trend will return, whatever <c>months</c> asks for.</summary>
	public const int MaxMonths = 60;

	/// <summary>
	/// Days of data the month in progress needs before it is reported. Fewer, and its figures say more
	/// about how few days have been crawled than about the ecosystem: almost everything would look
	/// stopped, and the series would lurch at the start of every month.
	/// </summary>
	public const int MinDaysForInProgressMonth = 5;

	/// <summary>First day of the calendar month <paramref name="day"/> falls in.</summary>
	public static DateOnly MonthStart(DateOnly day) => new(day.Year, day.Month, 1);

	/// <summary>Last day of the calendar month starting <paramref name="monthStart"/>.</summary>
	public static DateOnly MonthEnd(DateOnly monthStart) => monthStart.AddMonths(1).AddDays(-1);

	/// <summary>
	/// The latest calendar month that has ended on or before <paramref name="snapshot"/>: the snapshot's
	/// own month when the snapshot is its last day, otherwise the month before.
	/// </summary>
	/// <remarks>
	/// The month in progress is reported only once it has <see cref="MinDaysForInProgressMonth"/> days of
	/// data — see <see cref="InProgressMonth"/> and <see cref="IsReportable"/>.
	/// </remarks>
	public static DateOnly LastCompleteMonth(DateOnly snapshot)
	{
		var month = MonthStart(snapshot);
		return MonthEnd(month) == snapshot ? month : month.AddMonths(-1);
	}

	/// <summary>
	/// The month <paramref name="snapshot"/> falls in when the snapshot is not its last day — the month
	/// still being crawled — otherwise <c>null</c>.
	/// </summary>
	public static DateOnly? InProgressMonth(DateOnly snapshot)
	{
		var month = MonthStart(snapshot);
		return MonthEnd(month) == snapshot ? null : month;
	}

	/// <summary>
	/// Whether a month may be reported: every complete month is, and the month in progress once it holds
	/// at least <see cref="MinDaysForInProgressMonth"/> days of data.
	/// </summary>
	public static bool IsReportable(EcosystemGrowthMonth month) =>
		!month.InProgress || month.DaysWithData >= MinDaysForInProgressMonth;

	/// <summary>
	/// The months the trend evaluates, oldest first: the <paramref name="months"/> complete months ending at
	/// <see cref="LastCompleteMonth"/>, followed by the <see cref="InProgressMonth"/> when there is one.
	/// <paramref name="months"/> is clamped to <c>1</c>..<see cref="MaxMonths"/>.
	/// </summary>
	/// <remarks>
	/// Candidates only. Which of them are reported is decided by <see cref="ReportableMonths"/> once the
	/// data has been read, because both rules it applies — enough days in the month in progress, and no
	/// months from before the history starts — depend on what the table holds.
	/// </remarks>
	public static IReadOnlyList<DateOnly> CandidateMonths(DateOnly snapshot, int months)
	{
		months = Math.Clamp(months, 1, MaxMonths);
		var last = LastCompleteMonth(snapshot);

		var candidates = Enumerable.Range(0, months)
			.Select(i => last.AddMonths(-(months - 1 - i)))
			.ToList();

		if (InProgressMonth(snapshot) is { } current)
		{
			candidates.Add(current);
		}

		return candidates;
	}

	/// <summary>
	/// Narrows evaluated <see cref="CandidateMonths"/> to the series the trend returns: the month in
	/// progress only when <see cref="IsReportable"/>, no months from before the history starts, and at most
	/// the latest <paramref name="months"/> of what remains.
	/// </summary>
	/// <remarks>
	/// The candidates hold one month more than asked for whenever a month is in progress, so the series is
	/// <paramref name="months"/> long whether or not that month makes the cut.
	/// </remarks>
	public static IReadOnlyList<EcosystemGrowthMonth> ReportableMonths(
		IReadOnlyList<EcosystemGrowthMonth> evaluated,
		int months)
	{
		months = Math.Clamp(months, 1, MaxMonths);

		return DropMonthsBeforeData(evaluated.Where(IsReportable).ToList())
			.TakeLast(months)
			.ToList();
	}

	/// <summary>
	/// Evaluates each of <paramref name="months"/> against the catalogue listings and activity rows, in the
	/// order given.
	/// </summary>
	/// <param name="catalogue">
	/// The datasets the catalogues listed, one row per dataset per month. Must include the month before the
	/// first of <paramref name="months"/> if that month is to have a baseline for added and removed.
	/// </param>
	/// <param name="activity">
	/// Feed ingestion activity, one row per dataset per month. A dataset with activity but no listing is
	/// still counted as crawled — its feeds were ingested, so it was tried.
	/// </param>
	/// <param name="coverage">Days with data per month. A month with no entry has none.</param>
	/// <param name="months">First days of the months to evaluate.</param>
	/// <param name="snapshot">
	/// The latest day of data. A month that ends after it is marked <see cref="EcosystemGrowthMonth.InProgress"/>;
	/// <c>null</c> treats every month as complete.
	/// </param>
	public static IReadOnlyList<EcosystemGrowthMonth> Compute(
		IEnumerable<CatalogueListing> catalogue,
		IEnumerable<DatasetMonthActivity> activity,
		IEnumerable<MonthCoverage> coverage,
		IReadOnlyList<DateOnly> months,
		DateOnly? snapshot = null)
	{
		var byMonth = activity
			.GroupBy(a => MonthStart(a.Month))
			.ToDictionary(
				g => g.Key,
				// One row per dataset: should the caller hand in two for the same dataset and month, they
				// are folded together rather than counted twice.
				g => g
					.GroupBy(a => a.DatasetId, StringComparer.Ordinal)
					.ToDictionary(
						d => d.Key,
						d => new DatasetMonthActivity(
							g.Key,
							d.Key,
							d.Sum(a => a.Changes),
							d.Max(a => a.MaxFutureOpportunities),
							d.All(a => a.AllRunsFailed)),
						StringComparer.Ordinal));

		var listed = catalogue
			.GroupBy(c => MonthStart(c.Month))
			.ToDictionary(g => g.Key, g => g.Select(c => c.DatasetId).ToHashSet(StringComparer.Ordinal));

		var days = coverage
			.GroupBy(c => MonthStart(c.Month))
			.ToDictionary(g => g.Key, g => (Crawl: g.Max(c => c.DaysWithData), Feed: g.Max(c => c.DaysWithFeedData)));

		IReadOnlySet<string> Crawled(DateOnly month)
		{
			var crawled = new HashSet<string>(listed.GetValueOrDefault(month) ?? [], StringComparer.Ordinal);
			crawled.UnionWith(byMonth.GetValueOrDefault(month)?.Keys ?? Enumerable.Empty<string>());
			return crawled;
		}

		return months
			.Select(MonthStart)
			.Select(month => Evaluate(
				month,
				snapshot is { } latest && MonthEnd(month) > latest,
				days.GetValueOrDefault(month),
				Crawled(month),
				Crawled(month.AddMonths(-1)),
				byMonth.GetValueOrDefault(month)))
			.ToList();
	}

	/// <summary>
	/// Drops the months at the front of <paramref name="months"/> that have no data at all — the months
	/// the trend window reaches back into from before the ingestion history began. A month with no data
	/// <em>after</em> the history has started is kept, so a gap in the pipeline shows as a gap in the chart.
	/// </summary>
	public static IReadOnlyList<EcosystemGrowthMonth> DropMonthsBeforeData(IReadOnlyList<EcosystemGrowthMonth> months) =>
		months.SkipWhile(m => m.DaysWithData == 0 && m.DaysWithFeedData == 0 && m.Crawled.Count == 0).ToList();

	private static EcosystemGrowthMonth Evaluate(
		DateOnly month,
		bool inProgress,
		(int Crawl, int Feed) days,
		IReadOnlySet<string> crawled,
		IReadOnlySet<string> previous,
		IReadOnlyDictionary<string, DatasetMonthActivity>? activity)
	{
		static List<string> Sorted(IEnumerable<string> ids) => ids.Order(StringComparer.Ordinal).ToList();

		List<string>? added = null;
		List<string>? removed = null;

		if (previous.Count > 0)
		{
			added = Sorted(crawled.Where(id => !previous.Contains(id)));
			removed = Sorted(previous.Where(id => !crawled.Contains(id)));
		}

		// Nothing is known about publishing in a month without a single feed ingestion row: leave those
		// figures unknown rather than report every dataset as stopped.
		if (days.Feed == 0 && activity is not { Count: > 0 })
		{
			return new EcosystemGrowthMonth(
				month, MonthEnd(month), inProgress, days.Crawl, days.Feed,
				Sorted(crawled), added, removed,
				WithFuture: null, WithoutFuture: null, Stopped: null, StoppedAllRunsFailed: null, NotIngested: null);
		}

		var rows = activity ?? new Dictionary<string, DatasetMonthActivity>(StringComparer.Ordinal);

		// The future and publishing figures judge only what was ingested; the rest are reported apart.
		var ingested = crawled.Where(rows.ContainsKey).Select(id => rows[id]).ToList();
		var stopped = ingested.Where(a => a.Changes == 0).ToList();

		return new EcosystemGrowthMonth(
			month,
			MonthEnd(month),
			inProgress,
			days.Crawl,
			days.Feed,
			Sorted(crawled),
			added,
			removed,
			Sorted(ingested.Where(a => a.MaxFutureOpportunities > 0).Select(a => a.DatasetId)),
			Sorted(ingested.Where(a => a.MaxFutureOpportunities <= 0).Select(a => a.DatasetId)),
			Sorted(stopped.Select(a => a.DatasetId)),
			Sorted(stopped.Where(a => a.AllRunsFailed).Select(a => a.DatasetId)),
			Sorted(crawled.Where(id => !rows.ContainsKey(id))));
	}
}
