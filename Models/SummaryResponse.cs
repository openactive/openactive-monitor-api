using System.Text.Json.Serialization;

namespace MonitorApi.Models;

/// <summary>Aggregate metrics returned by the <c>/summary</c> endpoint.</summary>
public class SummaryResponse
{
	public required long NumberOfOpportunities { get; init; }

	public required long NumberOfPublishers { get; init; }

	public required long NumberOfActivities { get; init; }

	public required long NumberOfFacilityTypes { get; init; }

	public required long NumberOfFacilities { get; init; }

	public required int PercentageOfLocalAuthorities { get; init; }

	public required long NumberOfActivityProviders { get; init; }

	public required DateTime Date { get; init; }
}
