namespace MonitorApi;

/// <summary>
/// Where the OpenActive activity list is published. Bound from the <c>ActivityList</c> configuration
/// section.
/// </summary>
/// <remarks>
/// <c>/admin/activity-coverage</c> reports only concepts from this vocabulary. The default points at
/// the canonical published list, so an environment that configures nothing still serves the endpoint.
/// The section exists so a deployment can pin a mirror or a fixed revision without a rebuild.
///
/// The URL is public and unauthenticated, so like <see cref="ActivePlacesOptions"/> it lives in
/// <c>appsettings.json</c> rather than in secrets.
/// </remarks>
public class ActivityListOptions
{
	public const string SectionName = "ActivityList";

	/// <summary>The activity list as JSON-LD.</summary>
	public string Url { get; set; } = "https://openactive.io/activity-list/activity-list.jsonld";

	/// <summary>
	/// How long to wait for the list before giving up and answering <c>502</c>. Default 30 seconds.
	/// </summary>
	public int TimeoutSeconds { get; set; } = 30;
}
