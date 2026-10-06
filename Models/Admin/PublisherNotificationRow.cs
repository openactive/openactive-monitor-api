using System.Text.Json;

namespace MonitorApi.Models.Admin;

/// <summary> One publisher on the notification list. </summary>
public sealed class PublisherNotificationRow
{
    public required string Id { get; init; }
    public required string? PublisherId { get; init; }
    public required string? PublisherName { get; init; }
    public required long? ProblemCount { get; init; }
    public required IReadOnlyList<string> Monitors { get; init; }
    public required long? OldestDaysOpen { get; init; }
    public required string? Status { get; init; }
    public required string? Stakeholder { get; init; }
    public required string? Contact { get; init; }
    public required DateOnly? DateContacted { get; init; }
    public required string? Notes { get; init; }
    public required JsonElement? Problems { get; init; }
    public required DateTime? CreatedAt { get; init; }
    public required DateTime? UpdatedAt { get; init; }
}