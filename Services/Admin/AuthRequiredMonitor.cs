namespace MonitorApi.Services.Admin;

/// <summary>
/// A feed currently refusing the crawler with 401 and 403, before feed metadata is attached.
/// </summary>
public sealed record AuthRequired
{
    public required string FeedId { get; init; }

    /// <summary>The dataset the feed belongs to, matching <c>feeds.dataset_url</c>.</summary>
    public required string DatasetId { get; init; }
    
    /// <summary>First day of the current unbroken run of auth failures.</summary>
    public required DateOnly FirstFailed { get; init; }
    
    /// <summary>Calendar days from <see cref="FirstFailed"/> to the evaluated day, inclusive</summary>
    public required int ConsecutiveDays { get; init; }
    
    /// <summary>The evaluated day's code: <c>401</c> or <c>403</c></summary>
    public required string ErrorCode { get; init; }
    
    /// <summary>The evaluated day's failure message, if the pipeline recorded one.</summary>
    public required string? ErrorMessage { get; init; }
} 

/// <summary>
/// Pure detection logic for the decision tree's "Authentication required" row: a feed whose ingestion
/// is failing with 401 or 403.
/// </summary>
/// <remarks>
/// Reports every current run, however short. A run is broken by any recorded day that is not an auth failure — a completed day, a different
/// error, a warning, or a failure with no code (recorded before <c>error_code</c> existed, so its cause
/// is unknown). Days with no ingestion run at all are not evidence either way and do not break it.
/// </remarks>
public static class AuthRequiredDetector
{
    /// <summary>
    /// Detects feeds failing with an auth code on <paramref name="asOf"/>, longest-running first.
    /// </summary>
    public static IReadOnlyList<AuthRequired> Detect(IEnumerable<FeedStatusHistory> feeds, DateOnly asOf)
    {
        var incidents = new List<AuthRequired>();

        foreach (var feed in feeds)
        {
            if (Evaluate(feed, asOf) is { } incident)
            {
                incidents.Add(incident);
            }
        }

        return incidents
            .OrderByDescending(i => i.ConsecutiveDays)
            .ThenBy(i => i.FeedId, StringComparer.Ordinal).ToList();
    }

    private static AuthRequired? Evaluate(FeedStatusHistory feed, DateOnly asOf)
    {
        // Newest first, ignoring anyhting after the evaluated day so as_of replays are honest.
        var days = feed.Days
            .Where(d => d.Day <= asOf)
            .OrderByDescending(d => d.Day)
            .ToList();

        if (days.Count == 0 || days[0].Day != asOf || !IsAuthFailure(days[0]))
        {
            // No run on the evaluated day, or a run that was not an auth failure
            return null;
        }

        var firstFailed = asOf;
        foreach (var day in days)
        {
            if (!IsAuthFailure(day))
            {
                break;
            }
            firstFailed = day.Day;
        }

        var today = days[0];

        return new AuthRequired
        {
            FeedId = feed.FeedId,
            DatasetId = feed.DatasetId,
            FirstFailed = firstFailed,
            ConsecutiveDays = asOf.DayNumber - firstFailed.DayNumber + 1,
            ErrorCode = today.ErrorCode!,
            ErrorMessage = today.ErrorMessage,
        };
    }

    private static bool IsAuthFailure(FeedIngestionDay day) =>
        day.Errored &&
        day.ErrorCode is { } code &&
        FeedIngestionErrorDetector.AuthErrorCodes.Contains(code);
}
