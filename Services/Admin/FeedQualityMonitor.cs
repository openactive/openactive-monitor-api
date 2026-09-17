// Everything the feed-quality surface is made of, in one file: the per-feed assessment it reads, the
// SQL that loads it, and the pure summariser that reduces a set of those assessments to the
// ecosystem-level figures — the same one-file convention as OrphanedChildrenMonitor.cs, and the same
// split between *deciding* and *fetching* enforced by type rather than by file: FeedQualitySummariser
// references no BigQuery and no ASP.NET, so it is unit tested without credentials.
//
// Unlike its siblings in this folder this is not a monitor: feed_quality is current state with no
// history, so nothing here detects an incident, opens one, or tracks one over time.

using System.Text.Json;
using Google.Cloud.BigQuery.V2;
using MonitorApi.Models;
using MonitorApi.Models.Admin;

namespace MonitorApi.Services.Admin;

/// <summary>
/// One feed's quality assessment, as stored in <c>feed_quality</c>: the row the endpoint returns and
/// the summariser reduces.
/// </summary>
/// <remarks>
/// Every field but the two identifiers is nullable, because every column but those two is. A
/// <c>null</c> here means the assessment did not report the value — never that it reported zero — and
/// the summariser keeps the two apart throughout.
/// </remarks>
/// <param name="FeedId">Identifier of the feed, matching <c>feeds.id</c>.</param>
/// <param name="DatasetUrl">Dataset the feed belongs to, matching <c>feeds.dataset_url</c>.</param>
/// <param name="DatasetName">Stored dataset name, or <c>null</c>.</param>
/// <param name="PublisherName">Publisher name joined from <c>feeds</c>, or <c>null</c> when the dataset has no feed row.</param>
/// <param name="FeedType">Opportunity kind the feed publishes, e.g. <c>SessionSeries</c>.</param>
/// <param name="FeedUrl">URL the feed is published at.</param>
/// <param name="IsRegular">Whether the feed publishes on a regular schedule; <c>null</c> when not determined.</param>
/// <param name="Status">Assessment outcome: <c>OK</c>, <c>WARNING</c> or <c>ERROR</c>.</param>
/// <param name="Grade">Quality grade: <c>None</c>, <c>Bronze</c>, <c>Silver</c> or <c>Gold</c>.</param>
/// <param name="FeedVersion">Detected OpenActive specification version, e.g. <c>V2.1</c>.</param>
/// <param name="Score">Normalised quality score, 0–100.</param>
/// <param name="NumFutureOpportunityItems">Opportunity items the feed currently offers in the future.</param>
/// <param name="LastAssessed">When the assessment ran. UTC.</param>
/// <param name="Completeness">The per-property completeness percentages, 0–100.</param>
/// <param name="Warnings">Raw <c>warnings</c> JSON, passed through exactly as stored.</param>
/// <param name="Errors">Raw <c>errors</c> JSON, passed through exactly as stored.</param>
/// <param name="MissingRequiredFields">Raw <c>missing_required_fields</c> JSON, passed through exactly as stored.</param>
public sealed record FeedQuality(
	string FeedId,
	string DatasetUrl,
	string? DatasetName,
	string? PublisherName,
	string? FeedType,
	string? FeedUrl,
	bool? IsRegular,
	string? Status,
	string? Grade,
	string? FeedVersion,
	double? Score,
	long? NumFutureOpportunityItems,
	DateTime? LastAssessed,
	FeedQualityCompletenessValues Completeness,
	JsonElement? Warnings,
	JsonElement? Errors,
	JsonElement? MissingRequiredFields);

/// <summary>
/// The nine completeness figures a feed assessment reports, each the percentage of the feed's items
/// that carry that property, from 0 to 100. <c>null</c> means the assessment did not measure it.
/// </summary>
/// <remarks>
/// Grouped into their own record rather than spread across <see cref="FeedQuality"/> so the summariser
/// can walk them as a list — <see cref="Columns"/> pairs each with its name, which is what keeps
/// <c>FeedQualitySummariser</c> from repeating the same averaging nine times.
/// </remarks>
public sealed record FeedQualityCompletenessValues(
	double? Location,
	double? StartDate,
	double? EndDate,
	double? Activities,
	double? Facilities,
	double? AgeRange,
	double? Level,
	double? AccessibilitySupport,
	double? GenderRestriction)
{
	/// <summary>An assessment that measured nothing.</summary>
	public static readonly FeedQualityCompletenessValues None =
		new(null, null, null, null, null, null, null, null, null);

	/// <summary>
	/// Each figure paired with its wire name, in the order the response presents them. Iterating this
	/// rather than the properties keeps the summary, the row and the averaging from drifting apart.
	/// </summary>
	public IEnumerable<(string Name, double? Value)> Columns =>
	[
		("location", Location),
		("start_date", StartDate),
		("end_date", EndDate),
		("activities", Activities),
		("facilities", Facilities),
		("age_range", AgeRange),
		("level", Level),
		("accessibility_support", AccessibilitySupport),
		("gender_restriction", GenderRestriction),
	];
}

/// <summary>
/// Reduces a set of feed assessments to the ecosystem-level figures the dashboard shows above the
/// table: how much is covered, how much of it is healthy, how complete it is and how it scores.
/// </summary>
/// <remarks>
/// Pure, over plain records: no BigQuery and no ASP.NET, so every rule below is unit tested against
/// hand-written assessments with no credentials — the point of the split this folder keeps.
///
/// The rules that are not obvious from the field names:
///
/// <list type="bullet">
/// <item><description>
/// Averages are unweighted means over the feeds that <em>report</em> the value. A feed whose ratio is
/// <c>null</c> is excluded from both the numerator and the denominator, and each mean is published
/// with the count it was taken over, so a figure drawn from three feeds cannot be read as an estate
/// figure. A column no feed reports averages to <c>null</c>, never to zero.
/// </description></item>
/// <item><description>
/// <c>status</c> and <c>grade</c> are matched case-insensitively. A value that is <c>null</c>, blank
/// or unrecognised is counted as unknown and appears in the breakdown under <c>unknown</c> — never
/// silently dropped, so the counters always sum to <c>total_feeds</c>.
/// </description></item>
/// <item><description>
/// Every breakdown is totally ordered — count descending, then value ascending — so the same input
/// always produces the same response.
/// </description></item>
/// </list>
/// </remarks>
public static class FeedQualitySummariser
{
	/// <summary>Value stood in for a status, grade, feed type or version the assessment did not report.</summary>
	public const string UnknownValue = "unknown";

	/// <summary>Status reported by a feed the assessment found nothing wrong with.</summary>
	public const string StatusOk = "OK";

	/// <summary>Status reported by a feed the assessment raised warnings against.</summary>
	public const string StatusWarning = "WARNING";

	/// <summary>Status reported by a feed the assessment raised errors against.</summary>
	public const string StatusError = "ERROR";

	/// <summary>
	/// Width of a score bucket. Five buckets span the 0–100 score: <c>0–20</c>, <c>20–40</c>,
	/// <c>40–60</c>, <c>60–80</c>, <c>80–100</c>.
	/// </summary>
	public const int ScoreBucketWidth = 20;

	/// <summary>Number of score buckets, covering the whole 0–100 range.</summary>
	public const int ScoreBucketCount = 100 / ScoreBucketWidth;

	/// <summary>
	/// Reduces every assessment given to one summary. Takes the whole filtered result set, not a page:
	/// the figures describe the population the caller asked about, so they do not move as pages are
	/// walked.
	/// </summary>
	public static FeedQualitySummary Summarise(IEnumerable<FeedQuality> feeds)
	{
		var rows = feeds as IReadOnlyList<FeedQuality> ?? feeds.ToList();

		var scores = rows
			.Select(f => f.Score)
			.OfType<double>()
			.OrderBy(s => s)
			.ToList();

		var assessed = rows.Select(f => f.LastAssessed).OfType<DateTime>().ToList();

		return new FeedQualitySummary
		{
			TotalFeeds = rows.Count,
			TotalDatasets = Distinct(rows, f => f.DatasetUrl),
			TotalPublishers = Distinct(rows, f => f.PublisherName),

			RegularFeeds = rows.Count(f => f.IsRegular == true),
			IrregularFeeds = rows.Count(f => f.IsRegular == false),
			RegularityUnknown = rows.Count(f => f.IsRegular is null),

			FeedsOk = rows.Count(f => IsStatus(f, StatusOk)),
			FeedsWithWarnings = rows.Count(f => IsStatus(f, StatusWarning)),
			FeedsWithErrors = rows.Count(f => IsStatus(f, StatusError)),
			FeedsStatusUnknown = rows.Count(f => !IsStatus(f, StatusOk) && !IsStatus(f, StatusWarning) && !IsStatus(f, StatusError)),
			DatasetsWithErrors = Distinct(rows.Where(f => IsStatus(f, StatusError)), f => f.DatasetUrl),

			FeedsWithFutureData = rows.Count(f => f.NumFutureOpportunityItems > 0),
			DatasetsWithFutureData = Distinct(rows.Where(f => f.NumFutureOpportunityItems > 0), f => f.DatasetUrl),
			TotalFutureOpportunityItems = rows.Sum(f => f.NumFutureOpportunityItems ?? 0),

			FeedsScored = scores.Count,
			AverageScore = scores.Count > 0 ? scores.Average() : null,
			MedianScore = Median(scores),
			MinScore = scores.Count > 0 ? scores[0] : null,
			MaxScore = scores.Count > 0 ? scores[^1] : null,
			ScoreBuckets = ScoreBuckets(scores),

			Completeness = AverageCompleteness(rows),

			StatusBreakdown = Breakdown(rows, f => f.Status),
			GradeBreakdown = Breakdown(rows, f => f.Grade),
			FeedTypeBreakdown = Breakdown(rows, f => f.FeedType),
			FeedVersionBreakdown = Breakdown(rows, f => f.FeedVersion),

			OldestAssessment = assessed.Count > 0 ? assessed.Min() : null,
			NewestAssessment = assessed.Count > 0 ? assessed.Max() : null,
		};
	}

	#region Rules

	/// <summary>
	/// Whether a feed reports the given status, compared case-insensitively. The column is free text
	/// documented as <c>OK | WARNING | ERROR</c>, so a differently-cased value is the same status
	/// rather than an unknown one.
	/// </summary>
	private static bool IsStatus(FeedQuality feed, string status) =>
		string.Equals(feed.Status?.Trim(), status, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Distinct non-blank values of a key. Blanks are dropped rather than counted as one shared value:
	/// two feeds with no publisher name are not two feeds from the same publisher.
	/// </summary>
	private static int Distinct(IEnumerable<FeedQuality> rows, Func<FeedQuality, string?> key) =>
		rows
			.Select(key)
			.Where(v => !string.IsNullOrWhiteSpace(v))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Count();

	/// <summary>
	/// Middle value of an already-ascending list, averaging the two middle values for an even count.
	/// <c>null</c> when nothing was scored.
	/// </summary>
	private static double? Median(IReadOnlyList<double> ascending) => ascending.Count switch
	{
		0 => null,
		var n when n % 2 == 1 => ascending[n / 2],
		var n => (ascending[(n / 2) - 1] + ascending[n / 2]) / 2,
	};

	/// <summary>
	/// The fixed five score buckets, always all present so the dashboard can draw a histogram of a
	/// constant shape. Lower bound inclusive, upper exclusive, except the top bucket which includes
	/// 100. Unscored feeds are in no bucket, so the counts sum to <c>feeds_scored</c>.
	/// </summary>
	private static IReadOnlyList<FeedQualityScoreBucket> ScoreBuckets(IEnumerable<double> scores)
	{
		var counts = new int[ScoreBucketCount];

		foreach (var score in scores)
		{
			// Scores outside 0..100 would index off either end; clamping files them in the nearest
			// bucket rather than throwing, since a stored score is not ours to validate.
			var index = Math.Clamp((int)(score / ScoreBucketWidth), 0, ScoreBucketCount - 1);
			counts[index]++;
		}

		return [.. Enumerable.Range(0, ScoreBucketCount).Select(i => new FeedQualityScoreBucket
		{
			Lower = i * ScoreBucketWidth,
			Upper = (i + 1) * ScoreBucketWidth,
			FeedCount = counts[i],
		})];
	}

	/// <summary>
	/// Mean of each completeness column over the feeds that report it, with that denominator alongside.
	/// </summary>
	private static FeedQualityCompleteness AverageCompleteness(IReadOnlyList<FeedQuality> rows)
	{
		// Walked by position rather than by name: Columns is ordered, and reading it the same way for
		// every row keeps this from depending on nine string literals matching nine properties.
		var reported = FeedQualityCompletenessValues.None.Columns
			.Select(_ => new List<double>())
			.ToList();

		foreach (var feed in rows)
		{
			var index = 0;
			foreach (var (_, value) in feed.Completeness.Columns)
			{
				if (value is double measured)
				{
					reported[index].Add(measured);
				}
				index++;
			}
		}

		FeedQualityAverage Average(int index) => new()
		{
			Average = reported[index].Count > 0 ? reported[index].Average() : null,
			FeedsReporting = reported[index].Count,
		};

		return new FeedQualityCompleteness
		{
			Location = Average(0),
			StartDate = Average(1),
			EndDate = Average(2),
			Activities = Average(3),
			Facilities = Average(4),
			AgeRange = Average(5),
			Level = Average(6),
			AccessibilitySupport = Average(7),
			GenderRestriction = Average(8),
		};
	}

	/// <summary>
	/// Counts feeds and datasets per distinct value of a column, worst-populated last. A blank value
	/// becomes <see cref="UnknownValue"/> rather than being dropped, so the feed counts always sum to
	/// <c>total_feeds</c>.
	/// </summary>
	/// <remarks>
	/// Ordered by feed count descending and then by value ascending. The tiebreak is load-bearing: two
	/// equally common values must not swap places between requests, or a dashboard rendering the
	/// breakdown as a list would reorder itself for no reason.
	/// </remarks>
	private static IReadOnlyList<FeedQualityBreakdown> Breakdown(
		IReadOnlyList<FeedQuality> rows,
		Func<FeedQuality, string?> column)
	{
		if (rows.Count == 0)
		{
			return [];
		}

		return [.. rows
			.GroupBy(f => Label(column(f)), StringComparer.OrdinalIgnoreCase)
			.Select(g => new FeedQualityBreakdown
			{
				Value = g.Key,
				FeedCount = g.Count(),
				DatasetCount = Distinct(g, f => f.DatasetUrl),
				Share = (double)g.Count() / rows.Count,
			})
			.OrderByDescending(b => b.FeedCount)
			.ThenBy(b => b.Value, StringComparer.OrdinalIgnoreCase)];
	}

	private static string Label(string? value) =>
		string.IsNullOrWhiteSpace(value) ? UnknownValue : value.Trim();

	#endregion
}

/// <summary>
/// SQL and row parsing for <c>feed_quality</c>. Table names arrive already fully qualified by the
/// caller's <c>Fq</c>; every filter value goes through a <see cref="BigQueryParameter"/>.
/// </summary>
internal static class FeedQualityQuery
{
	/// <summary>
	/// The day the assessments describe: the latest <c>last_assessed</c> in the table. <c>null</c> when
	/// the table is empty.
	/// </summary>
	public static string SnapshotDateSql(string feedQualityTable) =>
		$"""
		SELECT MAX(DATE(last_assessed)) AS snapshot_date
		FROM {feedQualityTable}
		""";

	/// <summary>
	/// Every assessed feed, with its publisher joined in, narrowed by the supplied filters.
	/// </summary>
	/// <remarks>
	/// <c>feeds</c> is collapsed to one row per <c>dataset_url</c> before the join. It holds one row per
	/// feed, so joining it raw would fan a dataset's assessments out by its feed count. <c>MIN</c>
	/// rather than <c>ANY_VALUE</c> for the reason <see cref="DatasetMetadataQuery.DatasetMetadataSql"/>
	/// documents: where a dataset's feeds disagree on <c>publisher_name</c>, <c>ANY_VALUE</c> would pick
	/// a different one from one request to the next.
	///
	/// The join is a <c>LEFT JOIN</c>, so an assessed feed whose dataset has no <c>feeds</c> row is
	/// still returned, with no publisher.
	///
	/// Ordered best-scoring first, then by dataset and feed. The last two are not decoration: the order
	/// must be total, or two feeds on the same score could swap pages between requests and a caller
	/// paging through would see one twice and another never.
	/// </remarks>
	/// <param name="feedQualityTable">Fully qualified <c>feed_quality</c>.</param>
	/// <param name="feedsTable">Fully qualified <c>feeds</c>.</param>
	/// <param name="filterByDatasetUrl">Whether to restrict to <c>@dataset_urls</c>.</param>
	/// <param name="filterByPublisher">Whether to restrict to <c>@publishers</c>.</param>
	public static string FeedQualitySql(
		string feedQualityTable,
		string feedsTable,
		bool filterByDatasetUrl,
		bool filterByPublisher)
	{
		var conditions = new List<string>();

		if (filterByDatasetUrl)
		{
			conditions.Add("q.dataset_url IN UNNEST(@dataset_urls)");
		}

		if (filterByPublisher)
		{
			conditions.Add("p.publisher_name IN UNNEST(@publishers)");
		}

		var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

		return $"""
			WITH publishers AS (
			  SELECT dataset_url, MIN(publisher_name) AS publisher_name
			  FROM {feedsTable}
			  GROUP BY dataset_url
			)
			SELECT q.feed_id,
			       q.dataset_url,
			       q.dataset_name,
			       p.publisher_name,
			       q.feed_type,
			       q.feed_url,
			       q.is_regular,
			       q.status,
			       q.warnings,
			       q.errors,
			       q.missing_required_fields,
			       q.location_completeness,
			       q.start_date_completeness,
			       q.end_date_completeness,
			       q.activities_completeness,
			       q.facilities_completeness,
			       q.age_range_completeness,
			       q.level_completeness,
			       q.accessibility_support_completeness,
			       q.gender_restriction_completeness,
			       q.num_future_opportunity_items,
			       q.grade,
			       q.feed_version,
			       q.score,
			       q.last_assessed
			FROM {feedQualityTable} AS q
			LEFT JOIN publishers AS p USING (dataset_url)
			{where}
			ORDER BY q.score DESC NULLS LAST, q.dataset_url, q.feed_id
			""";
	}

	/// <summary>
	/// The filter values, as array parameters. Blank entries are dropped and the rest de-duplicated: a
	/// BigQuery <c>ARRAY</c> parameter cannot carry NULL elements. Only the parameters the SQL actually
	/// references are bound.
	/// </summary>
	public static IReadOnlyList<BigQueryParameter> FeedQualityParameters(
		IReadOnlyCollection<string> datasetUrls,
		IReadOnlyCollection<string> publishers)
	{
		var parameters = new List<BigQueryParameter>();

		if (datasetUrls.Count > 0)
		{
			parameters.Add(StringArray("dataset_urls", datasetUrls));
		}

		if (publishers.Count > 0)
		{
			parameters.Add(StringArray("publishers", publishers));
		}

		return parameters;
	}

	private static BigQueryParameter StringArray(string name, IEnumerable<string> values) =>
		new(name, BigQueryDbType.Array, values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal).ToList())
		{
			ArrayElementType = BigQueryDbType.String,
		};

	/// <summary>
	/// Reads one assessment. <c>feed_id</c> and <c>dataset_url</c> are cast directly because the column
	/// is <c>REQUIRED</c>; everything else goes through <see cref="BigQueryValueParser"/> and
	/// <c>GetValueOrDefault</c>, since a NULL column is absent from the row dictionary rather than
	/// present as null.
	/// </summary>
	public static FeedQuality ParseFeedQuality(Dictionary<string, object> row) =>
		new(
			(string)row["feed_id"],
			(string)row["dataset_url"],
			row.GetValueOrDefault("dataset_name") as string,
			row.GetValueOrDefault("publisher_name") as string,
			row.GetValueOrDefault("feed_type") as string,
			row.GetValueOrDefault("feed_url") as string,
			BigQueryValueParser.AsBool(row.GetValueOrDefault("is_regular")),
			row.GetValueOrDefault("status") as string,
			row.GetValueOrDefault("grade") as string,
			row.GetValueOrDefault("feed_version") as string,
			BigQueryValueParser.AsDouble(row.GetValueOrDefault("score")),
			BigQueryValueParser.AsLong(row.GetValueOrDefault("num_future_opportunity_items")),
			ParseAssessedAt(row.GetValueOrDefault("last_assessed")),
			new FeedQualityCompletenessValues(
				BigQueryValueParser.AsDouble(row.GetValueOrDefault("location_completeness")),
				BigQueryValueParser.AsDouble(row.GetValueOrDefault("start_date_completeness")),
				BigQueryValueParser.AsDouble(row.GetValueOrDefault("end_date_completeness")),
				BigQueryValueParser.AsDouble(row.GetValueOrDefault("activities_completeness")),
				BigQueryValueParser.AsDouble(row.GetValueOrDefault("facilities_completeness")),
				BigQueryValueParser.AsDouble(row.GetValueOrDefault("age_range_completeness")),
				BigQueryValueParser.AsDouble(row.GetValueOrDefault("level_completeness")),
				BigQueryValueParser.AsDouble(row.GetValueOrDefault("accessibility_support_completeness")),
				BigQueryValueParser.AsDouble(row.GetValueOrDefault("gender_restriction_completeness"))),
			BigQueryValueParser.ParseJson(row.GetValueOrDefault("warnings")),
			BigQueryValueParser.ParseJson(row.GetValueOrDefault("errors")),
			BigQueryValueParser.ParseJson(row.GetValueOrDefault("missing_required_fields")));

	/// <summary>
	/// Reads the snapshot date from <see cref="SnapshotDateSql"/>, or <c>null</c> for an empty table.
	/// </summary>
	public static DateOnly? ParseSnapshotDate(Dictionary<string, object>? row) =>
		row?.GetValueOrDefault("snapshot_date") is DateTime snapshot
			? DateOnly.FromDateTime(snapshot)
			: null;

	/// <summary>
	/// A BigQuery <c>TIMESTAMP</c> is an instant, but the client hands it back unkinded. Stamping it UTC
	/// keeps the serialised value from being read as local time by whoever consumes it.
	/// </summary>
	private static DateTime? ParseAssessedAt(object? value) =>
		value is DateTime assessed ? DateTime.SpecifyKind(assessed, DateTimeKind.Utc) : null;
}
