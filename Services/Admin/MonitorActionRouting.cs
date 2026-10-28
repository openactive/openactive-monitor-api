namespace MonitorApi.Services.Admin;

/// <summary>
/// The rows of the Monitor Actions Decision Tree that the router can produce. Plain strings rather than
/// an enum so they serialise as-is and match the monitor ids used elsewhere on the admin surface.
/// </summary>
public static class MonitorActionKind
{
    /// <summary>Feed returns HTTP failure codes (404, 500, …) from the ingestion error monitor.</summary>
    public const string HttpEndpointFailure = "http_endpoint_failure";

    /// <summary> Feed returns 401 or 403. </summary>
    public const string AuthenticationRequired = "authentication_required";

    /// <summary> Every feed in a dataset has stopped publishing. </summary>
    public const string DatasetStall = "dataset_stall";

    /// <summary> One feed has stopped publishing while its neighbours carry on. </summary>
    public const string SingleFeedStall = "single_feed_stall";

    /// <summary> Children whose parent is missing from the same dataset. </summary>
    public const string OrphanedChildren = "orphaned_children";

    /// <summary> Forward supply of Slots / ScheduledSessions is draining. </summary>
    public const string FutureDecline = "future_decline";

    /// <summary> Parent and child feeds use different patterns. </summary>
    public const string IdPatternMismatch = "id_pattern_mismatch";
}

/// <summary>
/// Why an incident was not turned into its own action. Each value names the higher-priority problem
/// that was reported instead, so a steward can see the decision the tree made.
/// </summary>
public static class SuppressionReasons
{
    /// <summary>Builds the reason for an incident hidden behind a higher-priority action.</summary>
    public static string SupersededBy(string actionKind) => $"superseded_by:{actionKind}";
}

/// <summary>
/// The decision tree's trigger thresholds. Defaults are the values in the tree; tests and callers can
/// override any of them with <c>with</c>.
/// </summary>
public sealed record ActionTriggers
{
    /// <summary>Continuous days of HTTP failure before acting. Decision tree value: 10.</summary>
    public int HttpFailureDays { get; init; } = 10;
    
    /// <summary>Continuous days of 401/403 failure before acting. Decision tree value: 10.</summary>
    public int AuthRequiredDays { get; init; } = 10;
    
    /// <summary>Continuous days a whole dataset is silent before acting. Decision tree value: 5.</summary>
    public int DatasetStallDays { get; init; } = 5;
    
    /// <summary>Continuous days a single feed is silent before acting. Decision tree value: 10.</summary>
    public int SingleFeedStallDays { get; init; } = 10;
    
    /// <summary>Share of a dataset's children that must be orphaned before acting, 0..1. Decision tree value: 20%.</summary>
    public double OrphanShare { get; init; } = 0.20;
    
    /// <summary>Continuous days of future-supply decline before acting. Decision tree value: 15.</summary>
    public int FutureDeclineDays { get; init; } = 15;

     /// <summary>
    /// Feed kinds the single-feed stall and future-decline rows apply to. Tree: Slot and
    /// ScheduledSession. Compared case-insensitively.
    /// </summary>
    public IReadOnlySet<string> SessionKinds { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Slot", "ScheduledSession" };
}

/// <summary>An incident the router chose not to report, and why.</summary>
/// <param name="Kind">The <see cref="MonitorActionKind"/> the incident would have been.</param>
/// <param name="FeedId">The feed, for feed-level incidents; <c>null</c> for dataset-level ones.</param>
/// <param name="Reason">See <see cref="SuppressionReasons"/>.</param>
public sealed record SuppressedIncident(string Kind, string? FeedId, string Reason);

/// <summary>
/// One thing a steward should act on: a decision-tree incident row that triggered and survived the pre-checks.
/// </summary>
/// <remarks>
/// Pure: carries the detector's own incident in <see cref="Incident"/> so the controller can hydrate
/// publisher and feed metadata without the router knowing about BigQuery.
/// </remarks>
public sealed record MonitorAction
{
    /// <summary>See <see cref="MonitorActionKind"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>The dataset, matching <c>feeds.dataset_url</c>.</summary>
    public required string DatasetUrl { get; init; }
    
    /// <summary>The feed, for feed-level actions; <c>null</c> for dataset-level ones.</summary>
    public string? FeedId { get; init; }
    
    /// <summary>The day the underlying problem started, where the monitor knows it.</summary>
    public DateOnly? FirstDetected { get; init; }
    
    /// <summary>Continuous days the problem has lasted, for day-based triggers</summary>
    public int? ConsecutiveDays { get; init; }
    
    /// <summary>Affected share 0..1, for share-based triggers (orphans).</summary>
    public double? Share { get; init; }
    
    /// <summary>
    /// Outcome of the seasonal pre-check for the future-decline actions: <c>"unavailable"</c> until there is
    /// enough history to judge. <c>null</c> for every other kinds.
    /// </summary>
    public string? SeasonalCheck { get; init; }

    /// <summary>Incidents hidden behind this action by the pre-checks. </summary>
    public IReadOnlyList<SuppressedIncident> Suppressed { get; init; } = [];

    /// <summary>
    /// The detector's own incident (<see cref="SingleFeedStall"/>, <see cref="DatasetStall"/>, …), kept
    /// for evidence and hydration.
    /// </summary>
    public required object Incident { get; init; }
}
