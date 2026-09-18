namespace MonitorApi;

/// <summary>
/// Where the Active Places coverage analysis is published. Bound from the <c>ActivePlaces</c>
/// configuration section.
/// </summary>
/// <remarks>
/// The only part of this API whose data does not come from BigQuery: the analysis runs in the
/// <c>openactive-monitor</c> jobs repository and publishes its output as two files in that repository,
/// which this API mirrors rather than recomputes. The defaults point at those files on <c>main</c>, so
/// an environment that configures nothing still serves the endpoints; the section exists so a
/// deployment can point at a fork, a branch or a pinned revision without a rebuild.
///
/// Neither URL is a secret and neither is authenticated, which is why they sit in
/// <c>appsettings.json</c> alongside the empty credential placeholders rather than in user secrets.
/// </remarks>
public class ActivePlacesOptions
{
	public const string SectionName = "ActivePlaces";

	private const string ReportRoot =
		"https://raw.githubusercontent.com/openactive-contrib/openactive-monitor/refs/heads/main" +
		"/jobs/opportunity-insights/reports/active_places/";

	/// <summary>
	/// The site-to-OpenActive mapping CSV — one row per matched site/venue pair. Served by
	/// <c>/admin/active-places-site-mappings</c>.
	/// </summary>
	public string SiteMappingCsvUrl { get; set; } = ReportRoot + "site_oa_mapping.csv";

	/// <summary>
	/// The coverage summary JSON — the headline figures and every breakdown behind them. Served by
	/// <c>/admin/active-places-coverage</c>, and the source of <c>meta.snapshot_date</c> for both
	/// endpoints.
	/// </summary>
	public string CoverageJsonUrl { get; set; } = ReportRoot + "active_places_coverage.json";

	/// <summary>
	/// How long to wait for either file before giving up and answering <c>502</c>. Default 30 seconds.
	/// </summary>
	public int TimeoutSeconds { get; set; } = 30;
}
