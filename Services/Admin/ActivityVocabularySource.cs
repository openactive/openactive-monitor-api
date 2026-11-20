using Microsoft.Extensions.Options;

namespace MonitorApi.Services.Admin;

/// <summary>
/// Fetches the OpenActive activity list and holds it, parsed, until the next daily refresh.
/// </summary>
/// <remarks>
/// Like <see cref="ActivePlacesSource"/>, this is network I/O kept out of the pure code beside it:
/// <see cref="ActivityVocabulary"/> and <see cref="ActivityLabelMatcher"/> stay testable from a string
/// literal because every byte of network handling is here. It touches no ASP.NET type either. The
/// controller turns a failure into a status code.
///
/// <b>Why it caches.</b> Output-cache entries vary by the whole query string, so each page, filter and
/// threshold is a separate miss. Without this copy each one would re-download the list (several hundred
/// kilobytes) and rebuild the matcher, losing its memoised label matches.
///
/// <b>Why until 07:00 UTC.</b> The same boundary as the responses built from it, so a caller walking
/// the pages always sees one version of the vocabulary.
///
/// Registered as a singleton. Only one caller fetches at a time and the rest wait for that result. A
/// failure is not cached, so the next request retries.
/// </remarks>
public sealed class ActivityVocabularySource(IHttpClientFactory httpClientFactory, IOptions<ActivityListOptions> options)
{
	/// <summary>Name the <see cref="HttpClient"/> for this source is registered under.</summary>
	public const string HttpClientName = "activity-list";

	private readonly ActivityListOptions options = options.Value;
	private readonly SemaphoreSlim gate = new(1, 1);

	private ActivityVocabulary? vocabulary;
	private DateTime expiresAt;

	/// <summary>The activity list, from cache when it is still current.</summary>
	/// <exception cref="InvalidDataException">The published list could not be read.</exception>
	/// <exception cref="HttpRequestException">The list could not be fetched.</exception>
	public async Task<ActivityVocabulary> Get(CancellationToken cancellationToken = default)
	{
		await gate.WaitAsync(cancellationToken);
		try
		{
			if (vocabulary is not null && DateTime.UtcNow < expiresAt)
			{
				return vocabulary;
			}

			using var client = httpClientFactory.CreateClient(HttpClientName);
			client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 300));

			var response = await client.GetAsync(options.Url, cancellationToken);
			response.EnsureSuccessStatusCode();

			var parsed = ActivityVocabulary.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

			var now = DateTime.UtcNow;
			vocabulary = parsed;
			expiresAt = now + DailyRefreshCachePolicy.TimeUntilRefresh(now, DailyRefreshCachePolicy.AdminRefreshAt);
			return parsed;
		}
		finally
		{
			gate.Release();
		}
	}
}
