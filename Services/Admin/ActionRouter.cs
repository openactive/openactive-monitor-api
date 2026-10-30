namespace MonitorApi.Services.Admin;

/// <summary>
/// Everything the router decides on for one snapshot day: the incidents each monitor detected, plus
/// the feed metadata the decision tree's kind filters need.
/// </summary>
/// <remarks>
/// Orphan and future-decline inputs arrive with the rows that use them.
/// </remarks>
public sealed record ActionRouterInputs
{
	/// <summary>Feeds failing with a non-auth HTTP code.</summary>
	public IReadOnlyList<FeedIngestionError> HttpFailures { get; init; } = [];

	/// <summary>Feeds failing with 401 or 403.</summary>
	public IReadOnlyList<AuthRequired> AuthFailures { get; init; } = [];

	public IReadOnlyList<DatasetStall> DatasetStalls { get; init; } = [];

	public IReadOnlyList<SingleFeedStall> SingleFeedStalls { get; init; } = [];

	/// <summary>
	/// Feed id to <c>feeds.type</c>, for the rows the tree limits to Slot and ScheduledSession feeds.
	/// A feed absent from this map has an unknown kind.
	/// </summary>
	public IReadOnlyDictionary<string, string> FeedKinds { get; init; } =
		new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Pure implementation of the Monitor Actions Decision Tree. It turns the monitors' incidents into the
/// things a steward should act on, applying each row's trigger and its pre-checks.
/// </summary>
public static class ActionRouter
{
	/// <summary>
	/// Decision-tree rows worst first. The order a steward reads them in, and — from the orphan row
	/// onwards — the order the pre-checks suppress in.
	/// </summary>
	private static readonly string[] KindOrder =
	[
		MonitorActionKind.HttpEndpointFailure,
		MonitorActionKind.AuthenticationRequired,
		MonitorActionKind.DatasetStall,
		MonitorActionKind.SingleFeedStall,
		MonitorActionKind.IdPatternMismatch,
		MonitorActionKind.OrphanedChildren,
		MonitorActionKind.FutureDecline,
	];

	/// <summary>Rank of a kind in <see cref="KindOrder"/>; an unknown kind sorts last.</summary>
	public static int Priority(string kind)
	{
		var index = Array.IndexOf(KindOrder, kind);
		return index < 0 ? KindOrder.Length : index;
	}

	/// <summary>
	/// Applies the decision tree to one snapshot day's incidents, worst first.
	/// </summary>
	public static IReadOnlyList<MonitorAction> Route(ActionRouterInputs inputs, ActionTriggers? triggers = null)
	{
		var thresholds = triggers ?? new ActionTriggers();
		var actions = new List<MonitorAction>();

		foreach (var failure in inputs.HttpFailures)
		{
			if (failure.ConsecutiveDays < thresholds.HttpFailureDays)
			{
				continue;
			}

			actions.Add(new MonitorAction
			{
				Kind = MonitorActionKind.HttpEndpointFailure,
				DatasetUrl = failure.DatasetId,
				FeedId = failure.FeedId,
				// LastCompleted is the last day that worked, so the failure began the day after.
				FirstDetected = failure.LastCompleted.AddDays(1),
				ConsecutiveDays = failure.ConsecutiveDays,
				Incident = failure,
			});
		}

		foreach (var failure in inputs.AuthFailures)
		{
			if (failure.ConsecutiveDays < thresholds.AuthRequiredDays)
			{
				continue;
			}

			actions.Add(new MonitorAction
			{
				Kind = MonitorActionKind.AuthenticationRequired,
				DatasetUrl = failure.DatasetId,
				FeedId = failure.FeedId,
				FirstDetected = failure.FirstFailed,
				ConsecutiveDays = failure.ConsecutiveDays,
				Incident = failure,
			});
		}

		foreach (var stall in inputs.DatasetStalls)
		{
			if (stall.ConsecutiveDays < thresholds.DatasetStallDays)
			{
				continue;
			}

			actions.Add(new MonitorAction
			{
				Kind = MonitorActionKind.DatasetStall,
				DatasetUrl = stall.DatasetId,
				// Dataset-wide: no single feed to name.
				FirstDetected = stall.LastPublished.AddDays(1),
				ConsecutiveDays = stall.ConsecutiveDays,
				Incident = stall,
			});
		}

		foreach (var stall in inputs.SingleFeedStalls)
		{
			if (stall.ConsecutiveDays < thresholds.SingleFeedStallDays)
			{
				continue;
			}

			if (!IsSessionKind(stall.FeedId, inputs, thresholds))
			{
				continue;
			}

			actions.Add(new MonitorAction
			{
				Kind = MonitorActionKind.SingleFeedStall,
				DatasetUrl = stall.DatasetId,
				FeedId = stall.FeedId,
				FirstDetected = stall.LastPublished.AddDays(1),
				ConsecutiveDays = stall.ConsecutiveDays,
				Incident = stall,
			});
		}

		// Total order: ties are common — a dataset's feeds fail together on the same day — and without
		// the last two tiebreaks two pages of one result set could overlap or drop a row.
		return actions
			.OrderBy(a => Priority(a.Kind))
			.ThenByDescending(a => a.ConsecutiveDays ?? 0)
			.ThenBy(a => a.DatasetUrl, StringComparer.Ordinal)
			.ThenBy(a => a.FeedId ?? string.Empty, StringComparer.Ordinal)
			.ToList();
	}

	/// <summary>
	/// Whether a feed is one of the kinds the tree limits a row to. A feed whose kind is unknown is
	/// excluded: these rows end in an email to a publisher, and the tree asks for them only about Slot
	/// and ScheduledSession feeds, so a missing <c>feeds</c> row must not be read as permission to send
	/// one.
	/// </summary>
	private static bool IsSessionKind(string feedId, ActionRouterInputs inputs, ActionTriggers triggers) =>
		inputs.FeedKinds.TryGetValue(feedId, out var kind) && triggers.SessionKinds.Contains(kind);
}