namespace MonitorApi.Models.Admin;

/// <summary>
/// National headline figures for activity data: how much of the future activity supply publishes an
/// activity, how much of that reaches the OpenActive activity list, and how many listed activities are
/// on offer.
/// </summary>
/// <remarks>
/// Every percentage is of <see cref="ActivityOpportunities"/>, so
/// <c>linked_percentage ≤ labelled_percentage</c> always holds. Each opportunity counts once, however
/// many activities it carries.
/// </remarks>
public sealed class ActivityCoverageSummary
{
	/// <summary>Future activity opportunities nationally, with or without an activity label. Facility bookings are excluded.</summary>
	public required long ActivityOpportunities { get; init; }

	/// <summary>Of those, the opportunities that publish at least one activity label (their own or inherited from a parent event).</summary>
	public required long LabelledOpportunities { get; init; }

	/// <summary>
	/// <see cref="LabelledOpportunities"/> as a percentage of <see cref="ActivityOpportunities"/>, to one
	/// decimal place. <c>null</c> when there are no activity opportunities.
	/// </summary>
	public required double? LabelledPercentage { get; init; }

	/// <summary>
	/// Of those, the opportunities with at least one label matched to an OpenActive activity-list
	/// concept at the requested <c>min_similarity</c>.
	/// </summary>
	public required long LinkedOpportunities { get; init; }

	/// <summary>
	/// <see cref="LinkedOpportunities"/> as a percentage of <see cref="ActivityOpportunities"/> (not of
	/// the labelled ones), to one decimal place. <c>null</c> when there are no activity opportunities.
	/// </summary>
	public required double? LinkedPercentage { get; init; }

	/// <summary>Distinct activity-list concepts with at least one future opportunity.</summary>
	public required int ActivitiesOffered { get; init; }

	/// <summary>Concepts in the OpenActive activity list, the ceiling for <see cref="ActivitiesOffered"/>.</summary>
	public required int VocabularySize { get; init; }
}
