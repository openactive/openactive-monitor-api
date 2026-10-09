using System.Text.Json;

namespace MonitorApi.Models.Admin;

/// <summary>Writable fields for a publisher notification (create/update body).</summary>
public class PublisherNotificationBody
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

/// <summary>One publisher on the notification list (API response).</summary>
public sealed class PublisherNotificationRow : PublisherNotificationBody
{
	public DateTime? CreatedAt { get; init; }
	public DateTime? UpdatedAt { get; init; }
}
