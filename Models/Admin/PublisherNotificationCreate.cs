using System.Text.Json;

namespace MonitorApi.Models.Admin;

/// <summary> Body for POST /admin/publisher-notifications </summary>
public sealed class PublisherNotificationCreate
{
    public required string Id { get; init; }
    public string? PublisherId { get; init; }
    public string? PublisherName { get; init; }
    public long? ProblemCount { get; init; }
    public IReadOnlyList<string> Monitors { get; init; } = [];
    public long? OldestDaysOpen { get; init; }
    public string? Status { get; init; }
    public string? Stakeholder { get; init; }
    public string? Contact { get; init; }
    public DateOnly? DateContacted { get; init; }
    public string? Notes { get; init; }
    public JsonElement? Problems { get; init; }

}