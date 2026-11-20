using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MonitorApi.Services.Admin;

/// <summary>The concept a published label resolved to, and how confidently.</summary>
/// <param name="Concept">The best-scoring concept.</param>
/// <param name="Score">Similarity in <c>0..1</c>; see <see cref="ActivityLabelMatcher"/> for the scale.</param>
public sealed record ActivityLabelMatch(ActivityConcept Concept, double Score);

/// <summary>
/// Matches free-text activity labels, as publishers write them, to concepts in the OpenActive activity
/// list.
/// </summary>
/// <remarks>
/// Both sides are normalised first (<see cref="Normalise"/>). Each concept is then scored against the
/// label, and the best one is accepted when its score reaches the caller's threshold:
///
/// - <b>1.0</b>: the normalised strings are equal, or equal once spaces are removed
///   (<c>body pump</c> = <c>Bodypump™</c>, <c>zumba</c> = <c>Zumba®</c>, <c>crèche</c> = <c>Creche</c>).
/// - <b><see cref="ContainmentScore"/> (0.9)</b>: every word of the concept appears in the label, in any
///   order (<c>lane swimming</c> → Swimming, <c>pilates - reformer - beginner</c> → Reformer Pilates).
///   Concepts in <see cref="AmbiguousConcepts"/> are excluded from this rule only.
/// - <b>Dice coefficient</b> of character bigrams, the better of the label as written and with its
///   words sorted. This catches typos and reordering (<c>mind, body and soul</c> → Body Mind and Soul).
///
/// The highest score wins. Ties go to the concept with more words (the most specific), then the longer
/// label, then the earlier concept, so the result never depends on dictionary order.
///
/// <see cref="DefaultMinSimilarity"/> is 0.85. Containment and strong fuzzy matches pass. When this was
/// prototyped against live labels, the Dice false matches all scored 0.80–0.83 (<c>swimming sessions</c>
/// → Swimming Lessons, <c>dance fit</c> → Popdance Fit), so they fall below it. Both of those labels
/// resolve correctly through containment instead.
///
/// Thread-safe. The best candidate for each normalised label is memoised whatever the threshold, so
/// one instance serves every request for as long as its vocabulary is cached.
/// </remarks>
public sealed partial class ActivityLabelMatcher
{
	/// <summary>Threshold used when the caller does not supply one.</summary>
	public const double DefaultMinSimilarity = 0.85;

	/// <summary>
	/// Score given to a label that contains every word of a concept. Fixed below an exact match, so an
	/// exact match on a different concept always wins, and above the default threshold, so containment
	/// is accepted unless the caller asks for exact matches only.
	/// </summary>
	public const double ContainmentScore = 0.9;

	/// <summary>
	/// Normalised single-word concepts that are also everyday words in session names, and so would
	/// match far too much by containment. <c>learner pool</c> is a swimming pool, not the cue sport
	/// Pool. A label that <em>is</em> one of these words still matches it exactly.
	/// </summary>
	public static readonly IReadOnlySet<string> AmbiguousConcepts = new HashSet<string>(StringComparer.Ordinal)
	{
		"pool", "air", "bounce", "burn", "freestyle", "racing", "commercial", "modern", "urban",
		"house", "funk", "latin", "jazz", "tap", "swing", "extend", "nova", "fierce", "eton",
	};

	private readonly List<PreparedConcept> concepts;
	private readonly ConcurrentDictionary<string, ActivityLabelMatch?> memo = new(StringComparer.Ordinal);

	/// <summary>Prepares the concepts for matching.</summary>
	public ActivityLabelMatcher(IEnumerable<ActivityConcept> concepts)
	{
		this.concepts = concepts
			.Select(c => new PreparedConcept(c, Normalise(c.PrefLabel)))
			.Where(c => c.Normalised.Length > 0)
			.ToList();
	}

	/// <summary>
	/// The concept a label refers to, or <c>null</c> when no concept reaches
	/// <paramref name="minSimilarity"/>.
	/// </summary>
	/// <param name="label">The label as published.</param>
	/// <param name="minSimilarity">Lowest acceptable score, <c>0..1</c>. <c>1</c> accepts exact matches only.</param>
	public ActivityLabelMatch? Match(string label, double minSimilarity = DefaultMinSimilarity)
	{
		var normalised = Normalise(label);
		if (normalised.Length == 0)
		{
			return null;
		}

		var best = memo.GetOrAdd(normalised, FindBest);
		return best is not null && best.Score >= minSimilarity ? best : null;
	}

	/// <summary>
	/// Folds a label to a comparable form: lowercase, accents and ™/® removed, <c>&amp;</c> read as
	/// <c>and</c>, bracketed asides such as <c>(8-15yrs)</c> dropped, and every run of anything other
	/// than a letter or digit collapsed to a single space.
	/// </summary>
	public static string Normalise(string label)
	{
		if (string.IsNullOrWhiteSpace(label))
		{
			return "";
		}

		var text = label.Replace("™", " ").Replace("®", " ").Replace("&", " and ");
		text = Parenthetical().Replace(text, " ");

		var decomposed = text.Normalize(NormalizationForm.FormKD);
		var builder = new StringBuilder(decomposed.Length);
		foreach (var c in decomposed)
		{
			if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
			{
				builder.Append(char.ToLowerInvariant(c));
			}
		}

		return NonAlphanumeric().Replace(builder.ToString(), " ").Trim();
	}

	/// <summary>
	/// Sørensen–Dice coefficient over character bigrams, ignoring spaces: twice the shared bigrams
	/// divided by the bigrams in both. <c>1</c> for identical strings, <c>0</c> for nothing in common.
	/// </summary>
	public static double Dice(string a, string b)
	{
		var left = Bigrams(a);
		var right = Bigrams(b);
		if (left.Count == 0 || right.Count == 0)
		{
			return 0;
		}

		var counts = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var bigram in left)
		{
			counts[bigram] = counts.GetValueOrDefault(bigram) + 1;
		}

		var shared = 0;
		foreach (var bigram in right)
		{
			if (counts.GetValueOrDefault(bigram) > 0)
			{
				counts[bigram]--;
				shared++;
			}
		}

		return 2.0 * shared / (left.Count + right.Count);
	}

	private ActivityLabelMatch? FindBest(string normalised)
	{
		var compact = normalised.Replace(" ", "");
		var tokens = normalised.Split(' ');
		var tokenSet = new HashSet<string>(tokens, StringComparer.Ordinal);
		var sorted = string.Join(' ', tokens.Order(StringComparer.Ordinal));

		PreparedConcept? best = null;
		var bestScore = 0.0;

		foreach (var concept in concepts)
		{
			var score = Score(concept, normalised, compact, tokenSet, sorted);
			if (score > 0 && (best is null || IsBetter(score, concept, bestScore, best)))
			{
				best = concept;
				bestScore = score;
			}
		}

		return best is null ? null : new ActivityLabelMatch(best.Concept, Math.Round(bestScore, 4));
	}

	private static double Score(PreparedConcept concept, string normalised, string compact, HashSet<string> tokenSet, string sorted)
	{
		if (concept.Normalised == normalised || concept.Compact == compact)
		{
			return 1.0;
		}

		var score = Math.Max(Dice(normalised, concept.Normalised), Dice(sorted, concept.Sorted));

		if (!AmbiguousConcepts.Contains(concept.Normalised) && concept.Tokens.All(tokenSet.Contains))
		{
			score = Math.Max(score, ContainmentScore);
		}

		return score;
	}

	private static bool IsBetter(double score, PreparedConcept concept, double bestScore, PreparedConcept best)
	{
		// Scores are compared with a small tolerance so floating-point noise between two equal Dice
		// values cannot decide the winner; the explicit tie-breaks below do.
		const double epsilon = 1e-9;
		if (score > bestScore + epsilon)
		{
			return true;
		}
		if (score < bestScore - epsilon)
		{
			return false;
		}
		if (concept.Tokens.Length != best.Tokens.Length)
		{
			return concept.Tokens.Length > best.Tokens.Length;
		}
		return concept.Normalised.Length > best.Normalised.Length;
	}

	private static List<string> Bigrams(string text)
	{
		var compact = text.Replace(" ", "");
		var bigrams = new List<string>(Math.Max(compact.Length - 1, 1));
		if (compact.Length == 1)
		{
			bigrams.Add(compact);
		}
		for (var i = 0; i < compact.Length - 1; i++)
		{
			bigrams.Add(compact.Substring(i, 2));
		}
		return bigrams;
	}

	[GeneratedRegex(@"\([^)]*\)")]
	private static partial Regex Parenthetical();

	[GeneratedRegex(@"[^a-z0-9]+")]
	private static partial Regex NonAlphanumeric();

	private sealed class PreparedConcept
	{
		public PreparedConcept(ActivityConcept concept, string normalised)
		{
			Concept = concept;
			Normalised = normalised;
			Compact = normalised.Replace(" ", "");
			Tokens = normalised.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			Sorted = string.Join(' ', Tokens.Order(StringComparer.Ordinal));
		}

		public ActivityConcept Concept { get; }
		public string Normalised { get; }
		public string Compact { get; }
		public string[] Tokens { get; }
		public string Sorted { get; }
	}
}
