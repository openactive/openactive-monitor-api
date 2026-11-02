using MonitorApi.Services.Admin;
using static MonitorApi.Admin.Tests.Actions.RouterFixtures;

namespace MonitorApi.Admin.Tests.Actions;

public class ActionRouterFutureDeclineTests
{
    private static readonly Dictionary<string, string> SlotFeed = Kinds(("f1", "Slot"));

    [Fact]
    public void Decline_UnderFifteenDays_IsNotAnAction()
    {
        var actions = ActionRouter.Route(new ActionRouterInputs
        {
            FutureDeclines = [Decline(14)],
            FeedKinds = SlotFeed,
        });

        Assert.Empty(actions);
    }

    [Fact]
    public void Decline_AtFifteenDays_IsADatasetLevelAction()
    {
        var actions = ActionRouter.Route(new ActionRouterInputs
        {
            FutureDeclines = [Decline(15)],
            FeedKinds = SlotFeed,
        });

        var action = Assert.Single(actions);
        Assert.Equal(MonitorActionKind.FutureDecline, action.Kind);
        Assert.Equal("d1", action.DatasetUrl);
        Assert.Null(action.FeedId);
        Assert.Equal(15, action.ConsecutiveDays);
        Assert.Equal(AsOf.AddDays(-15), action.FirstDetected);
        Assert.Equal("unavailable", action.SeasonalCheck);
    }

    [Fact]
    public void Decline_OnlyFromOtherFeedKinds_IsNotAnAction()
    {
        var actions = ActionRouter.Route(new ActionRouterInputs
        {
            FutureDeclines = [Decline(20)],
            FeedKinds = Kinds(("f1", "FacilityUse")),
        });

        Assert.Empty(actions);
    }

    [Fact]
    public void Decline_FromAFeedOfUnknownKind_IsNotAnAction()
    {
        var actions = ActionRouter.Route(new ActionRouterInputs { FutureDeclines = [Decline(20)] });

        Assert.Empty(actions);
    }

    [Fact]
    public void Decline_WithOneSessionFeedAmongOthers_IsAnAction()
    {
        var actions = ActionRouter.Route(new ActionRouterInputs
        {
            FutureDeclines = [Decline(20, "d1", "f1", "f2")],
            FeedKinds = Kinds(("f1", "FacilityUse"), ("f2", "ScheduledSession")),
        });

        Assert.Equal(MonitorActionKind.FutureDecline, Assert.Single(actions).Kind);
    }

    [Fact]
    public void Decline_IsHiddenByAnHttpFailure()
    {
        var actions = ActionRouter.Route(new ActionRouterInputs
        {
            HttpFailures = [HttpFailure("f9", 10)],
            FutureDeclines = [Decline(20)],
            FeedKinds = SlotFeed,
        });

        var action = Assert.Single(actions);
        Assert.Equal(MonitorActionKind.HttpEndpointFailure, action.Kind);
        Assert.Equal("superseded_by:http_endpoint_failure", Assert.Single(action.Suppressed).Reason);
    }

    [Fact]
    public void Decline_IsHiddenByAnAuthFailure()
    {
        var actions = ActionRouter.Route(new ActionRouterInputs
        {
            AuthFailures = [AuthFailure("f9", 10)],
            FutureDeclines = [Decline(20)],
            FeedKinds = SlotFeed,
        });

        Assert.Equal(MonitorActionKind.AuthenticationRequired, Assert.Single(actions).Kind);
    }

    [Fact]
    public void Decline_IsHiddenByADatasetStall()
    {
        // Unlike orphans, the tree lists dataset stalls for this row.
        var actions = ActionRouter.Route(new ActionRouterInputs
        {
            DatasetStalls = [WholeDatasetStall(5)],
            FutureDeclines = [Decline(20)],
            FeedKinds = SlotFeed,
        });

        var action = Assert.Single(actions);
        Assert.Equal(MonitorActionKind.DatasetStall, action.Kind);
        Assert.Equal("superseded_by:dataset_stall", Assert.Single(action.Suppressed).Reason);
    }

    [Fact]
    public void Decline_IsHiddenByASingleFeedStall()
    {
        var actions = ActionRouter.Route(new ActionRouterInputs
        {
            SingleFeedStalls = [FeedStall("f1", 10)],
            FutureDeclines = [Decline(20)],
            FeedKinds = SlotFeed,
        });

        Assert.Equal(MonitorActionKind.SingleFeedStall, Assert.Single(actions).Kind);
    }

    [Fact]
    public void Decline_IsHiddenByTheFirstMatchingCheckOnly()
    {
        var actions = ActionRouter.Route(new ActionRouterInputs
        {
            HttpFailures = [HttpFailure("f8", 10)],
            DatasetStalls = [WholeDatasetStall(5)],
            FutureDeclines = [Decline(20)],
            FeedKinds = SlotFeed,
        });

        Assert.Equal(2, actions.Count);
        Assert.Equal(MonitorActionKind.HttpEndpointFailure, actions[0].Kind);
        Assert.Single(actions[0].Suppressed);
        Assert.Empty(actions[1].Suppressed);
    }

    [Fact]
    public void Decline_IsNotHiddenByAProblemThatHasNotTriggered()
    {
        var actions = ActionRouter.Route(new ActionRouterInputs
        {
            DatasetStalls = [WholeDatasetStall(4)],
            FutureDeclines = [Decline(20)],
            FeedKinds = SlotFeed,
        });

        Assert.Equal(MonitorActionKind.FutureDecline, Assert.Single(actions).Kind);
    }

    [Fact]
    public void Decline_IsNotHiddenByAProblemInAnotherDataset()
    {
        var actions = ActionRouter.Route(new ActionRouterInputs
        {
            HttpFailures = [HttpFailure("f9", 10, "d2")],
            FutureDeclines = [Decline(20, "d1")],
            FeedKinds = SlotFeed,
        });

        Assert.Equal(2, actions.Count);
        Assert.Equal(MonitorActionKind.FutureDecline, actions[1].Kind);
    }
}