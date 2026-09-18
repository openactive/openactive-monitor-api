namespace MonitorApi.Models.Admin;

/// <summary>
/// Ecosystem-level quality figures: how much of the estate is assessed, how much of it is healthy,
/// how complete its data is and how it scores.
/// </summary>
/// <remarks>
/// Describes <b>every row the request's filters matched</b>, not the page returned, so it does not
/// change as the caller walks the pages. It is reduced from the very rows being paged, so it can
/// never disagree with them.
///
/// Two conventions run through it. Counts are whole-estate and always sum to
/// <see cref="TotalFeeds"/> — a feed whose status, grade, type or version is missing is counted as
/// unknown rather than dropped. Averages are unweighted means over the feeds that <em>report</em> the
/// value, published next to the count they were taken over, and are <c>null</c> rather than zero when
/// nothing reported the value at all.
/// </remarks>
public sealed class FeedQualitySummary
{
	/// <summary>Feeds assessed, and the number of rows behind this summary.</summary>
	public required int TotalFeeds { get; init; }

	/// <summary>Distinct datasets those feeds belong to.</summary>
	public required int TotalDatasets { get; init; }

	/// <summary>
	/// Distinct publishers behind those datasets. Feeds whose dataset has no <c>feeds</c> row have no
	/// publisher and are counted in none of them, so this can be lower than <see cref="TotalDatasets"/>
	/// for reasons other than publishers owning several datasets.
	/// </summary>
	public required int TotalPublishers { get; init; }

	/// <summary>Feeds the assessment found to publish on a regular schedule.</summary>
	public required int RegularFeeds { get; init; }

	/// <summary>Feeds the assessment found <em>not</em> to publish on a regular schedule.</summary>
	public required int IrregularFeeds { get; init; }

	/// <summary>Feeds whose regularity the assessment did not determine.</summary>
	public required int RegularityUnknown { get; init; }

	/// <summary>Feeds with status <c>OK</c>.</summary>
	public required int FeedsOk { get; init; }

	/// <summary>Feeds with status <c>WARNING</c>.</summary>
	public required int FeedsWithWarnings { get; init; }

	/// <summary>Feeds with status <c>ERROR</c>.</summary>
	public required int FeedsWithErrors { get; init; }

	/// <summary>Feeds with no status, or one outside <c>OK</c>/<c>WARNING</c>/<c>ERROR</c>.</summary>
	public required int FeedsStatusUnknown { get; init; }

	/// <summary>Distinct datasets with at least one <c>ERROR</c> feed — the publishers worth contacting.</summary>
	public required int DatasetsWithErrors { get; init; }

	/// <summary>Feeds currently offering at least one future opportunity item.</summary>
	public required int FeedsWithFutureData { get; init; }

	/// <summary>Distinct datasets with at least one feed offering future data.</summary>
	public required int DatasetsWithFutureData { get; init; }

	/// <summary>Future opportunity items across every feed. A feed reporting no figure contributes nothing.</summary>
	public required long TotalFutureOpportunityItems { get; init; }

	/// <summary>Feeds carrying a score, and the denominator of every figure in this group.</summary>
	public required int FeedsScored { get; init; }

	/// <summary>Mean score across the scored feeds, 0–100. <c>null</c> when none is scored.</summary>
	public required double? AverageScore { get; init; }

	/// <summary>
	/// Middle score across the scored feeds, averaging the two middle values for an even count.
	/// <c>null</c> when none is scored. Worth reading next to <see cref="AverageScore"/>: a handful of
	/// very poor feeds drag the mean but not the median.
	/// </summary>
	public required double? MedianScore { get; init; }

	/// <summary>Lowest score across the scored feeds. <c>null</c> when none is scored.</summary>
	public required double? MinScore { get; init; }

	/// <summary>Highest score across the scored feeds. <c>null</c> when none is scored.</summary>
	public required double? MaxScore { get; init; }

	/// <summary>
	/// The score histogram: always five buckets of twenty, in ascending order, so the shape is constant
	/// whatever the data. Counts sum to <see cref="FeedsScored"/>.
	/// </summary>
	public required IReadOnlyList<FeedQualityScoreBucket> ScoreBuckets { get; init; }

	/// <summary>Mean completeness per property, each with the number of feeds it was averaged over.</summary>
	public required FeedQualityCompleteness Completeness { get; init; }

	/// <summary>Feeds and datasets per <c>status</c>.</summary>
	public required IReadOnlyList<FeedQualityBreakdown> StatusBreakdown { get; init; }

	/// <summary>Feeds and datasets per <c>grade</c>.</summary>
	public required IReadOnlyList<FeedQualityBreakdown> GradeBreakdown { get; init; }

	/// <summary>Feeds and datasets per <c>feed_type</c>.</summary>
	public required IReadOnlyList<FeedQualityBreakdown> FeedTypeBreakdown { get; init; }

	/// <summary>Feeds and datasets per detected <c>feed_version</c> — how much of the estate is on the current spec.</summary>
	public required IReadOnlyList<FeedQualityBreakdown> FeedVersionBreakdown { get; init; }

	/// <summary>Earliest <c>last_assessed</c> across the feeds. UTC. <c>null</c> when none is assessed.</summary>
	public required DateTime? OldestAssessment { get; init; }

	/// <summary>Latest <c>last_assessed</c> across the feeds. UTC. <c>null</c> when none is assessed.</summary>
	public required DateTime? NewestAssessment { get; init; }
}

/// <summary>
/// How many feeds and datasets carry one value of a column. Used for status, grade, feed type and
/// feed version alike, so the dashboard renders all four the same way.
/// </summary>
public sealed class FeedQualityBreakdown
{
	/// <summary>
	/// The value, as stored. <c>unknown</c> where the column is <c>NULL</c> or blank, so the counts
	/// always account for every feed.
	/// </summary>
	public required string Value { get; init; }

	/// <summary>Feeds with this value.</summary>
	public required int FeedCount { get; init; }

	/// <summary>Distinct datasets with at least one feed of this value. Sums above the dataset total where datasets are mixed.</summary>
	public required int DatasetCount { get; init; }

	/// <summary><see cref="FeedCount"/> as a fraction of the summary's total feeds, 0–1.</summary>
	public required double Share { get; init; }
}

/// <summary>One bar of the score histogram: <c>lower</c> inclusive, <c>upper</c> exclusive except in the top bucket, which includes 100.</summary>
public sealed class FeedQualityScoreBucket
{
	public required int Lower { get; init; }

	public required int Upper { get; init; }

	public required int FeedCount { get; init; }
}

/// <summary>
/// Mean completeness per property across the summarised feeds.
/// </summary>
/// <remarks>
/// Each is its own <see cref="FeedQualityAverage"/> rather than a bare number because the denominators
/// differ: <c>facilities_completeness</c> is only meaningful for facility feeds, so its mean is taken
/// over far fewer feeds than <c>location_completeness</c>, and the two are not comparable without
/// knowing that.
/// </remarks>
public sealed class FeedQualityCompleteness
{
	public required FeedQualityAverage Location { get; init; }

	public required FeedQualityAverage StartDate { get; init; }

	public required FeedQualityAverage EndDate { get; init; }

	public required FeedQualityAverage Activities { get; init; }

	public required FeedQualityAverage Facilities { get; init; }

	public required FeedQualityAverage AgeRange { get; init; }

	public required FeedQualityAverage Level { get; init; }

	public required FeedQualityAverage AccessibilitySupport { get; init; }

	public required FeedQualityAverage GenderRestriction { get; init; }
}

/// <summary>A mean and the number of feeds it was taken over.</summary>
public sealed class FeedQualityAverage
{
	/// <summary>
	/// Unweighted mean over the feeds reporting the value, on the same 0–100 scale as the rows.
	/// <c>null</c> — not <c>0</c> — when no feed reports it.
	/// </summary>
	public required double? Average { get; init; }

	/// <summary>Feeds the mean was taken over. Read it before reading the mean.</summary>
	public required int FeedsReporting { get; init; }
}
