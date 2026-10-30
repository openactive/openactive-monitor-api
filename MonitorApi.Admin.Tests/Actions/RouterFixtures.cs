using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.Actions;

/// <summary>Incident builders shared by the decision-tree router tests.</summary>
internal static class RouterFixtures
{
	/// <summary>The day every incident below is detected on.</summary>
	public static readonly DateOnly AsOf = new(2026, 9, 9);

	public static FeedIngestionError HttpFailure(string feedId, int days, string dataset = "d1") => new()
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

	public static AuthRequired AuthFailure(string feedId, int days, string dataset = "d1") => new()
	{
		FeedId = feedId,
		DatasetId = dataset,
		FirstFailed = AsOf.AddDays(-(days - 1)),
		ConsecutiveDays = days,
		ErrorCode = "403",
		ErrorMessage = "HTTP 403",
	};

	public static DatasetStall WholeDatasetStall(int days, string dataset = "d1") => new()
	{
		DatasetId = dataset,
		LastPublished = AsOf.AddDays(-days),
		ConsecutiveDays = days,
		PastThreshold = false,
		Feeds = [],
		Trend = [],
	};

	public static SingleFeedStall FeedStall(string feedId, int days, string dataset = "d1") => new()
	{
		FeedId = feedId,
		DatasetId = dataset,
		LastPublished = AsOf.AddDays(-days),
		ConsecutiveDays = days,
		PastThreshold = false,
		Trend = [],
	};

	public static Dictionary<string, string> Kinds(params (string FeedId, string Kind)[] feeds) =>
		feeds.ToDictionary(f => f.FeedId, f => f.Kind, StringComparer.Ordinal);

	/// <summary>
	/// A dataset publishing orphaned children of one kind. <paramref name="share"/> is set directly
	/// rather than derived, so a test can sit a hair either side of the trigger without arithmetic.
	/// </summary>
	public static OrphanedChildren OrphanIncident(
		double share = 0.25,
		string dataset = "d1",
		string kind = "Slot",
		IReadOnlyList<string>? missingIds = null)
	{
		IReadOnlyList<string> ids = missingIds ?? ["p1"];

		var byKind = new DatasetKindOrphanCounts(
			dataset,
			kind,
			ChildCount: 1_000,
			CheckedCount: 500,
			OrphanCount: 250,
			MissingParentCount: ids.Count,
			MissingParents: ids.Select(id => new MissingParent(id, 1)).ToList());

		return new OrphanedChildren
		{
			DatasetUrl = dataset,
			ChildCount = byKind.ChildCount,
			CheckedCount = byKind.CheckedCount,
			OrphanCount = byKind.OrphanCount,
			OrphanShare = share,
			MissingParentCount = byKind.MissingParentCount,
			ByKind = [byKind],
			MissingParents = byKind.MissingParents,
			PastThreshold = false,
		};
	}

	public static ParentIdSample ParentSample(string kind, IReadOnlyList<string> ids, string dataset = "d1") =>
		new(dataset, kind, ids);
}