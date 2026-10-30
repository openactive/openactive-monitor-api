namespace MonitorApi.Services.Admin;

/// <summary>
/// Everything the router decides on for one snapshot day: the incidents each monitor detected, plus
/// the feed metadata the decision tree's kind filters need.
/// </summary>
/// <remarks>
/// Future-decline inputs arrive with the row that uses them.
/// </remarks>
public sealed record ActionRouterInputs
{
	/// <summary>Feeds failing with a non-auth HTTP code.</summary>
	public IReadOnlyList<FeedIngestionError> HttpFailures { get; init; } = [];

	/// <summary>Feeds failing with 401 or 403.</summary>
	public IReadOnlyList<AuthRequired> AuthFailures { get; init; } = [];

	public IReadOnlyList<DatasetStall> DatasetStalls { get; init; } = [];

	public IReadOnlyList<SingleFeedStall> SingleFeedStalls { get; init; } = [];


	/// <summary>Datasets publishing children whose parent is missing from the same dataset.</summary>
	public IReadOnlyList<OrphanedChildren> Orphans { get; init; } = [];

	/// <summary>
	/// A sample of the parent ids each dataset publishes, for the id-format pre-check on the orphan row.
	/// </summary>
	public IReadOnlyList<ParentIdSample> ParentIdSamples { get; init; } = [];

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
		var actions = new ActionList();

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

		// Keyed by dataset and parent kind so each child kind is compared against the right parents.
		var parentSamples = inputs.ParentIdSamples.ToLookup(s => (s.DatasetUrl, s.Kind));

		foreach (var orphan in inputs.Orphans)
		{
			if (orphan.OrphanShare < thresholds.OrphanShare)
			{
				continue;
			}

			// Matched by dataset url, not by feed: an orphan incident has no feed_id — it is counted
			// across the whole dataset — so "is this already explained?" can only be asked at dataset
			// level. Only a triggered action suppresses, because only a triggered action means the
			// publisher is already being contacted about the cause.
			if (actions.FirstFor(orphan.DatasetUrl, OrphanPreChecks) is { } explained)
			{
				actions.Suppress(explained, MonitorActionKind.OrphanedChildren);
				continue;
			}

			var mismatches = IdPatternMismatches(orphan, parentSamples);
			var changedIds = mismatches.Count > 0;

			actions.Add(new MonitorAction
			{
				Kind = changedIds ? MonitorActionKind.IdPatternMismatch : MonitorActionKind.OrphanedChildren,
				DatasetUrl = orphan.DatasetUrl,
				// No FirstDetected: opportunities holds current state only, so there is no day the
				// problem started.
				Share = orphan.OrphanShare,
				Suppressed = changedIds
					? [new SuppressedIncident(
						MonitorActionKind.OrphanedChildren,
						null,
						SuppressionReasons.SupersededBy(MonitorActionKind.IdPatternMismatch))]
					: [],
				Incident = changedIds ? new IdPatternIncident(orphan, mismatches) : orphan,
			});
		}

		return actions.Build();
	}

	/// <summary>
	/// Whether a feed is one of the kinds the tree limits a row to. A feed whose kind is unknown is
	/// excluded: these rows end in an email to a publisher, and the tree asks for them only about Slot
	/// and ScheduledSession feeds, so a missing <c>feeds</c> row must not be read as permission to send
	/// one.
	/// </summary>
	private static bool IsSessionKind(string feedId, ActionRouterInputs inputs, ActionTriggers triggers) =>
		inputs.FeedKinds.TryGetValue(feedId, out var kind) && triggers.SessionKinds.Contains(kind);


	/// <summary>
	/// Problems that explain orphaned children, in the order the tree checks them. Dataset stalls are
	/// deliberately absent: the tree does not list them for this row.
	/// </summary>
	private static readonly string[] OrphanPreChecks =
	[
		MonitorActionKind.HttpEndpointFailure,
		MonitorActionKind.AuthenticationRequired,
		MonitorActionKind.SingleFeedStall,
	];

	/// <summary>
	/// Child kinds whose missing parents are in a different id format from the parents the dataset
	/// publishes. Empty when the formats agree, and empty when there is nothing to compare — no parent
	/// sample is no evidence, and an unevidenced id change must not be put to a publisher.
	/// </summary>
	private static IReadOnlyList<KindIdPatternMismatch> IdPatternMismatches(
		OrphanedChildren orphan,
		ILookup<(string DatasetUrl, string Kind), ParentIdSample> parentSamples)
	{
		var mismatches = new List<KindIdPatternMismatch>();

		foreach (var kind in orphan.ByKind)
		{
			// Slots are compared against FacilityUses, ScheduledSessions against SessionSeries. A kind
			// with no parent mapping is not one this check knows how to read.
			if (IdPatternCheck.ParentKindFor(kind.Kind) is not { } parentKind)
			{
				continue;
			}

			var mismatch = IdPatternCheck.Compare(
				kind.MissingParents.Select(p => p.MissingId),
				parentSamples[(orphan.DatasetUrl, parentKind)].SelectMany(s => s.SampleIds));

			if (mismatch is not null)
			{
				mismatches.Add(new KindIdPatternMismatch(kind.Kind, mismatch));
			}
		}

		return mismatches;
	}

	/// <summary>
	/// Actions under construction. <see cref="MonitorAction"/> is immutable, but a pre-check only
	/// discovers what an action superseded after that action has been built, so the suppression lists
	/// are gathered alongside and folded in once, at <see cref="Build"/>.
	/// </summary>
	private sealed class ActionList
	{
		private readonly List<MonitorAction> actions = [];
		private readonly List<List<SuppressedIncident>> suppressed = [];

		public void Add(MonitorAction action)
		{
			actions.Add(action);
			suppressed.Add([]);
		}

		/// <summary>
		/// Index of the first action for <paramref name="datasetUrl"/> whose kind is in
		/// <paramref name="kinds"/>, tried in the order given; <c>null</c> when there is none.
		/// </summary>
		public int? FirstFor(string datasetUrl, IReadOnlyList<string> kinds)
		{
			foreach (var kind in kinds)
			{
				var index = actions.FindIndex(a =>
					a.Kind == kind && string.Equals(a.DatasetUrl, datasetUrl, StringComparison.Ordinal));

				if (index >= 0)
				{
					return index;
				}
			}

			return null;
		}

		/// <summary>Records an incident the action at <paramref name="index"/> was reported instead of.</summary>
		public void Suppress(int index, string kind, string? feedId = null) =>
			suppressed[index].Add(new SuppressedIncident(
				kind, feedId, SuppressionReasons.SupersededBy(actions[index].Kind)));

		/// <summary>
		/// The finished list, worst first. The order is total: ties are common — a dataset's feeds fail
		/// together on the same day — and without the last two tiebreaks two pages of one result set
		/// could overlap or drop a row.
		/// </summary>
		public IReadOnlyList<MonitorAction> Build() =>
			actions
				.Select((action, i) => suppressed[i].Count == 0 ? action : action with { Suppressed = suppressed[i] })
				.OrderBy(a => Priority(a.Kind))
				.ThenByDescending(a => a.ConsecutiveDays ?? 0)
				.ThenBy(a => a.DatasetUrl, StringComparer.Ordinal)
				.ThenBy(a => a.FeedId ?? string.Empty, StringComparer.Ordinal)
				.ToList();
	}
}