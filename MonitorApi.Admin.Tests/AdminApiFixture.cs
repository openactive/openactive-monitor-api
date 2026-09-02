using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace MonitorApi.Admin.Tests;

/// <summary>
/// Boots the real <see cref="Program"/> in-process for the <c>/admin</c> surface and exposes an
/// <see cref="HttpClient"/> that hits live BigQuery through the actual controllers. Configuration comes
/// from the web project's <c>appsettings.Development.json</c>; the BigQuery credentials path is
/// rewritten to an absolute path so it resolves whatever the test process's working directory is.
/// </summary>
/// <remarks>
/// Mirrors <c>MonitorApi.Tests.ApiFixture</c> but carries the admin token instead of the public access
/// token, so the two suites stay independent and can run with only the secret each one needs.
/// Tests that exercise pure detection logic take no fixture and need no credentials at all.
/// </remarks>
public sealed class AdminApiFixture : WebApplicationFactory<Program>
{
	private string adminToken = "";
	private string publicAccessToken = "";

	/// <summary>The configured <c>Api:AdminToken</c>.</summary>
	public string AdminToken
	{
		get
		{
			EnsureConfigurationRead();
			return adminToken;
		}
	}

	/// <summary>The configured <c>Api:AccessToken</c>, used to prove the two tokens are not interchangeable.</summary>
	public string PublicAccessToken
	{
		get
		{
			EnsureConfigurationRead();
			return publicAccessToken;
		}
	}

	/// <summary>
	/// Configuration is only read once the host is built, which happens lazily. Touching
	/// <see cref="WebApplicationFactory{TEntryPoint}.Server"/> forces it, so the tokens are populated
	/// whether or not the test has created a client yet.
	/// </summary>
	private void EnsureConfigurationRead() => _ = Server;

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		var contentRoot = ResolveContentRoot();
		builder.UseContentRoot(contentRoot);
		builder.UseEnvironment("Development");

		builder.ConfigureAppConfiguration((_, cfg) =>
		{
			cfg.SetBasePath(contentRoot);

			var built = cfg.Build();
			var credentials = built["BigQuery:Credentials"];
			adminToken = built["Api:AdminToken"] ?? "";
			publicAccessToken = built["Api:AccessToken"] ?? "";

			if (string.IsNullOrWhiteSpace(adminToken))
			{
				throw new InvalidOperationException(
					"Api:AdminToken is not configured. Add it to appsettings.Development.json " +
					"(see docs/development.md) — the admin endpoints refuse every request without it.");
			}

			if (!string.IsNullOrWhiteSpace(credentials) &&
				credentials.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
				!Path.IsPathRooted(credentials))
			{
				cfg.AddInMemoryCollection(new Dictionary<string, string?>
				{
					["BigQuery:Credentials"] = Path.Combine(contentRoot, credentials),
				});
			}
		});
	}

	private static string ResolveContentRoot()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MonitorApi.csproj")))
		{
			dir = dir.Parent;
		}
		if (dir is null)
		{
			throw new InvalidOperationException("Could not locate MonitorApi.csproj from " + AppContext.BaseDirectory);
		}
		return dir.FullName;
	}

	/// <summary>Appends the admin token to an admin path, preserving any existing query string.</summary>
	public string WithAdminToken(string path) => WithToken(path, AdminToken);

	/// <summary>Appends an arbitrary token to a path — for the negative auth cases.</summary>
	public static string WithToken(string path, string token) =>
		path + (path.Contains('?') ? "&" : "?") + "token=" + Uri.EscapeDataString(token);
}
