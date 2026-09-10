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
	IReadOnlyDictionary<DateOnly, long>? RecentUpdated = null)
{
	/// <summary>
	/// Most recent day the feed published at or before <paramref name="asOf"/>, or <c>null</c> if there
	/// is none inside the loaded window.
	/// </summary>
	/// <remarks>
	/// Lives on the history rather than in one detector because both stall monitors ask the same
	/// question of the same record — the single-feed monitor per feed, the dataset monitor across a
	/// dataset's feeds — and they must answer it identically for their results to partition the silence
	/// between them. A binary search rather than a scan because the trend endpoints ask it once per feed
	/// per day of the series.
	/// </remarks>
	public DateOnly? LastPublishedOnOrBefore(DateOnly asOf)
	{
		var low = 0;
		var high = PublishedDays.Count - 1;
		DateOnly? found = null;

		while (low <= high)
		{
			var mid = low + ((high - low) / 2);
			if (PublishedDays[mid] <= asOf)
			{
				found = PublishedDays[mid];
				low = mid + 1;
			}
			else
			{
				high = mid - 1;
			}
		}

		return found;
	}
}
