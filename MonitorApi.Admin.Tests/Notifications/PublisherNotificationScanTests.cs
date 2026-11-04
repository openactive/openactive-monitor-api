using System.Text.Json;
using MonitorApi.Models.Admin;
using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.Notifications;

public class PublisherNotificationScanTests
{
    private static MonitorActionRow Row(string kind, string publisher, int? days) => new()
    {
        Kind = kind,
        DatasetUrl = "https://example.org/" + AdminSlug.Of(publisher),
        DatasetName = "example.org",
        PublisherId = "pub_" + AdminSlug.Of(publisher),
        PublisherName = publisher,
        ConsecutiveDays = days,
        Evidence = new Dictionary<string, object?> { ["last_published"] = new DateOnly(2026, 9, 1) },
        Suppressed = [],
    };

    [Fact]
    public void SamePublisher_BecomesOneRow()
    {
        var result = PublisherNotificationScan.Group(
        [
            Row(MonitorActionKind.SingleFeedStall, "Example Leisure", 12),
            Row(MonitorActionKind.HttpEndpointFailure, "Example Leisure", 10),
        ]);

        var publisher = Assert.Single(result.Publishers);
        Assert.Equal("pn_" + AdminSlug.Of("Example Leisure"), publisher.Id);
        Assert.Equal("Example Leisure", publisher.PublisherName);
        Assert.Equal(2, publisher.ProblemCount);
        Assert.Equal(12, publisher.OldestDaysOpen);
        Assert.Equal(
            [MonitorActionKind.HttpEndpointFailure, MonitorActionKind.SingleFeedStall],
            publisher.Monitors);
    }

    [Fact]
    public void DifferentPublishers_AreSeparateRows_OldestFirst()
    {
        var result = PublisherNotificationScan.Group(
        [
            Row(MonitorActionKind.DatasetStall, "Newer Problems", 6),
            Row(MonitorActionKind.DatasetStall, "Older Problems", 30),
        ]);

        Assert.Equal(["Older Problems", "Newer Problems"], result.Publishers.Select(p => p.PublisherName));
    }

    [Fact]
    public void UnnamedPublisher_IsLeftOutAndCounted()
    {
        var result = PublisherNotificationScan.Group(
        [
            Row(MonitorActionKind.DatasetStall, "", 6),
            Row(MonitorActionKind.DatasetStall, "Example Leisure", 6),
        ]);

        Assert.Single(result.Publishers);
        Assert.Equal(1, result.SkippedActions);
    }

    [Fact]
    public void OrphansOnly_HaveNoOldestDays_AndSortLast()
    {
        var result = PublisherNotificationScan.Group(
        [
            Row(MonitorActionKind.OrphanedChildren, "Orphans Only", null),
            Row(MonitorActionKind.DatasetStall, "Has A Stall", 5),
        ]);

        Assert.Equal("Has A Stall", result.Publishers[0].PublisherName);
        Assert.Null(result.Publishers[1].OldestDaysOpen);
    }

    [Fact]
    public void RepeatedMonitor_IsListedOnce()
    {
        var result = PublisherNotificationScan.Group(
        [
            Row(MonitorActionKind.SingleFeedStall, "Example Leisure", 12),
            Row(MonitorActionKind.SingleFeedStall, "Example Leisure", 11),
        ]);

        var publisher = Assert.Single(result.Publishers);
        Assert.Equal(2, publisher.ProblemCount);
        Assert.Equal([MonitorActionKind.SingleFeedStall], publisher.Monitors);
    }

    [Fact]
    public void ProblemsJson_KeepsEveryProblemAndItsEvidence()
    {
        var result = PublisherNotificationScan.Group(
        [
            Row(MonitorActionKind.SingleFeedStall, "Example Leisure", 12),
            Row(MonitorActionKind.DatasetStall, "Example Leisure", 6),
        ]);

        using var json = JsonDocument.Parse(Assert.Single(result.Publishers).ProblemsJson);
        var problems = json.RootElement;

        Assert.Equal(2, problems.GetArrayLength());
        Assert.Equal("single_feed_stall", problems[0].GetProperty("monitor_id").GetString());
        Assert.Equal(12, problems[0].GetProperty("days_open").GetInt32());
        Assert.Equal("2026-09-01", problems[0].GetProperty("detail").GetProperty("last_published").GetString());
    }
}