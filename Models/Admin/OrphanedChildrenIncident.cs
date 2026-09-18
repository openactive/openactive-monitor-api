namespace MonitorApi.Models.Admin;

/// <summary>
/// A dataset publishing orphaned children, as reported to the admin dashboard.
/// </summary>
/// <remarks>
/// Shares only the identifier and workflow fields with <see cref="StallIncident"/> and
/// <see cref="IngestionErrorIncident"/> — <c>monitor_id</c>, <c>publisher_id</c>,
/// <c>publisher_name</c>, <c>past_threshold</c>, <c>status</c>, <c>last_contacted</c>. Those are the
/// spine the dashboard can rely on across every monitor.
///
/// Deliberately absent, rather than present and always null:
///
/// <list type="bullet">
/// <item>
/// <c>feed_id</c>, <c>feed_name</c>, <c>feed_type</c> and <c>feed_url</c>, because the entity here is a
/// dataset. A child's parent may be published by a different feed of the same dataset, so the check
/// only means anything at dataset scope — and a publisher fixes it once.
/// </item>
/// <item>
/// <c>first_detected</c>, <c>days_open</c>, <c>consecutive_days</c> and <c>trend</c>, because
/// <c>opportunities</c> is a current-state mirror with no per-day snapshots. These would be
/// <em>added</em> if a snapshot source appeared; carrying them as nullable fields now would mean
/// narrowing them later, which is a breaking change for any consumer that had handled the null.
/// </item>
/// <item>
/// <c>quality_score</c>, because <c>feed_quality.score</c> is per-feed and there is no dataset-level
/// equivalent.
/// </item>
/// </list>
/// </remarks>
public sealed class OrphanedChildrenIncident
{
	/// <summary>Always <c>dataset_orphaned_children</c>; identifies which monitor raised the incident.</summary>
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
	/// Every child of the monitored kinds the dataset publishes, whatever its age — the denominator of
	/// <see cref="OrphanShare"/>. Nothing is filtered by date: the whole table is counted.
	/// </summary>
	public required long ChildCount { get; init; }

	/// <summary>
	/// Children actually examined: those naming their parent with a scalar reference <em>and</em>
	/// publishing no location of their own (<c>null</c> or <c>{}</c>). Always between
	/// <see cref="OrphanCount"/> and <see cref="ChildCount"/>. The shortfall against
	/// <see cref="ChildCount"/> is children that inline their <c>superEvent</c> as an object and so
	/// carry their parent with them, plus children that carry a location and so remain placeable
	/// whether or not their parent resolves.
	/// </summary>
	public required long CheckedCount { get; init; }

	/// <summary>
	/// Examined children whose referenced parent is missing from the dataset: a dangling reference with
	/// no location on the child to fall back on.
	/// </summary>
	public required long OrphanCount { get; init; }

	/// <summary>
	/// <see cref="OrphanCount"/> over <see cref="ChildCount"/>, between <c>0</c> and <c>1</c>.
	/// </summary>
	/// <remarks>
	/// The numerator counts only the children that could be checked — those naming a parent and carrying
	/// no location of their own — while the denominator counts every child of those kinds. Divide
	/// <see cref="OrphanCount"/> by <see cref="CheckedCount"/> instead for the ratio over exactly what
	/// was examined; the two differ for a dataset whose children mostly inline their parent, which is
	/// common for <c>ScheduledSession</c>, or mostly publish their own location.
	/// </remarks>
	public required double OrphanShare { get; init; }

	/// <summary>
	/// Distinct parent ids that could not be found — <b>the actionable figure</b>. One absent
	/// <c>SessionSeries</c> or <c>FacilityUse</c> can orphan thousands of children, so an
	/// <see cref="OrphanCount"/> of 432,830 against a <see cref="MissingParentCount"/> of 59 is
	/// fifty-nine things to fix, not four hundred thousand.
	/// </summary>
	public required long MissingParentCount { get; init; }

	/// <summary>Whether the incident has passed the escalation threshold.</summary>
	public required bool PastThreshold { get; init; }

	/// <summary>
	/// Workflow state. Currently always <c>open</c>: outreach states such as <c>awaiting_reply</c>
	/// require an incident-tracking store, which does not exist yet.
	/// </summary>
	public required string Status { get; init; }

	/// <summary>Always <c>null</c> until contact tracking exists. See <see cref="Status"/>.</summary>
	public required DateOnly? LastContacted { get; init; }

	public required OrphanedChildrenIncidentDetail Detail { get; init; }
}

/// <summary>Monitor-specific evidence for a dataset's orphaned children.</summary>
public sealed class OrphanedChildrenIncidentDetail
{
	/// <summary>
	/// The same counts split by child kind, worst kind first, so the dashboard can distinguish
	/// "Slots fine, ScheduledSessions broken" from a dataset that is broken throughout. The entries
	/// sum to the incident's own counts.
	/// </summary>
	public required IReadOnlyList<OrphanedChildrenKindCounts> ByKind { get; init; }

	/// <summary>
	/// The dataset's worst missing parents, most children first, capped at five. Merged across kinds,
	/// so these are the dataset's worst rather than any one kind's. Paste one into the publisher's feed
	/// to show them what is missing.
	/// </summary>
	public required IReadOnlyList<OrphanedChildrenMissingParent> MissingParents { get; init; }
}

/// <summary>One child kind's contribution to a dataset's orphaned children.</summary>
/// <remarks>
/// A <c>Slot</c> references its <c>FacilityUse</c>; a <c>ScheduledSession</c> references its
/// <c>SessionSeries</c>. A child that inlines its <c>superEvent</c> as a JSON object rather than
/// naming it cannot dangle, and a child with a location of its own stays placeable without its parent;
/// both count in <see cref="ChildCount"/> but never in <see cref="CheckedCount"/>.
/// </remarks>
public sealed class OrphanedChildrenKindCounts
{
	/// <summary>The child kind: <c>Slot</c> or <c>ScheduledSession</c>.</summary>
	public required string Kind { get; init; }

	public required long ChildCount { get; init; }

	public required long CheckedCount { get; init; }

	public required long OrphanCount { get; init; }

	public required long MissingParentCount { get; init; }
}

/// <summary>A parent the dataset references but does not publish.</summary>
public sealed class OrphanedChildrenMissingParent
{
	/// <summary>The <c>data_id</c> named by <c>has_superEvent</c> that is not in the dataset.</summary>
	public required string MissingId { get; init; }

	/// <summary>
	/// Orphaned children pointing at it — how much repairing this one parent would fix. Children that
	/// name it but publish their own location are not counted: they are not orphaned.
	/// </summary>
	public required long ChildCount { get; init; }
}
