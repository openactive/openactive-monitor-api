using Google.Cloud.BigQuery.V2;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MonitorApi.Models.Admin;
using MonitorApi.Services.Admin;

namespace MonitorApi.Controllers.Admin;

/// <summary>
/// Activity coverage for the admin dashboard: how many future opportunities each local authority
/// district offers for each OpenActive activity (the map), plus national headline figures and the top
/// activities. Every endpoint requires the admin token as the <c>token</c> query parameter.
/// </summary>
/// <remarks>
/// Reads <c>active_opportunities_summary</c>, which the pipeline rebuilds each night and limits to
/// future opportunities. It also mirrors the OpenActive activity list over HTTP, the one input that
/// is not in BigQuery (see <see cref="ActivityListOptions"/>).
///
/// Derives from <see cref="MonitorControllerBase"/> only for <c>ResolveSnapshotDate</c>, so
/// <c>meta.snapshot_date</c> means the same thing here as on every monitor: the latest ingestion day,
/// which is the run that rebuilt the summary.
/// </remarks>
public class ActivityCoverageController(
	IOptions<BigQueryOptions> bigQueryOptions,
	IOptions<ApiOptions> apiOptions,
	ActivityVocabularySource vocabularySource)
	: MonitorControllerBase(bigQueryOptions, apiOptions)
{
	private readonly ActivityVocabularySource vocabularySource = vocabularySource;

	/// <summary>
	/// Activity Coverage
	/// </summary>
	/// <remarks>
	/// Future opportunities per local authority district per activity, for a coverage map. One row per
	/// (district, activity) with at least one opportunity. Ordered by activity, then
	/// <c>opportunity_count</c> (largest first), then <c>district_code</c>.
	///
	/// **Only activities from the OpenActive activity list are reported**
	/// (<c>https://openactive.io/activity-list/activity-list.jsonld</c>). Publishers write activity
	/// labels as free text, so each label is matched to a concept and reported under the concept's
	/// canonical label and <c>@id</c>. Several published labels can land in one row.
	///
	/// Matching normalises both sides first: case, accents, ™ and ®, <c>&amp;</c> versus "and",
	/// bracketed asides such as "(8-15yrs)", and punctuation are all ignored. Each concept is then
	/// scored against the label:
	///
	/// - **1.0** when they are equal, or equal ignoring spaces ("body pump" → Bodypump™).
	/// - **0.9** when every word of the concept appears in the label ("lane swimming" → Swimming,
	///	  "pilates - reformer - beginner" → Reformer Pilates). When several concepts qualify, the one
	///	  with the most words wins. A few single-word concepts that are also everyday words (Pool, Air,
	///	  Bounce, Latin, Jazz, …) are excluded from this rule, so "learner pool" is not counted as the
	///	  cue sport. A label that *is* one of those words still matches it.
	/// - **Text similarity** (Dice coefficient over letter pairs) for typos and reordered words
	///	  ("mind, body and soul" → Body Mind and Soul).
	///
	/// The best-scoring concept is accepted when its score reaches <c>min_similarity</c>.
	///
	/// Other rules worth knowing:
	///
	/// - **An opportunity counts once per activity.** A group labelled "Swimming" and "Lane Swimming"
	///	  adds to Swimming once. One labelled "Swimming" and "Aqua Fit" adds to both, so the sum across
	///	  activities is more than the number of distinct opportunities.
	/// - **Free text that matches nothing is dropped.** Labels such as "Gym Session" and "Fitness
	///	  Classes" have no concept in the list.
	/// - **Opportunities with no activity label are not counted.** Many sessions inherit their activity
	///	  from a parent event, and the pipeline does not yet resolve every parent. Treat counts as a lower
	///	  bound, and a low count as "little published data", not necessarily "little provision".
	/// - **"Future" is fixed when the pipeline builds the summary** each night (start date on or after
	///	  that day, UTC), not when you call this endpoint.
	/// - **Activities only.** Facility bookings (Slot, FacilityUse) are excluded.
	/// - **Rows are sparse.** A district with no opportunities for an activity has no row. Fill the gaps
	///	  against your own district list.
	///
	/// There is no <c>as_of</c> and no trend: the summary holds current state only.
	///
	/// Answers <c>502</c> when the activity list cannot be fetched or read. Successful results are
	/// cached until the next daily refresh, varying by all query parameters.
	/// </remarks>
	/// <param name="page">One-based page number. Default <c>1</c>.</param>
	/// <param name="page_size">Rows per page. Default <c>500</c>, capped at <c>1000</c>.</param>
	/// <param name="activity">Activities to return, each as the concept's label (any case) or <c>@id</c>. Repeat the parameter or separate values with commas; values are OR'd. A value that names no concept matches nothing.</param>
	/// <param name="district">ONS local authority district codes. Repeat or comma-separate.</param>
	/// <param name="region">ONS region codes. Repeat or comma-separate.</param>
	/// <param name="country">ONS country codes. Repeat or comma-separate. <c>district</c>, <c>region</c> and <c>country</c> are OR'd together as one location filter.</param>
	/// <param name="min_similarity">Lowest match score accepted, <c>0</c> to <c>1</c>. Default <c>0.85</c>. <c>1</c> accepts exact matches only (after normalisation). Lower values match more labels but risk wrong matches; below <c>0.85</c> they start to appear. Values outside <c>0..1</c> are clamped.</param>
	[HttpGet("activity-coverage")]
	[ProducesResponseType(typeof(AdminPage<ActivityCoverageRow>), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status502BadGateway)]
	public async Task<ActionResult<AdminPage<ActivityCoverageRow>>> ActivityCoverage(
		int page = 1,
		int page_size = DefaultPageSize,
		[FromQuery] string[]? activity = null,
		[FromQuery] string[]? district = null,
		[FromQuery] string[]? region = null,
		[FromQuery] string[]? country = null,
		double min_similarity = ActivityLabelMatcher.DefaultMinSimilarity)
	{
		var activities = NormaliseMultiValue(activity);
		var districts = NormaliseMultiValue(district);
		var regions = NormaliseMultiValue(region);
		var countries = NormaliseMultiValue(country);
		var threshold = Threshold(min_similarity);

		var vocabulary = await LoadVocabulary();
		if (vocabulary.Failure is not null)
		{
			return vocabulary.Failure;
		}

		var snapshotDate = await SnapshotDate();

		var sourceRows = await LoadRows(
			ActivityCoverageQuery.Sql(
				Fq(Tables.ActiveOpportunitiesSummary),
				filterDistrict: districts.Count > 0,
				filterRegion: regions.Count > 0,
				filterCountry: countries.Count > 0),
			ActivityCoverageQuery.Parameters(districts, regions, countries));

		var coverage = Services.Admin.ActivityCoverage
			.Build(sourceRows, vocabulary.Value!, threshold, activities)
			.Select(ToRow)
			.ToList();

		return Ok(Paginate(coverage, page, page_size, snapshotDate));
	}

	/// <summary>
	/// Activity Coverage Summary
	/// </summary>
	/// <remarks>
	/// National headline figures for the activity coverage dashboard, as one object:
	///
	/// - **`activity_opportunities`**: future activity opportunities in total, with or without an
	///	  activity label. Facility bookings (Slot, FacilityUse) are excluded.
	/// - **`labelled_percentage`**: the share that publishes at least one activity label.
	/// - **`linked_percentage`**: the share with at least one label matched to the OpenActive activity
	///	  list at <c>min_similarity</c>, using the same matching as <c>/admin/activity-coverage</c>.
	/// - **`activities_offered`**: distinct activity-list concepts with at least one future opportunity,
	///	  out of <c>vocabulary_size</c>.
	///
	/// Both percentages are of <c>activity_opportunities</c>, so <c>linked_percentage</c> is never above
	/// <c>labelled_percentage</c>. They are rounded to one decimal place, and <c>null</c> when there are
	/// no activity opportunities. The matching counts behind them are returned alongside.
	///
	/// **Each opportunity counts once**, however many activities it carries. That is different from
	/// <c>/admin/activity-coverage</c> and <c>/admin/activity-coverage/top-activities</c>, which count an
	/// opportunity under each of its activities, so their totals are larger than
	/// <c>linked_opportunities</c>.
	///
	/// Many sessions take their activity from a parent event that the pipeline does not yet resolve, so
	/// <c>labelled_percentage</c> understates what publishers actually describe. "Future" is fixed when
	/// the pipeline rebuilds the summary each night.
	///
	/// Answers <c>502</c> when the activity list cannot be fetched or read. Successful results are cached
	/// until the next daily refresh, varying by all query parameters.
	/// </remarks>
	/// <param name="min_similarity">Lowest label match score accepted, <c>0</c> to <c>1</c>. Default <c>0.85</c>; <c>1</c> accepts exact matches only (after normalisation). Affects <c>linked_*</c> and <c>activities_offered</c> only. Values outside <c>0..1</c> are clamped.</param>
	[HttpGet("activity-coverage/summary")]
	[ProducesResponseType(typeof(AdminDocument<ActivityCoverageSummary>), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status502BadGateway)]
	public async Task<ActionResult<AdminDocument<ActivityCoverageSummary>>> ActivityCoverageSummary(
		double min_similarity = ActivityLabelMatcher.DefaultMinSimilarity)
	{
		var threshold = Threshold(min_similarity);

		var vocabulary = await LoadVocabulary();
		if (vocabulary.Failure is not null)
		{
			return vocabulary.Failure;
		}

		var snapshotDate = await SnapshotDate();

		var sourceRows = await LoadRows(
			ActivityCoverageQuery.Sql(
				Fq(Tables.ActiveOpportunitiesSummary),
				filterDistrict: false,
				filterRegion: false,
				filterCountry: false,
				includeUnlabelled: true),
			[]);

		var stats = Services.Admin.ActivityCoverage.Summarise(sourceRows, vocabulary.Value!, threshold);

		return Ok(Document(
			new ActivityCoverageSummary
			{
				ActivityOpportunities = stats.ActivityOpportunities,
				LabelledOpportunities = stats.LabelledOpportunities,
				LabelledPercentage = stats.PercentageOfTotal(stats.LabelledOpportunities),
				LinkedOpportunities = stats.LinkedOpportunities,
				LinkedPercentage = stats.PercentageOfTotal(stats.LinkedOpportunities),
				ActivitiesOffered = stats.ActivitiesOffered,
				VocabularySize = stats.VocabularySize,
			},
			snapshotDate));
	}

	/// <summary>
	/// Top Activities
	/// </summary>
	/// <remarks>
	/// OpenActive activity-list concepts ranked nationally by future opportunities, largest first, then
	/// by activity label. The default page size is <c>10</c>, so a plain call returns the top 10; later
	/// pages continue the ranking, and <c>rank</c> is the position in the whole ranking, not on the page.
	///
	/// Activities only: facility bookings are excluded, and so is free text that matches no concept
	/// ("Gym Session", "Fitness Classes"). Labels are matched exactly as on
	/// <c>/admin/activity-coverage</c>, and counts follow the same rule: an opportunity counts once under
	/// each activity it carries. A concept's <c>opportunity_count</c> therefore equals the sum of its
	/// rows there, and <c>district_count</c> equals how many rows it has.
	///
	/// Opportunities with no activity label are not counted, so treat counts as a lower bound.
	///
	/// Answers <c>502</c> when the activity list cannot be fetched or read. Successful results are cached
	/// until the next daily refresh, varying by all query parameters.
	/// </remarks>
	/// <param name="page">One-based page number. Default <c>1</c>.</param>
	/// <param name="page_size">Rows per page. Default <c>10</c>, capped at <c>1000</c>.</param>
	/// <param name="min_similarity">Lowest label match score accepted, <c>0</c> to <c>1</c>. Default <c>0.85</c>; <c>1</c> accepts exact matches only (after normalisation). Values outside <c>0..1</c> are clamped.</param>
	[HttpGet("activity-coverage/top-activities")]
	[ProducesResponseType(typeof(AdminPage<TopActivityRow>), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status502BadGateway)]
	public async Task<ActionResult<AdminPage<TopActivityRow>>> TopActivities(
		int page = 1,
		int page_size = TopActivitiesPageSize,
		double min_similarity = ActivityLabelMatcher.DefaultMinSimilarity)
	{
		var threshold = Threshold(min_similarity);

		var vocabulary = await LoadVocabulary();
		if (vocabulary.Failure is not null)
		{
			return vocabulary.Failure;
		}

		var snapshotDate = await SnapshotDate();

		var sourceRows = await LoadRows(
			ActivityCoverageQuery.Sql(
				Fq(Tables.ActiveOpportunitiesSummary),
				filterDistrict: false,
				filterRegion: false,
				filterCountry: false),
			[]);

		var ranking = Services.Admin.ActivityCoverage
			.Rank(sourceRows, vocabulary.Value!, threshold)
			.Select(e => new TopActivityRow
			{
				Rank = e.Rank,
				Activity = e.Activity.PrefLabel,
				ActivityId = e.Activity.Id,
				OpportunityCount = e.OpportunityCount,
				DistrictCount = e.DistrictCount,
			})
			.ToList();

		return Ok(Paginate(ranking, page, page_size, snapshotDate));
	}

	#region Utilities

	/// <summary>Default page size of <c>top-activities</c>: a plain call returns the top 10.</summary>
	private const int TopActivitiesPageSize = 10;

	/// <summary>The requested match threshold, clamped to <c>0..1</c>; NaN falls back to the default.</summary>
	private static double Threshold(double minSimilarity) =>
		double.IsNaN(minSimilarity)
			? ActivityLabelMatcher.DefaultMinSimilarity
			: Math.Clamp(minSimilarity, 0, 1);

	/// <summary>The cached activity list, or the <c>502</c> to return when it cannot be fetched or read.</summary>
	private async Task<(ActivityVocabulary? Value, ObjectResult? Failure)> LoadVocabulary()
	{
		try
		{
			return (await vocabularySource.Get(HttpContext.RequestAborted), null);
		}
		catch (Exception e) when (IsUpstreamFailure(e))
		{
			return (null, Unavailable("The OpenActive activity list", e));
		}
	}

	/// <summary>The latest ingestion day, the run that rebuilt the summary; today if there is none.</summary>
	private async Task<DateOnly> SnapshotDate() =>
		await ResolveSnapshotDate(asOf: null) ?? DateOnly.FromDateTime(DateTime.UtcNow);

	private async Task<List<ActivityCoverageSourceRow>> LoadRows(string sql, IReadOnlyList<BigQueryParameter> parameters)
	{
		var rows = await Query(sql, parameters);
		return await rows.Select(ActivityCoverageQuery.Parse).ToListAsync();
	}

	private static ActivityCoverageRow ToRow(ActivityCoverageEntry entry) =>
		new()
		{
			DistrictCode = entry.DistrictCode,
			DistrictName = entry.DistrictName,
			RegionCode = entry.RegionCode,
			RegionName = entry.RegionName,
			CountryCode = entry.CountryCode,
			CountryName = entry.CountryName,
			Activity = entry.Activity.PrefLabel,
			ActivityId = entry.Activity.Id,
			OpportunityCount = entry.OpportunityCount,
		};

	#endregion
}
