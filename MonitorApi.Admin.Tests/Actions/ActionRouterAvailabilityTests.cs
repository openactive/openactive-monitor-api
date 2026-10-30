using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.Actions;

/// <summary>
/// Deterministic tests for the decision tree's availability rows. No BigQuery credentials needed.
/// </summary>
public class ActionRouterAvailabilityTests
{
	private static readonly DateOnly AsOf = new(2026, 9, 9);

	#region Builders

	private static FeedIngestionError HttpFailure(string feedId, int days, string dataset = "d1") => new()
	{
		FeedId = feedId,
		DatasetId = dataset,
		LastCompleted = AsOf.AddDays(-days),
		ConsecutiveDays = days,
		PastThreshold = false,
		ErrorCode = "500",
		ErrorMessage = "HTTP 500",
		Trend = [],
	};

	private static AuthRequired AuthFailure(string feedId, int days, string dataset = "d1") => new()
	{
		FeedId = feedId,
		DatasetId = dataset,
		FirstFailed = AsOf.AddDays(-(days - 1)),
		ConsecutiveDays = days,
		ErrorCode = "403",
		ErrorMessage = "HTTP 403",
	};

	private static DatasetStall WholeDatasetStall(int days, string dataset = "d1") => new()
	{
		DatasetId = dataset,
		LastPublished = AsOf.AddDays(-days),
		ConsecutiveDays = days,
		PastThreshold = false,
		Feeds = [],
		Trend = [],
	};

	private static SingleFeedStall FeedStall(string feedId, int days, string dataset = "d1") => new()
	{
		FeedId = feedId,
		DatasetId = dataset,
		LastPublished = AsOf.AddDays(-days),
		ConsecutiveDays = days,
		PastThreshold = false,
		Trend = [],
	};

	private static Dictionary<string, string> Kinds(params (string FeedId, string Kind)[] feeds) =>
		feeds.ToDictionary(f => f.FeedId, f => f.Kind, StringComparer.Ordinal);

	#endregion

	#region HTTP endpoint failures

	[Fact]
	public void HttpFailure_BelowTrigger_IsNotAnAction()
	{
		var actions = ActionRouter.Route(new ActionRouterInputs { HttpFailures = [HttpFailure("f1", 9)] });

		Assert.Empty(actions);
	}

	[Fact]
	public void HttpFailure_AtTrigger_IsAnAction()
	{
		var actions = ActionRouter.Route(new ActionRouterInputs { HttpFailures = [HttpFailure("f1", 10)] });

		var action = Assert.Single(actions);
		Assert.Equal(MonitorActionKind.HttpEndpointFailure, action.Kind);
		Assert.Equal("d1", action.DatasetUrl);
		Assert.Equal("f1", action.FeedId);
		Assert.Equal(10, action.ConsecutiveDays);
		// The last completed day was 10 days ago, so the failure began 9 days ago.
		Assert.Equal(AsOf.AddDays(-9), action.FirstDetected);
		Assert.Empty(action.Suppressed);
	}

	#endregion

	#region Authentication required

	[Fact]
	public void AuthFailure_BelowTrigger_IsNotAnAction()
	{
		var actions = ActionRouter.Route(new ActionRouterInputs { AuthFailures = [AuthFailure("f1", 9)] });

		Assert.Empty(actions);
	}

	[Fact]
	public void AuthFailure_AtTrigger_IsAnAction()
	{
		var actions = ActionRouter.Route(new ActionRouterInputs { AuthFailures = [AuthFailure("f1", 10)] });

		var action = Assert.Single(actions);
		Assert.Equal(MonitorActionKind.AuthenticationRequired, action.Kind);
		Assert.Equal("f1", action.FeedId);
		Assert.Equal(AsOf.AddDays(-9), action.FirstDetected);
	}

	#endregion

	#region Dataset-wide stalls

	[Fact]
	public void DatasetStall_BelowTrigger_IsNotAnAction()
	{
		var actions = ActionRouter.Route(new ActionRouterInputs { DatasetStalls = [WholeDatasetStall(4)] });

		Assert.Empty(actions);
	}

	[Fact]
	public void DatasetStall_AtTrigger_IsADatasetLevelAction()
	{
		var actions = ActionRouter.Route(new ActionRouterInputs { DatasetStalls = [WholeDatasetStall(5)] });

		var action = Assert.Single(actions);
		Assert.Equal(MonitorActionKind.DatasetStall, action.Kind);
		Assert.Equal("d1", action.DatasetUrl);
		// Dataset-wide: there is no one feed to name.
		Assert.Null(action.FeedId);
	}

	#endregion

	#region Single-feed stalls

	[Fact]
	public void FeedStall_BelowTrigger_IsNotAnAction()
	{
		var actions = ActionRouter.Route(new ActionRouterInputs
		{
			SingleFeedStalls = [FeedStall("f1", 9)],
			FeedKinds = Kinds(("f1", "Slot")),
		});

		Assert.Empty(actions);
	}

	[Theory]
	[InlineData("Slot")]
	[InlineData("ScheduledSession")]
	[InlineData("scheduledsession")]
	public void FeedStall_AtTrigger_OnASessionKind_IsAnAction(string kind)
	{
		var actions = ActionRouter.Route(new ActionRouterInputs
		{
			SingleFeedStalls = [FeedStall("f1", 10)],
			FeedKinds = Kinds(("f1", kind)),
		});

		var action = Assert.Single(actions);
		Assert.Equal(MonitorActionKind.SingleFeedStall, action.Kind);
		Assert.Equal("f1", action.FeedId);
	}

	[Theory]
	[InlineData("SessionSeries")]
	[InlineData("FacilityUse")]
	public void FeedStall_OnAParentKind_IsNotAnAction(string kind)
	{
		// The tree limits this row to the feeds that carry bookable availability.
		var actions = ActionRouter.Route(new ActionRouterInputs
		{
			SingleFeedStalls = [FeedStall("f1", 30)],
			FeedKinds = Kinds(("f1", kind)),
		});

		Assert.Empty(actions);
	}

	[Fact]
	public void FeedStall_WithUnknownKind_IsNotAnAction()
	{
		// No feeds row for f1. The row ends in an email, so an unknown kind must fail closed.
		var actions = ActionRouter.Route(new ActionRouterInputs { SingleFeedStalls = [FeedStall("f1", 30)] });

		Assert.Empty(actions);
	}

	#endregion

	#region Triggers and ordering

	[Fact]
	public void Triggers_CanBeOverridden()
	{
		var actions = ActionRouter.Route(
			new ActionRouterInputs { HttpFailures = [HttpFailure("f1", 3)] },
			new ActionTriggers { HttpFailureDays = 3 });

		Assert.Single(actions);
	}

	[Fact]
	public void Actions_AreOrderedWorstRowFirst()
	{
		var actions = ActionRouter.Route(new ActionRouterInputs
		{
			SingleFeedStalls = [FeedStall("f3", 30)],
			DatasetStalls = [WholeDatasetStall(30)],
			AuthFailures = [AuthFailure("f2", 30)],
			HttpFailures = [HttpFailure("f1", 30)],
			FeedKinds = Kinds(("f3", "Slot")),
		});

		Assert.Equal(
			[
				MonitorActionKind.HttpEndpointFailure,
				MonitorActionKind.AuthenticationRequired,
				MonitorActionKind.DatasetStall,
				MonitorActionKind.SingleFeedStall,
			],
			actions.Select(a => a.Kind));
	}

	[Fact]
	public void ActionsOfOneKind_AreOrderedLongestFirstThenByDatasetAndFeed()
	{
		var actions = ActionRouter.Route(new ActionRouterInputs
		{
			HttpFailures =
			[
				HttpFailure("b", 10, "d2"),
				HttpFailure("a", 10, "d2"),
				HttpFailure("c", 10, "d1"),
				HttpFailure("d", 20, "d9"),
			],
		});

		Assert.Equal(["d", "c", "a", "b"], actions.Select(a => a.FeedId));
	}

	#endregion
}