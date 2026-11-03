using MonitorApi.Models.Admin;
using MonitorApi.Services.Admin;
using static MonitorApi.Admin.Tests.Actions.RouterFixtures;

namespace MonitorApi.Admin.Tests.Actions;

public class MonitorActionRowsTests
{
    private const string DatasetUrl = "https://example.org/openactive";

    [Fact]
    public void FeedAction_TakesNamesFromTheFeedDetails()
    {
        var action = new MonitorAction
        {
            Kind = MonitorActionKind.SingleFeedStall,
            DatasetUrl = DatasetUrl,
            FeedId = "f1",
            FirstDetected = AsOf.AddDays(-9),
            ConsecutiveDays = 10,
            Incident = FeedStall("f1", 10, DatasetUrl),
        };
        var dataset = new DatasetMetadata(DatasetUrl, "Example dataset", "Someone Else");
        var feed = new FeedMetadata("f1", "https://example.org/feeds/scheduled-sessions", "ScheduledSession", "Example Leisure", 80);

        var row = MonitorActionRows.From(action, dataset, feed);

        Assert.Equal("Example Leisure", row.PublisherName);
        Assert.Equal("pub_" + AdminSlug.Of("Example Leisure"), row.PublisherId);
        Assert.Equal("Example dataset", row.DatasetName);
        Assert.Equal("scheduled-sessions", row.FeedName);
        Assert.Equal("ScheduledSession", row.FeedType);
        Assert.Equal(10, row.ConsecutiveDays);
        Assert.Equal(AsOf.AddDays(-10), row.Evidence["last_published"]);
    }

    [Fact]
    public void DatasetAction_WithNoDetails_StillHasANameAndPublisherId()
    {
        var action = new MonitorAction
        {
            Kind = MonitorActionKind.FutureDecline,
            DatasetUrl = DatasetUrl,
            FirstDetected = AsOf.AddDays(-20),
            ConsecutiveDays = 20,
            SeasonalCheck = "unavailable",
            Incident = Decline(20, DatasetUrl),
        };

        var row = MonitorActionRows.From(action, dataset: null, feed: null);

        Assert.Equal("example.org", row.DatasetName);
        Assert.Equal("", row.PublisherName);
        Assert.Equal("pub_" + AdminSlug.Of(null), row.PublisherId);
        Assert.Null(row.FeedId);
        Assert.Null(row.FeedName);
        Assert.Equal("unavailable", row.SeasonalCheck);
        Assert.Equal(600L, row.Evidence["drop"]);
    }

    [Fact]
    public void HttpFailure_EvidenceHasTheError()
    {
        var action = new MonitorAction
        {
            Kind = MonitorActionKind.HttpEndpointFailure,
            DatasetUrl = "d1",
            FeedId = "f1",
            Incident = HttpFailure("f1", 10),
        };

        var row = MonitorActionRows.From(action, null, null);

        Assert.Equal("500", row.Evidence["error_code"]);
        Assert.Equal("HTTP 500", row.Evidence["error_message"]);
        Assert.Equal(AsOf.AddDays(-10), row.Evidence["last_completed"]);
    }

    [Fact]
    public void Orphans_EvidenceHasExampleMissingParents()
    {
        var action = new MonitorAction
        {
            Kind = MonitorActionKind.OrphanedChildren,
            DatasetUrl = "d1",
            Share = 0.25,
            Incident = OrphanIncident(missingIds: ["p1", "p2"]),
        };

        var row = MonitorActionRows.From(action, null, null);

        Assert.Equal(250L, row.Evidence["orphan_count"]);
        Assert.Equal(["p1", "p2"], (List<string>)row.Evidence["example_missing_parents"]!);
        Assert.Equal(0.25, row.Share);
    }

    [Fact]
    public void IdPatternAction_EvidenceHasThePatterns()
    {
        var actions = ActionRouter.Route(new ActionRouterInputs
        {
            Orphans = [OrphanIncident(kind: "Slot", missingIds: ["session-1"])],
            ParentIdSamples = [ParentSample("FacilityUse", ["a1b2c3d4-0000-0000-0000-000000000001"])],
        });

        var row = MonitorActionRows.From(Assert.Single(actions), null, null);

        var byKind = Assert.Single((List<Dictionary<string, object?>>)row.Evidence["by_kind"]!);
        Assert.Equal("Slot", byKind["kind"]);
        Assert.Equal(["session-{n}"], (IReadOnlyList<string>)byKind["referenced_patterns"]!);
        Assert.Single(row.Suppressed);
    }
}
