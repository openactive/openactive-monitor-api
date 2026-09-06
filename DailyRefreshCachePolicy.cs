using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Primitives;

namespace MonitorApi;

/// <summary>
/// Output cache policy that holds a response until the next daily refresh time rather than for a fixed
/// duration, so every cached admin response is discarded at the same moment each day.
/// </summary>
/// <remarks>
/// The admin monitors describe one ingestion day at a time: the numbers do not move again until the
/// overnight pipeline has landed, so a fixed sliding window either serves stale figures past the
/// refresh or re-runs the whole ingestion history for nothing. Expiring at a wall-clock time instead
/// means a response cached at any point in the day survives exactly until the next refresh.
///
/// Apart from the expiry the behaviour matches the framework's default policy: <c>GET</c>/<c>HEAD</c>
/// only, never an authenticated request, never a response that sets a cookie or answers with anything
/// but <c>200</c> — so the <c>403</c> an invalid token earns is not cached.
/// </remarks>
/// <param name="refreshAt">Time of day, UTC, at which cached responses expire.</param>
public sealed class DailyRefreshCachePolicy(TimeOnly refreshAt) : IOutputCachePolicy
{
	/// <summary>Name this policy is registered under in <c>Program.cs</c>.</summary>
	public const string PolicyName = "DailyRefresh";

	private readonly TimeOnly refreshAt = refreshAt;

	/// <summary>
	/// How long a response cached at <paramref name="utcNow"/> stays valid: the time remaining until the
	/// next occurrence of the refresh time. Exactly at the refresh time the entry gets the full day,
	/// rather than expiring immediately.
	/// </summary>
	public TimeSpan TimeUntilRefresh(DateTime utcNow)
	{
		var next = utcNow.Date + refreshAt.ToTimeSpan();
		if (next <= utcNow)
		{
			next = next.AddDays(1);
		}

		return next - utcNow;
	}

	ValueTask IOutputCachePolicy.CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
	{
		var cacheable = IsCacheableRequest(context.HttpContext.Request);

		context.EnableOutputCaching = true;
		context.AllowCacheLookup = cacheable;
		context.AllowCacheStorage = cacheable;
		context.AllowLocking = true;

		// Every admin endpoint's answer depends on its query string, the token included.
		context.CacheVaryByRules.QueryKeys = "*";
		context.ResponseExpirationTimeSpan = TimeUntilRefresh(DateTime.UtcNow);

		return ValueTask.CompletedTask;
	}

	ValueTask IOutputCachePolicy.ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken) =>
		ValueTask.CompletedTask;

	ValueTask IOutputCachePolicy.ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken)
	{
		var response = context.HttpContext.Response;

		if (!StringValues.IsNullOrEmpty(response.Headers.SetCookie) ||
			response.StatusCode != StatusCodes.Status200OK)
		{
			context.AllowCacheStorage = false;
		}

		return ValueTask.CompletedTask;
	}

	private static bool IsCacheableRequest(HttpRequest request) =>
		(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) &&
		StringValues.IsNullOrEmpty(request.Headers.Authorization) &&
		request.HttpContext.User.Identity?.IsAuthenticated != true;
}
