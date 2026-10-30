using MonitorApi.Services.Admin;
using static MonitorApi.Admin.Tests.Actions.RouterFixtures;

namespace MonitorApi.Admin.Tests.Actions;

/// <summary>
/// Deterministic tests for the decision tree's orphaned-children row and its pre-checks. No BigQuery
/// credentials needed.
/// </summary>
public class ActionRouterOrphanTests
{
	#region Trigger

	[Fact]
	public void Orphans_BelowShare_AreNotAnAction()
	{
		var actions = ActionRouter.Route(new ActionRouterInputs { Orphans = [OrphanIncident(share: 0.19)] });

		Assert.Empty(actions);
	}

	[Fact]
	public void Orphans_AtShare_AreADatasetLevelAction()
	{
		var actions = ActionRouter.Route(new ActionRouterInputs { Orphans = [OrphanIncident(share: 0.20)] });

		var action = Assert.Single(actions);
		Assert.Equal(MonitorActionKind.OrphanedChildren, action.Kind);
		Assert.Equal("d1", action.DatasetUrl);
		Assert.Null(action.FeedId);
		Assert.Equal(0.20, action.Share);
		// opportunities holds current state only, so there is no day the problem started.
		Assert.Null(action.FirstDetected);
		Assert.Empty(action.Suppressed);
	}

	#endregion

	#region Pre-checks

	[Fact]
	public void Orphans_AreSuppressedByAnHttpFailureInTheSameDataset()
	{
		var actions = ActionRouter.Route(new ActionRouterInputs
		{
			HttpFailures = [HttpFailure("f1", 10)],
			Orphans = [OrphanIncident()],
		});

		var action = Assert.Single(actions);
		Assert.Equal(MonitorActionKind.HttpEndpointFailure, action.Kind);

		var hidden = Assert.Single(action.Suppressed);
		Assert.Equal(MonitorActionKind.OrphanedChildren, hidden.Kind);
		Assert.Null(hidden.FeedId);
		Assert.Equal("superseded_by:http_endpoint_failure", hidden.Reason);
	}

	[Fact]
	public void Orphans_AreSuppressedByAnAuthFailureInTheSameDataset()
	{
		var actions = ActionRouter.Route(new ActionRouterInputs
		{
			AuthFailures = [AuthFailure("f1", 10)],
			Orphans = [OrphanIncident()],
		});

		var action = Assert.Single(actions);
		Assert.Equal(MonitorActionKind.AuthenticationRequired, action.Kind);
		Assert.Equal("superseded_by:authentication_required", Assert.Single(action.Suppressed).Reason);
	}

	[Fact]
	public void Orphans_AreSuppressedByASingleFeedStallInTheSameDataset()
	{
		var actions = ActionRouter.Route(new ActionRouterInputs
		{
			SingleFeedStalls = [FeedStall("f1", 10)],
			FeedKinds = Kinds(("f1", "Slot")),
			Orphans = [OrphanIncident()],
		});

		var action = Assert.Single(actions);
		Assert.Equal(MonitorActionKind.SingleFeedStall, action.Kind);
		Assert.Equal("superseded_by:single_feed_stall", Assert.Single(action.Suppressed).Reason);
	}

	[Fact]
	public void Orphans_AreSuppressedByTheEarliestPreCheckThatExplainsThem()
	{
		var actions = ActionRouter.Route(new ActionRouterInputs
		{
			AuthFailures = [AuthFailure("f2", 10)],
			HttpFailures = [HttpFailure("f1", 10)],
			Orphans = [OrphanIncident()],
		});

		Assert.Equal(2, actions.Count);
		Assert.Equal(MonitorActionKind.HttpEndpointFailure, actions[0].Kind);
		Assert.Single(actions[0].Suppressed);
		Assert.Empty(actions[1].Suppressed);
	}

	[Fact]
	public void Orphans_AreNotSuppressedByAnUntriggeredFailure()
	{
		// Nine days is not yet an action, so nobody is being contacted about it.
		var actions = ActionRouter.Route(new ActionRouterInputs
		{
			HttpFailures = [HttpFailure("f1", 9)],
			Orphans = [OrphanIncident()],
		});

		Assert.Equal(MonitorActionKind.OrphanedChildren, Assert.Single(actions).Kind);
	}

	[Fact]
	public void Orphans_AreNotSuppressedByAFailureInAnotherDataset()
	{
		var actions = ActionRouter.Route(new ActionRouterInputs
		{
			HttpFailures = [HttpFailure("f1", 10, "d2")],
			Orphans = [OrphanIncident(dataset: "d1")],
		});

		Assert.Equal(2, actions.Count);
		Assert.Empty(actions[0].Suppressed);
		Assert.Equal(MonitorActionKind.OrphanedChildren, actions[1].Kind);
	}

	[Fact]
	public void Orphans_AreNotSuppressedByADatasetStall()
	{
		// The tree does not list dataset stalls as a pre-check for this row.
		var actions = ActionRouter.Route(new ActionRouterInputs
		{
			DatasetStalls = [WholeDatasetStall(10)],
			Orphans = [OrphanIncident()],
		});

		Assert.Equal(2, actions.Count);
		Assert.Empty(actions[0].Suppressed);
		Assert.Equal(MonitorActionKind.OrphanedChildren, actions[1].Kind);
	}

	#endregion

	#region ID-space change

	[Fact]
	public void Orphans_PointingAtADifferentIdFormat_BecomeAnIdPatternAction()
	{
		var actions = ActionRouter.Route(new ActionRouterInputs
		{
			Orphans = [OrphanIncident(kind: "Slot", missingIds: ["session-1", "session-2"])],
			ParentIdSamples = [ParentSample("FacilityUse", ["a1b2c3d4-0000-0000-0000-000000000001"])],
		});

		var action = Assert.Single(actions);
		Assert.Equal(MonitorActionKind.IdPatternMismatch, action.Kind);
		// The orphan figures survive: they still say how much of the dataset is affected.
		Assert.Equal(0.25, action.Share);

		var incident = Assert.IsType<IdPatternIncident>(action.Incident);
		var mismatch = Assert.Single(incident.ByKind);
		Assert.Equal("Slot", mismatch.Kind);
		Assert.Equal(["session-{n}"], mismatch.Mismatch.ReferencedPatterns);
		Assert.Equal(["{uuid}"], mismatch.Mismatch.ParentPatterns);

		Assert.Equal("superseded_by:id_pattern_mismatch", Assert.Single(action.Suppressed).Reason);
	}

	[Fact]
	public void Orphans_PointingAtTheSameIdFormat_StayAnOrphanAction()
	{
		var actions = ActionRouter.Route(new ActionRouterInputs
		{
			Orphans = [OrphanIncident(kind: "Slot", missingIds: ["fac-99"])],
			ParentIdSamples = [ParentSample("FacilityUse", ["fac-1", "fac-2"])],
		});

		Assert.Equal(MonitorActionKind.OrphanedChildren, Assert.Single(actions).Kind);
	}

	[Fact]
	public void Orphans_WithNoParentSample_StayAnOrphanAction()
	{
		// No evidence either way, and an unevidenced id change must not be put to a publisher.
		var actions = ActionRouter.Route(new ActionRouterInputs
		{
			Orphans = [OrphanIncident(kind: "Slot", missingIds: ["session-1"])],
		});

		Assert.Equal(MonitorActionKind.OrphanedChildren, Assert.Single(actions).Kind);
	}

	[Fact]
	public void Orphans_IgnoreASampleOfTheWrongParentKind()
	{
		// Slots reference FacilityUses; a SessionSeries sample says nothing about them.
		var actions = ActionRouter.Route(new ActionRouterInputs
		{
			Orphans = [OrphanIncident(kind: "Slot", missingIds: ["session-1"])],
			ParentIdSamples = [ParentSample("SessionSeries", ["a1b2c3d4-0000-0000-0000-000000000001"])],
		});

		Assert.Equal(MonitorActionKind.OrphanedChildren, Assert.Single(actions).Kind);
	}

	#endregion
}