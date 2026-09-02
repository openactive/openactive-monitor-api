namespace MonitorApi.Services.Admin;

/// <summary>
/// One feed's publishing history inside the analysis window, as read from <c>opportunity_ingestion</c>.
/// </summary>
/// <param name="FeedId">The feed identifier, matching <c>feeds.id</c>.</param>
/// <param name="DatasetId">The dataset the feed belongs to, matching <c>feeds.dataset_url</c>.</param>
/// <param name="PublishedDays">
/// Ascending, distinct days on which the feed published at least one updated item. Days on which the
/// feed was polled but returned nothing are absent, as are days on which no ingestion run happened.
/// </param>
public sealed record FeedIngestionHistory(
	string FeedId,
	string DatasetId,
	IReadOnlyList<DateOnly> PublishedDays);
