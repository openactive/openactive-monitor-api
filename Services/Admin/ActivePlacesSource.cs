using Microsoft.Extensions.Options;

namespace MonitorApi.Services.Admin;

/// <summary>
/// Fetches the two published Active Places files and holds them until the next daily refresh.
/// </summary>
/// <remarks>
/// The one thing in <c>Services/Admin</c> that is not pure, and deliberately the only one: the readers
/// in <c>ActivePlacesMonitor.cs</c> stay testable from a string literal because every byte of network
/// handling is here instead. It touches no ASP.NET type either — the controller turns a failure into a
/// status code.
///
/// <b>Why it caches.</b> Output caching alone is not enough. Its entries vary by the whole query
/// string, so each page, filter and token is a separate entry, and each miss would otherwise re-download
/// several megabytes of CSV and re-parse it. The copy held here is shared by every one of them.
///
/// <b>Why it caches until 07:00 UTC.</b> The same boundary as the responses built from it, so a caller
/// walking the pages sees one report throughout: a shorter window could hand them page 2 of a report
/// published after they fetched page 1, with rows silently added or removed in between.
///
/// Registered as a singleton, so the cache is per process rather than per request. Only one caller
/// fetches at a time and the rest wait for that result; a failure is not cached, so the next request
/// retries rather than inheriting the error for the rest of the day.
/// </remarks>
public sealed class ActivePlacesSource(IHttpClientFactory httpClientFactory, IOptions<ActivePlacesOptions> options)
{
	/// <summary>Name the <see cref="HttpClient"/> for this source is registered under.</summary>
	public const string HttpClientName = "active-places";

	private readonly ActivePlacesOptions options = options.Value;
	private readonly SemaphoreSlim gate = new(1, 1);

	private ActivePlacesCoverageReport? coverage;
	private DateTime coverageExpiresAt;

	private List<ActivePlacesSiteMapping>? mappings;
	private DateTime mappingsExpiresAt;

	/// <summary>The coverage report, from cache when it is still current.</summary>
	/// <exception cref="InvalidDataException">The published report could not be read.</exception>
	/// <exception cref="HttpRequestException">The report could not be fetched.</exception>
	public async Task<ActivePlacesCoverageReport> GetCoverage(CancellationToken cancellationToken = default)
	{
		await gate.WaitAsync(cancellationToken);
		try
		{
			if (coverage is not null && DateTime.UtcNow < coverageExpiresAt)
			{
				return coverage;
			}

			var report = ActivePlacesCoverageReader.Read(await Fetch(options.CoverageJsonUrl, cancellationToken));

			coverage = report;
			coverageExpiresAt = NextRefresh();
			return report;
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>
	/// Every row of the published site mapping, in file order, from cache when it is still current.
	/// </summary>
	/// <exception cref="InvalidDataException">The published file could not be read.</exception>
	/// <exception cref="HttpRequestException">The file could not be fetched.</exception>
	public async Task<IReadOnlyList<ActivePlacesSiteMapping>> GetSiteMappings(CancellationToken cancellationToken = default)
	{
		await gate.WaitAsync(cancellationToken);
		try
		{
			if (mappings is not null && DateTime.UtcNow < mappingsExpiresAt)
			{
				return mappings;
			}

			var rows = ActivePlacesMappingParser.Parse(await Fetch(options.SiteMappingCsvUrl, cancellationToken));

			mappings = rows;
			mappingsExpiresAt = NextRefresh();
			return rows;
		}
		finally
		{
			gate.Release();
		}
	}

	private async Task<string> Fetch(string url, CancellationToken cancellationToken)
	{
		using var client = httpClientFactory.CreateClient(HttpClientName);
		client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 300));

		var response = await client.GetAsync(url, cancellationToken);
		response.EnsureSuccessStatusCode();

		return await response.Content.ReadAsStringAsync(cancellationToken);
	}

	private static DateTime NextRefresh()
	{
		var now = DateTime.UtcNow;
		return now + DailyRefreshCachePolicy.TimeUntilRefresh(now, DailyRefreshCachePolicy.AdminRefreshAt);
	}
}
