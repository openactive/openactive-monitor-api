namespace MonitorApi.Models.Admin;

// <summary>Counts for the whole publisher notification list</summary>
public sealed class PublisherNotificationSummary
{
    public required int Publishers { get; init; }
    public required int Open { get; init; }
    public required int InProgress { get; init; }
    public required int Closed { get; init; }
    public required int NeverContacted { get; init; }
    public required int Alerts { get; init; }
}