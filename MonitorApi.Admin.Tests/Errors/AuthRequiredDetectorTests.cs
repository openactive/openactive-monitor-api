using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.Errors;

/// <summary>
/// Deterministic tests for the authentication-required rules, run against hand-written histories.
/// No BigQuery credentials needed.
/// </summary>
public class AuthRequiredDetectorTests
{
    private static readonly DateOnly AsOf = new(2026, 9, 9);

    private const string Complete = FeedIngestionDay.Complete;
    private const string Error = FeedIngestionDay.Error;

    /// <summary>
    /// A feed with one ingestion day per entry, given as <c>(daysAgo, status, code)</c> back from
    /// <see cref="AsOf"/>. Days not listed had no run.
    /// </summary>
    private static FeedStatusHistory Feed(string feedId, params (int DaysAgo, string Status, string? Code)[] days) =>
        new(
            feedId,
            "d1",
            days
                .Select(d => new FeedIngestionDay(AsOf.AddDays(-d.DaysAgo), d.Status, d.Code, 
                    d.Code is null ? null : $"HTTP {d.Code}"))
                .OrderBy(d => d.Day)
                .ToList()
        );

    [Fact]
    public void UnbrokenAuthFailures_CountEveryDay()
    {
        var feeds = new[] { Feed("f1", (0, Error, "403"), (1, Error, "403"), (2, Error, "401"), (3, Complete, null)) };

        var incident = Assert.Single(AuthRequiredDetector.Detect(feeds, AsOf));

        Assert.Equal("f1", incident.FeedId);
        Assert.Equal("d1", incident.DatasetId);
        Assert.Equal(AsOf.AddDays(-2), incident.FirstFailed);
        Assert.Equal(3, incident.ConsecutiveDays);
        Assert.Equal("403", incident.ErrorCode);
        Assert.Equal("HTTP 403", incident.ErrorMessage);
    }

    [Fact]
    public void CompletedToday_IsNotAnIncident()
    {
        var feeds = new[] { Feed("f1", (0, Complete, null), (1, Error, "403")) };

        Assert.Empty(AuthRequiredDetector.Detect(feeds, AsOf));
    }

    [Fact]
    public void NoRunToday_IsNotAnIncident()
    {
        var feeds = new[] { Feed("f1", (1, Error, "403"), (2, Error, "403")) };

        Assert.Empty(AuthRequiredDetector.Detect(feeds, AsOf));
    }

    [Fact]
    public void NonAuthErrorToday_IsNotAnIncident()
    {
        var feeds = new[] { Feed("f1", (0, Error, "500"), (1, Error, "403")) };

        Assert.Empty(AuthRequiredDetector.Detect(feeds, AsOf));
    }

    [Fact]
    public void CompletedDay_BreaksTheRun()
    {
        var feeds = new[] { Feed("f1", (0, Error, "403"), (1, Error, "403"), (2, Complete, null), (3, Error, "403")) };

        var incident = Assert.Single(AuthRequiredDetector.Detect(feeds, AsOf));

        Assert.Equal(2, incident.ConsecutiveDays);
        Assert.Equal(AsOf.AddDays(-1), incident.FirstFailed);
    }

    [Fact]
    public void NonAuthError_BreaksTheRun()
    {
        var feeds = new[] { Feed("f1", (0, Error, "401"), (1, Error, "500"), (2, Error, "401")) };

        var incident = Assert.Single(AuthRequiredDetector.Detect(feeds, AsOf));

        Assert.Equal(1, incident.ConsecutiveDays);
    }

    [Fact]
    public void FailureWithNoCode_BreaksTheRun()
    {
        // A failure recorded before error_code existed: cause unknown, so it cannot extend an auth run.
        var feeds = new[] { Feed("f1", (0, Error, "403"), (1, Error, null), (2, Error, "403")) };

        var incident = Assert.Single(AuthRequiredDetector.Detect(feeds, AsOf));

        Assert.Equal(1, incident.ConsecutiveDays);
    }

    [Fact]
    public void DaysWithNoRun_DoNotBreakTheRun()
    {
        // Days 1 and 2 had no ingestion run at all.
        var feeds = new[] { Feed("f1", (0, Error, "403"), (3, Error, "403"), (4, Complete, null)) };

        var incident = Assert.Single(AuthRequiredDetector.Detect(feeds, AsOf));

        Assert.Equal(AsOf.AddDays(-3), incident.FirstFailed);
        Assert.Equal(4, incident.ConsecutiveDays);
    }

    [Fact]
    public void DaysAfterAsOf_AreIgnored()
    {
        // A completed run after the evaluated day must not hide the run that was open on it.
        var feeds = new[] { Feed("f1", (-1, Complete, null), (0, Error, "403"), (1, Error, "403")) };

        var incident = Assert.Single(AuthRequiredDetector.Detect(feeds, AsOf));

        Assert.Equal(2, incident.ConsecutiveDays);
    }

    [Fact]
    public void Incidents_AreOrderedLongestFirstThenByFeedId()
    {
        var feeds = new[]
        {
            Feed("b", (0, Error, "403")),
            Feed("a", (0, Error, "403")),
            Feed("c", (0, Error, "403"), (1, Error, "403")),
        };

        var incidents = AuthRequiredDetector.Detect(feeds, AsOf);

        Assert.Equal(["c", "a", "b"], incidents.Select(i => i.FeedId));
    }
}
