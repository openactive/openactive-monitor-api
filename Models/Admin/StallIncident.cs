namespace MonitorApi.Models.Admin;

/// <summary>An open single-feed stall, as reported to the admin dashboard.</summary>
public sealed class StallIncident
{
	/// <summary>Always <c>single_feed_stall</c>; identifies which monitor raised the incident.</summary>
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

	/// <summary>The day the feed went quiet — its last publishing day.</summary>
	public required DateOnly FirstDetected { get; init; }

	/// <summary>Days the incident has been open as of the snapshot date.</summary>
	public required int DaysOpen { get; init; }

	/// <summary>Consecutive silent days as of the snapshot date.</summary>
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
	/// The feed's daily <c>updated</c> counts over the trailing ten days, oldest first, ending on the
	/// snapshot date. Always ten entries, so entry <c>i</c> is the same day for every incident in the
	/// response. <c>null</c> means no ingestion run was recorded that day; <c>0</c> means the feed was
	/// polled and published nothing.
	/// </summary>
	public required IReadOnlyList<long?> Trend { get; init; }

	public required StallIncidentDetail Detail { get; init; }

	/// <summary>Feed quality score from <c>feed_quality</c>, or <c>null</c> when the feed has not been assessed.</summary>
	public required double? QualityScore { get; init; }
}

/// <summary>Monitor-specific evidence for a single-feed stall.</summary>
public sealed class StallIncidentDetail
{
	/// <summary>Last day on which the feed published an updated item.</summary>
	public required DateOnly LastModified { get; init; }
}
