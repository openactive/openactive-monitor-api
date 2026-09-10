namespace MonitorApi.Models.Admin;

/// <summary>
/// A dataset whose feeds have all stopped publishing, as reported to the admin dashboard.
/// </summary>
/// <remarks>
/// The dataset-scoped counterpart of <see cref="StallIncident"/>. It shares the spine every monitor's
/// incidents carry — <c>monitor_id</c>, <c>publisher_id</c>, <c>publisher_name</c>,
/// <c>first_detected</c>, <c>days_open</c>, <c>consecutive_days</c>, <c>past_threshold</c>,
/// <c>status</c>, <c>last_contacted</c>, <c>trend</c> — and identifies a dataset instead of a feed.
///
/// Deliberately absent, rather than present and always null, for the reasons
/// <see cref="OrphanedChildrenIncident"/> sets out:
///
/// <list type="bullet">
/// <item>
/// <c>feed_id</c>, <c>feed_name</c>, <c>feed_type</c> and <c>feed_url</c>, because the entity here is
/// the dataset: every one of its feeds is stalled, and the publisher fixes the pipeline once. The
/// individual feeds are in <c>detail.feeds</c>.
/// </item>
/// <item>
/// <c>quality_score</c>, because <c>feed_quality.score</c> is per-feed and averaging a dataset's feeds
/// would invent a figure nobody asked for.
/// </item>
/// </list>
/// </remarks>
public sealed class DatasetStallIncident
{
	/// <summary>Always <c>dataset_stall</c>; identifies which monitor raised the incident.</summary>
	public required string MonitorId { get; init; }

	/// <summary>Slug derived from the publisher name, e.g. <c>pub_freedom-leisure</c>.</summary>
	public required string PublisherId { get; init; }

	public required string PublisherName { get; init; }

	/// <summary>The dataset, matching <c>feeds.dataset_url</c>. The incident's identity.</summary>
	public required string DatasetUrl { get; init; }

	/// <summary>
	/// Display name for the dataset: its <c>feed_quality.dataset_name</c>, falling back to the host of
	/// <see cref="DatasetUrl"/>. Never empty.
	/// </summary>
	public required string DatasetName { get; init; }

	/// <summary>
	/// Feeds seen for the dataset in the ingestion history — all of them silent, which is what makes
	/// this a dataset-wide stall rather than a set of single-feed ones.
	/// </summary>
	public required int FeedCount { get; init; }

	/// <summary>
	/// The day the dataset went quiet: the last day any of its feeds published an updated item.
	/// </summary>
	public required DateOnly FirstDetected { get; init; }

	/// <summary>Days the incident has been open as of the snapshot date.</summary>
	public required int DaysOpen { get; init; }

	/// <summary>Consecutive days with no feed of the dataset publishing, as of the snapshot date.</summary>
	public required int ConsecutiveDays { get; init; }

	/// <summary>Whether the incident has passed the escalation threshold.</summary>
	public required bool PastThreshold { get; init; }

	/// <summary>
	/// Workflow state. Currently always <c>open</c>: outreach states such as <c>awaiting_reply</c>
	/// require an incident-tracking store, which does not exist yet.
	/// </summary>
	public required string Status { get; init; }

	/// <summary>Always <c>null</c> until contact tracking exists. See <see cref="Status"/>.</summary>
	public required DateOnly? LastContacted { get; init; }

	/// <summary>
	/// The dataset's daily <c>updated</c> totals over the trailing ten days, oldest first, ending on the
	/// snapshot date — every feed's counts added up. Always ten entries, so entry <c>i</c> is the same
	/// day for every incident in the response. <c>null</c> means no feed of the dataset recorded an
	/// ingestion run that day; <c>0</c> means at least one was polled and the dataset published nothing.
	/// </summary>
	public required IReadOnlyList<long?> Trend { get; init; }

	public required DatasetStallIncidentDetail Detail { get; init; }
}

/// <summary>Monitor-specific evidence for a dataset-wide stall.</summary>
public sealed class DatasetStallIncidentDetail
{
	/// <summary>
	/// Last day on which any feed of the dataset published an updated item — the same day as
	/// <see cref="DatasetStallIncident.FirstDetected"/>.
	/// </summary>
	public required DateOnly LastModified { get; init; }

	/// <summary>
	/// Every feed of the dataset, most recently active first. The first entry is the feed that went
	/// quiet last and therefore dates the incident; the rest show whether the dataset stopped all at
	/// once or wound down feed by feed.
	/// </summary>
	public required IReadOnlyList<DatasetStallFeed> Feeds { get; init; }
}

/// <summary>One feed's silence within a dataset-wide stall.</summary>
public sealed class DatasetStallFeed
{
	public required string FeedId { get; init; }

	/// <summary>Short feed name — the last path segment of the feed URL.</summary>
	public required string FeedName { get; init; }

	/// <summary>
	/// The feed's last publishing day, or <c>null</c> when it never published inside the lookback
	/// window — a feed that has never been seen to work, rather than one that stopped.
	/// </summary>
	public required DateOnly? LastPublished { get; init; }

	/// <summary>
	/// Days this feed has been silent, <c>null</c> alongside a <c>null</c>
	/// <see cref="LastPublished"/>. Never fewer than the incident's own <c>consecutive_days</c>.
	/// </summary>
	public required int? ConsecutiveDays { get; init; }
}
