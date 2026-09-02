namespace MonitorApi;

/// <summary>
/// The OpenAPI documents this API publishes. Each is served at <c>/openapi/{name}.json</c> and is
/// selectable in the Scalar reference at <c>/scalar</c>.
/// </summary>
internal static class ApiDocuments
{
	/// <summary>The public analytics endpoints that power the OpenActive dashboards.</summary>
	public const string Analytics = "v1";

	/// <summary>The <c>/admin</c> endpoints that power the admin dashboard.</summary>
	public const string Admin = "admin";

	/// <summary>
	/// Value of <c>ApiExplorerSettings.GroupName</c> that puts a controller in the admin document.
	/// Set once on <c>AdminControllerBase</c>, so every admin controller inherits it.
	/// </summary>
	public const string AdminGroupName = Admin;
}
