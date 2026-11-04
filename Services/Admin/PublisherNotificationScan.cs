using System.Text.Json;
using MonitorApi.Models.Admin;

namespace MonitorApi.Services.Admin;

/// <summary>One publisher's problems, ready to be saved to the publisher notification list.</summary>
public sealed record ScannedPublisher(
    string Id,
    string PublisherId,
    string PublisherName,
    int ProblemCount,
    IReadOnlyList<string> Monitors,
    int? OldestDaysOpen,
    string ProblemsJson);

/// <param name="SkippedActions">Actions left out because we don't know the publisher's name.</param>
public sealed record PublisherScanResult(IReadOnlyList<ScannedPublisher> Publishers, int SkippedActions);

/// <summary>Groups monitor actions into one notification row per publisher.</summary>
public static class PublisherNotificationScan
{
    private static readonly JsonSerializerOptions Json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static PublisherScanResult Group(IEnumerable<MonitorActionRow> actions)
    {
        var all = actions.ToList();

        // We can't email a publisher we cannot name, so these are counted and left out.
        var named = all.Where(a => !string.IsNullOrWhiteSpace(a.PublisherName)).ToList();

        var publishers = named
            .GroupBy(a => a.PublisherId, StringComparer.Ordinal)
            .Select(g => ToPublisher(g.Key, g.ToList()))
            .OrderByDescending(p => p.OldestDaysOpen ?? -1)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToList();
        
        return new PublisherScanResult(publishers, all.Count - named.Count);  
    }

    private static ScannedPublisher ToPublisher(string publisherId, List<MonitorActionRow> rows)
    {
        var problems = rows.Select(r => new
        {
            MonitorId = r.Kind,
            r.DatasetUrl,
            r.DatasetName,
            r.FeedId,
            r.FeedName,
            r.FeedType,
            r.FeedUrl,
            r.FirstDetected,
            DaysOpen = r.ConsecutiveDays,
            r.Share,
            r.SeasonalCheck,
            Detail = r.Evidence,
            r.Suppressed,
        });

        return new ScannedPublisher(
            Id: "pn_" + publisherId["pub_".Length..],
            PublisherId: publisherId,
            PublisherName: rows[0].PublisherName,
            ProblemCount: rows.Count,
            Monitors: rows.Select(r => r.Kind).Distinct().OrderBy(ActionRouter.Priority).ToList(),
            OldestDaysOpen: rows.Max(r => r.ConsecutiveDays),
            ProblemsJson: JsonSerializer.Serialize(problems, Json));
    }
}