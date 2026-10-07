namespace MonitorApi.Models.Admin;

/// <summary> Body for PUT /admin/publisher-notifications/{id} </summary>
public sealed class PublisherNotificationUpdate
{
    public string? Status { get; init; }
    public string? Stakeholder { get; init; }
    public string? Contact { get; init; }
    public DateOnly? DateContacted { get; init; }
    public string? Notes { get; init; }
}