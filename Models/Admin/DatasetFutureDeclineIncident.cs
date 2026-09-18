namespace MonitorApi.Models.Admin;

/// <summary>
/// A dataset whose forward supply is draining while its feeds keep ingesting, as reported to the admin
/// dashboard.
/// </summary>
/// <remarks>
/// Shares the spine every monitor's incidents carry — <c>monitor_id</c>, <c>publisher_id</c>,
/// <c>publisher_name</c>, <c>first_detected</c>, <c>days_open</c>, <c>consecutive_days</c>,
/// <c>past_threshold</c>, <c>status</c>, <c>last_contacted</c>, <c>trend</c> — and identifies a dataset,
/// like <see cref="DatasetStallIncident"/> and <see cref="OrphanedChildrenIncident"/>.
///
/// Deliberately absent, rather than present and always null, for the reasons
/// <see cref="OrphanedChildrenIncident"/> sets out:
///
/// <list type="bullet">
/// <item>
/// <c>feed_id</c>, <c>feed_name</c>, <c>feed_type</c> and <c>feed_url</c>, because the entity here is
/// the dataset: it is the publisher who is contacted, and one dataset routinely has several feeds
/// shedding supply at once. The feeds responsible are in <c>detail.feeds</c>, each with its own figures.
/// </item>
/// <item>
/// <c>quality_score</c>, because <c>feed_quality.score</c> is per-feed and averaging a dataset's feeds
/// would invent a figure nobody asked for.
/// </item>
/// </list>
/// </remarks>
public sealed class DatasetFutureDeclineIncident
{
	/// <summary>Always <c>dataset_future_decline</c>; identifies which monitor raised the incident.</summary>
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
	/// Feeds of the dataset that are losing forward supply — not the dataset's whole feed count. A
	/// dataset's healthy feeds are not part of the incident and are not counted here.
	/// </summary>
	public required int FeedCount { get; init; }

	/// <summary>
	/// The day the decline began: the start of the unbroken run of falls that ends the window, or the day
	/// the steepest drop fell from when the window does not end in a fall. Never earlier than
	/// <c>window_days</c> before the snapshot date.
	/// </summary>
	public required DateOnly FirstDetected { get; init; }

	/// <summary>Days the incident has been open as of the snapshot date.</summary>
	public required int DaysOpen { get; init; }

	/// <summary>Days the dataset's forward supply has been falling, as of the snapshot date.</summary>
	public required int ConsecutiveDays { get; init; }

	/// <summary>
	/// Whether the net decline across the window has reached the escalation threshold. Set from the
	/// percentage, not from the raw count, so a small publisher losing most of its supply escalates
	/// alongside a large one.
	/// </summary>
	public required bool PastThreshold { get; init; }

	/// <summary>
	/// Workflow state. Currently always <c>open</c>: outreach states such as <c>awaiting_reply</c>
	/// require an incident-tracking store, which does not exist yet.
	/// </summary>
	public required string Status { get; init; }

	/// <summary>Always <c>null</c> until contact tracking exists. See <see cref="Status"/>.</summary>
	public required DateOnly? LastContacted { get; init; }

	/// <summary>
	/// Daily <c>total_future_opportunities</c> across the contributing feeds over the trailing ten days,
	/// oldest first, ending on the snapshot date — the falling line the incident is about. Always ten
	/// entries, so entry <c>i</c> is the same day for every incident in the response. <c>null</c> means no
	/// contributing feed completed an ingestion run that day, which is not the same as a genuine zero.
	/// </summary>
	public required IReadOnlyList<long?> Trend { get; init; }

	public required DatasetFutureDeclineIncidentDetail Detail { get; init; }
}

/// <summary>Monitor-specific evidence for a falling forward supply.</summary>
public sealed class DatasetFutureDeclineIncidentDetail
{
	/// <summary>
	/// Which rule fired: <c>monotonic_decline</c> when every observation in the window was lower than the
	/// one before it, <c>sharp_drop</c> when a single step lost at least <c>drop_percent</c>, or
	/// <c>both</c> when the dataset's feeds between them did each.
	/// </summary>
	public required string Reason { get; init; }

	/// <summary>Trailing days the figures below are measured over — the request's <c>window_days</c>.</summary>
	public required int WindowDays { get; init; }

	/// <summary>
	/// Forward opportunities the contributing feeds carried at the start of the window. Only those feeds,
	/// so this is not the dataset's total supply.
	/// </summary>
	public required long StartTotal { get; init; }

	/// <summary>The same feeds' forward opportunities at the end of the window.</summary>
	public required long CurrentTotal { get; init; }

	/// <summary><see cref="StartTotal"/> minus <see cref="CurrentTotal"/>: what has been lost.</summary>
	public required long Drop { get; init; }

	/// <summary><see cref="Drop"/> as a percentage of <see cref="StartTotal"/>, to two decimal places.</summary>
	public required double DropPercent { get; init; }

	/// <summary>
	/// Longer window the decline had to survive to be reported at all — the request's
	/// <c>qualify_window_days</c>.
	/// </summary>
	public required int QualifyWindowDays { get; init; }

	/// <summary>The contributing feeds' supply at the start of that longer window.</summary>
	public required long QualifyStartTotal { get; init; }

	/// <summary>
	/// <see cref="CurrentTotal"/> measured back to <see cref="QualifyStartTotal"/>, as a percentage. One of
	/// the two ways a feed qualifies: at or above <c>qualify_drop_percent</c> it is reported however it
	/// published, and below it only if its <c>delta_in_window</c> was negative.
	/// </summary>
	public required double QualifyDropPercent { get; init; }

	/// <summary>
	/// The feeds that raised the incident, largest loss first. Feeds of the same dataset that held steady
	/// are not listed.
	/// </summary>
	public required IReadOnlyList<DatasetFutureDeclineFeed> Feeds { get; init; }
}

/// <summary>One feed's contribution to a dataset's falling forward supply.</summary>
public sealed class DatasetFutureDeclineFeed
{
	public required string FeedId { get; init; }

	/// <summary>Short feed name — the last path segment of the feed URL.</summary>
	public required string FeedName { get; init; }

	/// <summary>Which rule this feed fired: <c>monotonic_decline</c>, <c>sharp_drop</c> or <c>both</c>.</summary>
	public required string Reason { get; init; }

	/// <summary>The feed's forward opportunities at the first observation in the window.</summary>
	public required long StartFuture { get; init; }

	/// <summary>Its forward opportunities at the last observation in the window.</summary>
	public required long CurrentFuture { get; init; }

	/// <summary><see cref="StartFuture"/> minus <see cref="CurrentFuture"/>.</summary>
	public required long Drop { get; init; }

	/// <summary><see cref="Drop"/> as a percentage of <see cref="StartFuture"/>, to two decimal places.</summary>
	public required double DropPercent { get; init; }

	/// <summary>The feed's forward opportunities at the start of the longer qualifying window.</summary>
	public required long QualifyStartFuture { get; init; }

	/// <summary>
	/// <see cref="CurrentFuture"/> measured back to <see cref="QualifyStartFuture"/>, as a percentage —
	/// the figure the qualifying gate tests. Floored at <c>0</c>, never negative: a feed holding more
	/// than it did at the start of the qualifying window reports <c>0</c> here and can only have been
	/// reported on the delta clause. <see cref="QualifyStartFuture"/> is an earlier observation than
	/// <see cref="StartFuture"/>, not necessarily a larger one, so this can sit below
	/// <see cref="DropPercent"/>.
	/// </summary>
	public required double QualifyDropPercent { get; init; }

	/// <summary>Days this feed's supply has been falling without interruption, as of the snapshot date.</summary>
	public required int ConsecutiveDecliningDays { get; init; }

	/// <summary>
	/// The steepest single step down inside the window, as a percentage of the figure it fell from — the
	/// figure the <c>sharp_drop</c> rule tests. <c>0</c> when nothing fell, which a feed reported for a
	/// monotonic decline cannot be.
	/// </summary>
	public required double LargestDailyDropPercent { get; init; }

	/// <summary>
	/// Items the feed reported as new or changed across the window. Not part of what <em>raises</em> a
	/// decline — the rules read supply alone — but, against <see cref="DeletesInWindow"/>, part of whether
	/// one is reported: see <see cref="DeltaInWindow"/>.
	/// </summary>
	public required long UpdatedInWindow { get; init; }

	/// <summary>
	/// Items the feed actually removed across the window. A drop matched by deletes is a publisher
	/// withdrawing opportunities; one without them is supply quietly expiring.
	/// </summary>
	public required long DeletesInWindow { get; init; }

	/// <summary>
	/// <see cref="UpdatedInWindow"/> minus <see cref="DeletesInWindow"/>. Negative means the feed took away
	/// more than it added over the detection window, which qualifies the decline for reporting on its own
	/// however shallow the curve looks over the longer window.
	/// </summary>
	public required long DeltaInWindow { get; init; }
}
