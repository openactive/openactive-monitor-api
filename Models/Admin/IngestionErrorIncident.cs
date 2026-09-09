namespace MonitorApi.Models.Admin;

/// <summary>An open feed ingestion error, as reported to the admin dashboard.</summary>
/// <remarks>
/// Carries the same fields as <see cref="StallIncident"/> so the dashboard can render any monitor's
/// incidents with one component; only <c>trend</c> and <c>detail</c> are monitor-specific.
/// </remarks>
public sealed class IngestionErrorIncident
{
	/// <summary>Always <c>feed_ingestion_error</c>; identifies which monitor raised the incident.</summary>
	public required string MonitorId { get; init; }

	/// <summary>Slug derived from the publisher name, e.g. <c>pub_freedom-leisure</c>.</summary>
	public required string PublisherId { get; init; }

	public required string PublisherName { get; init; }

	public required string FeedId { get; init; }

	/// <summary>Short feed name — the last path segment of the feed URL.</summary>
	public required string FeedName { get; init; }

	/// <summary>OpenActive feed kind, e.g. <c>ScheduledSession</c>, <c>Slot</c>, <c>FacilityUse</c>.</summary>
	public required string? FeedType { get; init; }

	public required string? FeedUrl { get; init; }

	/// <summary>The first day the feed failed — the day after its last completed ingestion.</summary>
	public required DateOnly FirstDetected { get; init; }

	/// <summary>Days the incident has been open as of the snapshot date.</summary>
	public required int DaysOpen { get; init; }

	/// <summary>Days since the feed last completed an ingestion, as of the snapshot date.</summary>
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
	/// One flag per day over the trailing ten days, oldest first, ending on the snapshot date:
	/// <c>1</c> on a day the feed's ingestion failed, <c>0</c> on any other day — whether it completed,
	/// warned, or was never polled. Always ten entries, so entry <c>i</c> is the same day for every
	/// incident in the response.
	/// </summary>
	public required IReadOnlyList<int> Trend { get; init; }

	public required IngestionErrorIncidentDetail Detail { get; init; }

	/// <summary>Feed quality score from <c>feed_quality</c>, or <c>null</c> when the feed has not been assessed.</summary>
	public required double? QualityScore { get; init; }
}

/// <summary>Monitor-specific evidence for a feed ingestion error.</summary>
public sealed class IngestionErrorIncidentDetail
{
	/// <summary>
	/// The failure's <c>error_code</c> on the snapshot date — an HTTP status (<c>500</c>, <c>404</c>) or a
	/// pipeline code (<c>BATCH_FAILED</c>, <c>CONNECTION_ERROR</c>, <c>MISSING_ITEMS</c>). <c>null</c> for
	/// a failure recorded before that column existed.
	/// </summary>
	public required string? ErrorCode { get; init; }

	/// <summary>
	/// The failure message on the snapshot date, e.g.
	/// <c>HTTP 500 fetching https://example.org/api/sessions</c>. Same nullability as
	/// <see cref="ErrorCode"/>.
	/// </summary>
	public required string? ErrorMessage { get; init; }

	/// <summary>Last day the feed completed an ingestion.</summary>
	public required DateOnly LastCompleted { get; init; }
}

/// <summary>Open feed ingestion error counts on one day.</summary>
public sealed class IngestionErrorTrendPoint
{
	public required DateOnly Date { get; init; }

	/// <summary>Ingestion errors open on this day.</summary>
	public required int OpenCount { get; init; }

	/// <summary>Subset of <see cref="OpenCount"/> that had passed the escalation threshold.</summary>
	public required int PastThresholdCount { get; init; }
}
