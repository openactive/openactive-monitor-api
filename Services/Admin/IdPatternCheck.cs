using Google.Cloud.BigQuery.V2;
using System.Text.RegularExpressions;

namespace MonitorApi.Services.Admin;

/// <summary>
/// Evidence that orphaned children point at parents in a different id format from the parents the
/// dataset publishes — the decision tree's "ID-space change".
/// </summary>
public sealed record IdPatternMismatch(
    IReadOnlyList<string> ReferencedPatterns,
    IReadOnlyList<string> ParentPatterns,
    IReadOnlyList<string> ExampleReferencedIds,
    IReadOnlyList<string> ExampleParentIds);

/// <summary>
/// Pure check for an id-format change between parent and child feeds. Ids are reduced to a shape
/// (lower-cased, UUIDs → <c>{uuid}</c>, digit runs → <c>{n}</c>); if no referenced shape matches any
/// parent shape, the publisher has changed id scheme.
/// </summary>
public static class IdPatternCheck
{
    private static readonly Regex Uuid = new(
        "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    
	/// <summary>The parent kind each monitored child kind references.</summary>
	public static readonly IReadOnlyDictionary<string, string> ParentKinds =
		new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["Slot"] = "FacilityUse",
			["ScheduledSession"] = "SessionSeries",
		};

	/// <summary>The kind a <paramref name="childKind"/> child points at, or <c>null</c> if unmonitored.</summary>
	public static string? ParentKindFor(string childKind) =>
		ParentKinds.TryGetValue(childKind, out var parent) ? parent : null;

    private static readonly Regex Digits = new("[0-9]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The shape of one id.</summary>
    public static string Pattern(string id)
    {
        var withoutUuids = Uuid.Replace(id.Trim().ToLowerInvariant(), "{uuid}");
        return Digits.Replace(withoutUuids, "{n}");
    }

    /// <summary>The mismatch, or <c>null</c> when the shapes overlap or either side is empty.</summary>
    public static IdPatternMismatch? Compare(IEnumerable<string> referencedIds, IEnumerable<string> parentIds)
    {
        var referenced = Usable(referencedIds);
        var parents = Usable(parentIds);

        if (referenced.Count == 0 || parents.Count == 0)
        {
            return null;
        }

        var referencedPatterns = Patterns(referenced);
        var parentPatterns = Patterns(parents);

        return referencedPatterns.Intersect(parentPatterns, StringComparer.Ordinal).Any()
            ? null
            : new IdPatternMismatch(referencedPatterns, parentPatterns, referenced, parents);
    }

    private static List<string> Usable(IEnumerable<string> ids) =>
        ids.Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static List<string> Patterns(IEnumerable<string> ids) =>
        ids.Select(Pattern)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
}


/// <summary>A sample of the parent ids one dataset publishes for one parent kind.</summary>
public sealed record ParentIdSample(string DatasetUrl, string Kind, IReadOnlyList<string> SampleIds);

/// <summary>
/// SQL and row parsing for the parent side of <see cref="IdPatternCheck"/> — a sample of the ids a
/// dataset actually publishes, to compare against the ids its orphaned children reference.
/// </summary>
/// <remarks>
/// The referenced side needs no query of its own: it arrives on the orphan incident as its
/// <see cref="MissingParent"/> sample. Table names are passed in already fully qualified by the
/// caller's <c>Fq</c>.
/// </remarks>
internal static class ParentIdSampleQuery
{
	/// <summary>Ids sampled per dataset and kind. Enough to show every scheme in use, few enough to be cheap.</summary>
	public const int SampleSize = 50;

	/// <summary>
	/// A sample of parent ids per <c>(dataset_url, kind)</c>, for the requested datasets only.
	/// </summary>
	public static string ParentIdSampleSql(string opportunitiesTable) =>
		$"""
		WITH parent_ids AS (
		  SELECT DISTINCT dataset_url, kind, data_id
		  FROM {opportunitiesTable}
		  WHERE dataset_url IN UNNEST(@dataset_urls)
		        AND kind IN UNNEST(@parent_kinds)
		        AND data_id IS NOT NULL
		)
		SELECT dataset_url,
		       kind,
		       ARRAY_AGG(data_id ORDER BY FARM_FINGERPRINT(data_id) LIMIT {SampleSize}) AS sample_ids
		FROM parent_ids
		GROUP BY dataset_url, kind
		""";

	/// <summary>
	/// The datasets to sample, and the parent kinds worth sampling. Blank dataset urls are dropped and
	/// the rest de-duplicated: a BigQuery <c>ARRAY</c> parameter cannot carry NULL elements.
	/// </summary>
	public static IReadOnlyList<BigQueryParameter> ParentIdSampleParameters(IReadOnlyCollection<string> datasetUrls) =>
	[
		new BigQueryParameter(
			"dataset_urls",
			BigQueryDbType.Array,
			datasetUrls
				.Where(url => !string.IsNullOrWhiteSpace(url))
				.Distinct(StringComparer.Ordinal)
				.ToList())
		{
			ArrayElementType = BigQueryDbType.String,
		},
		new BigQueryParameter(
			"parent_kinds",
			BigQueryDbType.Array,
			IdPatternCheck.ParentKinds.Values.Distinct(StringComparer.Ordinal).ToList())
		{
			ArrayElementType = BigQueryDbType.String,
		},
	];

	/// <summary>
	/// Reads one dataset-and-kind row. <c>dataset_url</c> and <c>kind</c> are cast directly because the
	/// query groups by them and filters them non-NULL.
	/// </summary>
	public static ParentIdSample ParseParentIdSample(Dictionary<string, object> row) =>
		new(
			(string)row["dataset_url"],
			(string)row["kind"],
			ParseIds(row.GetValueOrDefault("sample_ids")));

	/// <summary>
	/// Parses the repeated <c>STRING</c> sample. The BigQuery client surfaces a repeated column as an
	/// enumerable of boxed values, and an empty array is absent from the row dictionary rather than
	/// present as an empty one.
	/// </summary>
	private static IReadOnlyList<string> ParseIds(object? cell)
	{
		var ids = new List<string>();

		// A string is itself IEnumerable, so it has to be excluded explicitly.
		if (cell is not System.Collections.IEnumerable items || cell is string)
		{
			return ids;
		}

		foreach (var item in items)
		{
			if (item is string id && !string.IsNullOrWhiteSpace(id))
			{
				ids.Add(id.Trim());
			}
		}

		return ids;
	}
}