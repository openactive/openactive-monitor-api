namespace MonitorApi.Services.Admin;

/// <summary>
/// One feed's publishing history inside the analysis window, as read from <c>opportunity_ingestion</c>.
/// </summary>
/// <param name="FeedId">The feed identifier, matching <c>feeds.id</c>.</param>
/// <param name="DatasetId">The dataset the feed belongs to, matching <c>feeds.dataset_url</c>.</param>
/// <param name="PublishedDays">
/// Ascending, distinct days on which the feed published at least one updated item, over the whole
/// detection window. Days on which the feed was polled but returned nothing are absent, as are days on
/// which no ingestion run happened.
/// </param>
/// <param name="RecentUpdated">
/// Daily <c>updated</c> counts, keyed by day, covering only the trailing incident-trend window rather
/// than the whole detection window — it exists to render the per-incident <c>trend</c> column, not to
/// detect anything. A missing key means no ingestion row for that day at all, which is different from a
/// key holding zero (the feed was polled and reported nothing).
/// </param>
public sealed record FeedIngestionHistory(
	string FeedId,
	string DatasetId,
	IReadOnlyList<DateOnly> PublishedDays,
	IReadOnlyDictionary<DateOnly, long>? RecentUpdated = null);
