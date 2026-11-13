namespace MonitorApi.Models.Admin;

/// <summary>
/// One calendar month of the ecosystem growth series: its headline figures, and beside each figure worth
/// acting on, the datasets behind it.
/// </summary>
/// <remarks>
/// Every figure counts <em>datasets</em> (dataset URLs), not feeds: a dataset's feeds are aggregated over
/// every day of the month before anything is decided about it.
///
/// The crawl figures (<c>datasets_crawled</c>, <c>_added</c>, <c>_removed</c>) come from what the
/// catalogues listed (<c>feed_ingestion</c>). The activity figures (<c>_with_future</c>,
/// <c>_without_future</c>, <c>_stopped</c>, <c>_stopped_all_runs_failed</c>, <c>_not_ingested</c>) come
/// from what the feeds did (<c>opportunity_ingestion</c>), and are <c>null</c> for a month before that
/// table's history begins. Every crawled dataset is in exactly one of <c>_with_future</c>,
/// <c>_without_future</c> and <c>_not_ingested</c>.
///
/// Each <c>_list</c> has exactly as many entries as the count beside it, and is <c>null</c> exactly when
/// that count is. Entries are ordered by <c>dataset_name</c>, then <c>dataset_url</c>.
/// </remarks>
public sealed class EcosystemGrowthTrendPoint
{
	/// <summary>First day of the calendar month.</summary>
	public required DateOnly PeriodStart { get; init; }

	/// <summary>Last day of the calendar month — even when <see cref="InProgress"/>.</summary>
	public required DateOnly PeriodEnd { get; init; }

	/// <summary>
	/// The month is still being crawled: its figures cover only the days so far and will change daily
	/// until the month ends. Only ever the latest month, and only once it has at least five days of crawls.
	/// </summary>
	public required bool InProgress { get; init; }

	/// <summary>
	/// Days of the month the crawler ran (<c>feed_ingestion</c>). Below the month's length when the
	/// pipeline missed days, or when the history started part way through the month.
	/// </summary>
	public required int DaysWithData { get; init; }

	/// <summary>
	/// Days of the month with feed ingestion data (<c>opportunity_ingestion</c>), the days the activity
	/// figures are measured over. Zero, with those figures <c>null</c>, before that table's history begins.
	/// </summary>
	public required int DaysWithFeedData { get; init; }

	/// <summary>
	/// Datasets the catalogues listed on at least one crawl in the month, whatever came of crawling them.
	/// Agrees with <c>feed_ingestion.number_of_datasets</c>.
	/// </summary>
	public required int DatasetsCrawled { get; init; }

	/// <summary>
	/// Datasets listed this month that were not listed the month before. <c>null</c> when the month before
	/// has no data to compare with.
	/// </summary>
	public required int? DatasetsAdded { get; init; }

	/// <summary>The datasets counted in <see cref="DatasetsAdded"/>.</summary>
	public required IReadOnlyList<EcosystemGrowthDataset>? DatasetsAddedList { get; init; }

	/// <summary>
	/// Datasets listed the month before that are no longer listed by any catalogue this month. <c>null</c>
	/// when the month before has no data to compare with.
	/// </summary>
	public required int? DatasetsRemoved { get; init; }

	/// <summary>The datasets counted in <see cref="DatasetsRemoved"/>.</summary>
	public required IReadOnlyList<EcosystemGrowthDataset>? DatasetsRemovedList { get; init; }

	/// <summary>Ingested datasets where some feed reported future opportunities on some day of the month.</summary>
	public required int? DatasetsWithFuture { get; init; }

	/// <summary>
	/// Ingested datasets where no feed reported a single future opportunity on any day of the month. Always
	/// <c>datasets_crawled - datasets_with_future - datasets_not_ingested</c>.
	/// </summary>
	public required int? DatasetsWithoutFuture { get; init; }

	/// <summary>The datasets counted in <see cref="DatasetsWithoutFuture"/>.</summary>
	public required IReadOnlyList<EcosystemGrowthDataset>? DatasetsWithoutFutureList { get; init; }

	/// <summary>
	/// Ingested datasets whose feeds reported no updated and no deleted items across the whole month.
	/// </summary>
	public required int? DatasetsStopped { get; init; }

	/// <summary>The datasets counted in <see cref="DatasetsStopped"/>.</summary>
	public required IReadOnlyList<EcosystemGrowthDataset>? DatasetsStoppedList { get; init; }

	/// <summary>
	/// Subset of <see cref="DatasetsStopped"/> whose every feed ingestion run in the month ended in
	/// <c>ERROR</c>: unreachable rather than quietly silent.
	/// </summary>
	public required int? DatasetsStoppedAllRunsFailed { get; init; }

	/// <summary>The datasets counted in <see cref="DatasetsStoppedAllRunsFailed"/>.</summary>
	public required IReadOnlyList<EcosystemGrowthDataset>? DatasetsStoppedAllRunsFailedList { get; init; }

	/// <summary>
	/// Datasets the catalogues listed but of which the pipeline ingested not a single feed all month. A
	/// fact about the pipeline, not the publisher: the dataset page may block the crawler, have gone, or
	/// duplicate another dataset whose feeds are ingested under that one's URL. These datasets are in none
	/// of the future or stopped figures, because nothing was read to judge them by.
	/// </summary>
	public required int? DatasetsNotIngested { get; init; }

	/// <summary>The datasets counted in <see cref="DatasetsNotIngested"/>.</summary>
	public required IReadOnlyList<EcosystemGrowthDataset>? DatasetsNotIngestedList { get; init; }
}

/// <summary>A dataset named in one of an <see cref="EcosystemGrowthTrendPoint"/>'s lists.</summary>
public sealed class EcosystemGrowthDataset
{
	/// <summary>The dataset URL, matching <c>feeds.dataset_url</c>.</summary>
	public required string DatasetUrl { get; init; }

	/// <summary>
	/// Display name: its <c>feed_quality.dataset_name</c>, falling back to the host of
	/// <see cref="DatasetUrl"/>. Never empty.
	/// </summary>
	public required string DatasetName { get; init; }

	/// <summary>Slug derived from the publisher name, e.g. <c>pub_freedom-leisure</c>.</summary>
	public required string PublisherId { get; init; }

	/// <summary>Publisher name from <c>feeds</c>; empty when the dataset has no <c>feeds</c> row.</summary>
	public required string PublisherName { get; init; }
}
