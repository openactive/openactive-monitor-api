using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace MonitorApi.Tests;

/// <summary>
/// Boots the real <see cref="Program"/> in-process and exposes an <see cref="HttpClient"/> that
/// hits live BigQuery via the actual controller. Configuration is read from the project's
/// <c>appsettings.Development.json</c>; the BigQuery credentials path is rewritten to an absolute
/// path so it resolves regardless of where the test process's working directory is.
/// </summary>
public sealed class ApiFixture : WebApplicationFactory<Program>
{
	public string AccessToken { get; private set; } = "";

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		var contentRoot = ResolveContentRoot();
		builder.UseContentRoot(contentRoot);
		builder.UseEnvironment("Development");

		builder.ConfigureAppConfiguration((_, cfg) =>
		{
			cfg.SetBasePath(contentRoot);

			// Pull current config, then rewrite the credentials path to an absolute path so the
			// app can read it whatever the working directory.
			var built = cfg.Build();
			var credentials = built["BigQuery:Credentials"];
			AccessToken = built["Api:AccessToken"] ?? "";

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

	public HttpClient CreateAuthenticatedClient() => CreateClient();

	public string WithToken(string path) =>
		path + (path.Contains('?') ? "&" : "?") + "token=" + Uri.EscapeDataString(AccessToken);
}
