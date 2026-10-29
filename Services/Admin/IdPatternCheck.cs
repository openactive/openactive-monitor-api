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