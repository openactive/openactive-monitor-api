namespace MonitorApi;

/// <summary>
/// Which GitHub repositories the stewardship endpoints read, and who the stewards are. Bound from the
/// <c>GitHub</c> configuration section.
/// </summary>
/// <remarks>
/// Split by sensitivity. The repository names, lookback and timeout are public and live in
/// <c>appsettings.json</c>. <see cref="Stewards"/> is a secret: it is an empty placeholder in
/// <c>appsettings.json</c> and is supplied by the gitignored
/// <c>appsettings.Development.json</c> locally, by App Service application settings in production
/// (<c>GitHub__Stewards__0__Login</c>, <c>GitHub__Stewards__0__Name</c>, …) and by a repository secret
/// in CI. Never commit a steward's login or name.
///
/// GitHub is read anonymously: every repository read is public, so no token is configured. That caps
/// the API at GitHub's anonymous limit of 60 requests an hour per IP, which the daily cache in
/// <c>GitHubSource</c> keeps well inside.
/// </remarks>
public class GitHubOptions
{
	public const string SectionName = "GitHub";

	/// <summary>
	/// The OpenActive stewards. A person on this list is never counted as a requester, and only their
	/// replies count as a response. Empty by default: the endpoints still work, but every author counts
	/// as community and no reply counts as a steward's.
	/// </summary>
	public List<Steward> Stewards { get; set; } = [];

	/// <summary>The repository dataset addition requests are raised against, as <c>owner/name</c>.</summary>
	public string CatalogRepository { get; set; } = "openactive/data-catalogs";

	/// <summary>The OpenActive tooling repositories whose issues are tracked, each as <c>owner/name</c>.</summary>
	public List<string> ToolingRepositories { get; set; } = [];

	/// <summary>How far back, in days, a pull request or issue may have been opened to be reported. Default 365.</summary>
	public int LookbackDays { get; set; } = 365;

	/// <summary>How long to wait for each GitHub request before giving up and answering <c>502</c>. Default 30 seconds.</summary>
	public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>One OpenActive steward: their GitHub login and the name the dashboard shows for them.</summary>
public class Steward
{
	/// <summary>GitHub login, matched ignoring case.</summary>
	public string Login { get; set; } = "";

	/// <summary>Display name. Falls back to <see cref="Login"/> when blank.</summary>
	public string Name { get; set; } = "";
}
