namespace MonitorApi.Services.Admin;

/// <summary>
/// One activity row of <c>active_opportunities_summary</c>: a group of future opportunities that share
/// a district and a set of published activity labels.
/// </summary>
/// <param name="DistrictCode">ONS local authority district code.</param>
/// <param name="DistrictName">District name.</param>
/// <param name="RegionCode">ONS region code; <c>null</c> outside England.</param>
/// <param name="RegionName">Region name; <c>null</c> outside England.</param>
/// <param name="CountryCode">ONS country code.</param>
/// <param name="CountryName">Country name.</param>
/// <param name="OpportunityCount">Future opportunities in the group.</param>
/// <param name="Labels">The group's distinct activity labels, as published.</param>
public sealed record ActivityCoverageSourceRow(
	string DistrictCode,
	string? DistrictName,
	string? RegionCode,
	string? RegionName,
	string? CountryCode,
	string? CountryName,
	long OpportunityCount,
	IReadOnlyList<string> Labels);

/// <summary>Future opportunities for one vocabulary concept in one district.</summary>
public sealed record ActivityCoverageEntry(
	string DistrictCode,
	string? DistrictName,
	string? RegionCode,
	string? RegionName,
	string? CountryCode,
	string? CountryName,
	ActivityConcept Activity,
	long OpportunityCount);

/// <summary>National headline figures for activity data. See <see cref="ActivityCoverage.Summarise"/>.</summary>
/// <param name="ActivityOpportunities">Future activity opportunities, labelled or not.</param>
/// <param name="LabelledOpportunities">Those publishing at least one activity label.</param>
/// <param name="LinkedOpportunities">Those with at least one label resolved to a vocabulary concept.</param>
/// <param name="ActivitiesOffered">Distinct vocabulary concepts with at least one opportunity.</param>
/// <param name="VocabularySize">Concepts in the activity list.</param>
public sealed record ActivityCoverageStats(
	long ActivityOpportunities,
	long LabelledOpportunities,
	long LinkedOpportunities,
	int ActivitiesOffered,
	int VocabularySize)
{
	/// <summary><paramref name="part"/> as a percentage of <see cref="ActivityOpportunities"/>, to one decimal place; <c>null</c> when there are none.</summary>
	/// <param name="part">The numerator.</param>
	public double? PercentageOfTotal(long part) =>
		ActivityOpportunities > 0
			? Math.Round(100.0 * part / ActivityOpportunities, 1, MidpointRounding.AwayFromZero)
			: null;
}

/// <summary>One concept's place in the national ranking. See <see cref="ActivityCoverage.Rank"/>.</summary>
/// <param name="Rank">1-based position in the ranking.</param>
/// <param name="Activity">The vocabulary concept.</param>
/// <param name="OpportunityCount">National future opportunities for the concept.</param>
/// <param name="DistrictCount">Districts with at least one of them.</param>
public sealed record ActivityRankEntry(int Rank, ActivityConcept Activity, long OpportunityCount, int DistrictCount);

/// <summary>
/// Turns summary rows into per-district, per-concept opportunity counts. Pure: no BigQuery or
/// ASP.NET types, so every rule here is tested from hand-written rows.
/// </summary>
public static class ActivityCoverage
{
	/// <summary>
	/// Resolves each row's labels to vocabulary concepts and sums the opportunities for each
	/// (district, concept), ordered by concept label, then count (largest first), then district code.
	/// </summary>
	/// <remarks>
	/// <list type="bullet">
	/// <item>A row counts <b>once per distinct concept</b>. A group labelled
	/// <c>["Swimming", "Lane Swimming"]</c> adds its opportunities to Swimming once, not twice, because
	/// both labels resolve to the same concept.</item>
	/// <item>A row counts once for <b>each</b> distinct concept its labels reach, so the total across
	/// concepts can exceed the number of distinct opportunities.</item>
	/// <item>Labels that reach no concept at <paramref name="minSimilarity"/> are dropped. A row whose
	/// labels all miss contributes nothing.</item>
	/// <item>District, region and country names are taken from the first row seen for a district. The
	/// summary repeats them on every row, so they agree.</item>
	/// </list>
	/// </remarks>
	/// <param name="rows">Summary activity rows, already location-filtered.</param>
	/// <param name="vocabulary">The activity list to resolve labels against.</param>
	/// <param name="minSimilarity">Match threshold passed to <see cref="ActivityLabelMatcher.Match"/>.</param>
	/// <param name="activityFilter">
	/// Concepts to keep, each named by <c>@id</c> or <c>prefLabel</c> (ignoring case). Empty keeps
	/// every concept. A filter whose values name no concept returns nothing rather than everything.
	/// </param>
	public static List<ActivityCoverageEntry> Build(
		IEnumerable<ActivityCoverageSourceRow> rows,
		ActivityVocabulary vocabulary,
		double minSimilarity,
		IReadOnlyCollection<string> activityFilter)
	{
		HashSet<string>? wanted = null;
		if (activityFilter.Count > 0)
		{
			wanted = activityFilter
				.Select(vocabulary.Find)
				.OfType<ActivityConcept>()
				.Select(c => c.Id)
				.ToHashSet(StringComparer.Ordinal);

			if (wanted.Count == 0)
			{
				return [];
			}
		}

		var totals = new Dictionary<(string District, string ConceptId), Accumulator>();

		foreach (var row in rows)
		{
			foreach (var concept in Concepts(row, vocabulary, minSimilarity))
			{
				if (wanted is not null && !wanted.Contains(concept.Id))
				{
					continue;
				}

				var key = (row.DistrictCode, concept.Id);
				if (!totals.TryGetValue(key, out var accumulator))
				{
					accumulator = new Accumulator(row, concept);
					totals[key] = accumulator;
				}
				accumulator.Count += row.OpportunityCount;
			}
		}

		return totals.Values
			.Where(a => a.Count > 0)
			.Select(a => new ActivityCoverageEntry(
				a.Row.DistrictCode,
				a.Row.DistrictName,
				a.Row.RegionCode,
				a.Row.RegionName,
				a.Row.CountryCode,
				a.Row.CountryName,
				a.Concept,
				a.Count))
			.OrderBy(e => e.Activity.PrefLabel, StringComparer.Ordinal)
			.ThenByDescending(e => e.OpportunityCount)
			.ThenBy(e => e.DistrictCode, StringComparer.Ordinal)
			.ToList();
	}

	/// <summary>
	/// National headline figures: how many future activity opportunities there are, how many publish an
	/// activity label, how many reach the vocabulary, and how many distinct concepts are on offer.
	/// </summary>
	/// <remarks>
	/// <list type="bullet">
	/// <item>Every row adds to <see cref="ActivityCoverageStats.ActivityOpportunities"/>, labelled or not.</item>
	/// <item>A row adds to <see cref="ActivityCoverageStats.LinkedOpportunities"/> <b>once</b> when any of
	/// its labels resolves to a concept, however many concepts it reaches. The summary groups
	/// opportunities by their label set, so this counts distinct opportunities, unlike the per-concept
	/// sums in <see cref="Build"/> and <see cref="Rank"/>.</item>
	/// <item>A concept counts towards <see cref="ActivityCoverageStats.ActivitiesOffered"/> when it is
	/// reached by a row with at least one opportunity.</item>
	/// </list>
	/// </remarks>
	/// <param name="rows">Summary activity rows, including unlabelled ones (empty <c>Labels</c>).</param>
	/// <param name="vocabulary">The activity list to resolve labels against.</param>
	/// <param name="minSimilarity">Match threshold passed to <see cref="ActivityLabelMatcher.Match"/>.</param>
	public static ActivityCoverageStats Summarise(
		IEnumerable<ActivityCoverageSourceRow> rows,
		ActivityVocabulary vocabulary,
		double minSimilarity)
	{
		long total = 0, labelled = 0, linked = 0;
		var offered = new HashSet<string>(StringComparer.Ordinal);

		foreach (var row in rows)
		{
			total += row.OpportunityCount;
			if (row.Labels.Count == 0)
			{
				continue;
			}

			labelled += row.OpportunityCount;

			var concepts = Concepts(row, vocabulary, minSimilarity).ToList();
			if (concepts.Count == 0)
			{
				continue;
			}

			linked += row.OpportunityCount;
			if (row.OpportunityCount > 0)
			{
				offered.UnionWith(concepts.Select(c => c.Id));
			}
		}

		return new ActivityCoverageStats(total, labelled, linked, offered.Count, vocabulary.Concepts.Count);
	}

	/// <summary>
	/// Every vocabulary concept with at least one future opportunity, ranked nationally by opportunity
	/// count (largest first), then by concept label.
	/// </summary>
	/// <remarks>
	/// Counts follow the same rule as <see cref="Build"/>, once per row per distinct concept, so a
	/// concept's national count equals the sum of its per-district counts there.
	/// <see cref="ActivityRankEntry.Rank"/> is the 1-based position in this order; ties in count take
	/// consecutive ranks, broken by label.
	/// </remarks>
	/// <param name="rows">Summary activity rows.</param>
	/// <param name="vocabulary">The activity list to resolve labels against.</param>
	/// <param name="minSimilarity">Match threshold passed to <see cref="ActivityLabelMatcher.Match"/>.</param>
	public static List<ActivityRankEntry> Rank(
		IEnumerable<ActivityCoverageSourceRow> rows,
		ActivityVocabulary vocabulary,
		double minSimilarity)
	{
		var totals = new Dictionary<string, (ActivityConcept Concept, long Count, HashSet<string> Districts)>(StringComparer.Ordinal);

		foreach (var row in rows)
		{
			if (row.OpportunityCount <= 0)
			{
				continue;
			}

			foreach (var concept in Concepts(row, vocabulary, minSimilarity))
			{
				if (!totals.TryGetValue(concept.Id, out var total))
				{
					total = (concept, 0, new HashSet<string>(StringComparer.Ordinal));
				}
				total.Districts.Add(row.DistrictCode);
				totals[concept.Id] = (concept, total.Count + row.OpportunityCount, total.Districts);
			}
		}

		return totals.Values
			.OrderByDescending(t => t.Count)
			.ThenBy(t => t.Concept.PrefLabel, StringComparer.Ordinal)
			.Select((t, i) => new ActivityRankEntry(i + 1, t.Concept, t.Count, t.Districts.Count))
			.ToList();
	}

	/// <summary>The distinct concepts a row's labels resolve to at <paramref name="minSimilarity"/>.</summary>
	private static IEnumerable<ActivityConcept> Concepts(
		ActivityCoverageSourceRow row,
		ActivityVocabulary vocabulary,
		double minSimilarity) =>
		row.Labels
			.Select(label => vocabulary.Matcher.Match(label, minSimilarity)?.Concept)
			.OfType<ActivityConcept>()
			.DistinctBy(c => c.Id);

	private sealed class Accumulator(ActivityCoverageSourceRow row, ActivityConcept concept)
	{
		public ActivityCoverageSourceRow Row { get; } = row;
		public ActivityConcept Concept { get; } = concept;
		public long Count { get; set; }
	}
}
