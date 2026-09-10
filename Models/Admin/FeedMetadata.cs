namespace MonitorApi.Models.Admin;

/// <summary>
/// Descriptive fields for a feed, joined from <c>feeds</c> and <c>feed_quality</c>. Not returned
/// directly; incidents are hydrated from it.
/// </summary>
public sealed record FeedMetadata(
	string FeedId,
	string? FeedUrl,
	string? FeedType,
	string? PublisherName,
	double? QualityScore)
{
	/// <summary>
	/// Short feed name: the last non-empty path segment of the feed URL, falling back to the feed id.
	/// </summary>
	public string FeedName
	{
		get
		{
			if (string.IsNullOrWhiteSpace(FeedUrl))
			{
				return FeedId;
			}

			var path = Uri.TryCreate(FeedUrl, UriKind.Absolute, out var uri) ? uri.AbsolutePath : FeedUrl;
			var segment = path
				.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.LastOrDefault();

			return string.IsNullOrWhiteSpace(segment) ? FeedId : segment;
		}
	}

	/// <summary>Publisher slug, e.g. <c>pub_freedom-leisure</c>, or <c>pub_unknown</c> when unnamed.</summary>
	public string PublisherId => "pub_" + AdminSlug.Of(PublisherName);
}
