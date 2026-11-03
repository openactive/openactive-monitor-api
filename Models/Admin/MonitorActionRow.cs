using MonitorApi.Services.Admin;

namespace MonitorApi.Models.Admin;

/// <summary>One thing a steward should act on, as returned by <c>/admin/actions</c>.</summary>
public sealed class MonitorActionRow
{
    /// <summary>Which decision-tree row this is, e.g <c>single_feed_stall</c>.</summary>
    public required string Kind { get; init; }

    public required string DatasetUrl { get; init; }

    public required string DatasetName { get; init; }

    public required string PublisherId { get; init; }

    public required string PublisherName { get; init; }

    /// <summary>Set for feed-level actions, <c>null</c> for dataset-level ones.</summary>
    public string? FeedId { get; init; }
    
    public string? FeedName { get; init; }

    public string? FeedType { get; init; }

    public string? FeedUrl { get; init; }

    /// <summary>The day the problem started. <c>null</c> for orphans, which have no history.</summary>
    public DateOnly? FirstDetected { get; init; }

    public int? ConsecutiveDays { get; init; }

    /// <summary>Share of the dataset affected, 0 to 1. Orphan and ID-mismatch actions only.</summary>
    public double? Share { get; init; }

    /// <summary><c>unavailable</c> on future-decline actions until there's enough history; otherwise <c>null</c>.</summary>
    public string? SeasonalCheck { get; init; }

    /// <summary>The figures for the email. The keys depend on <see cref="Kind"/>.</summary>
    public required IReadOnlyDictionary<string, object?> Evidence { get; init; }

    /// <summary>Problems hidden behind this one because this one explains them.</summary>
    public required IReadOnlyList<SuppressedIncident> Suppressed { get; init; }
}