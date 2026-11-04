namespace MonitorApi.Models.Admin;

/// <summary>Summary counts for the publisher notification list.</summary>
public sealed class PublisherScanSummary
{
    /// <summary>Publishers created or updated on the list.</summary>
    public required int PublishersSaved { get; init; }

    /// <summary>Problems found across all publishers.</summary>
    public required int ActionsFound { get; init; }

    /// <summary>Problems left out because we don't know the publisher's name.</summary>
    public required int ActionsSkipped { get; init; }
}