namespace MonitorApi.Models.Admin;

/// <summary>
/// Descriptive fields for a dataset, joined from <c>feeds</c> and <c>feed_quality</c>. Not returned
/// directly; incidents are hydrated from it.
/// </summary>
/// <remarks>
/// The dataset-scoped sibling of <see cref="FeedMetadata"/>, and monitor-agnostic in the same way:
/// every dataset-level monitor needs the same publisher identity. Carries no quality score —
/// <c>feed_quality.score</c> is per-feed, and averaging a dataset's feeds would invent a figure
/// nobody asked for.
/// </remarks>
/// <param name="DatasetUrl">The dataset identifier, matching <c>feeds.dataset_url</c>.</param>
/// <param name="DatasetName">Stored name from <c>feed_quality</c>, or <c>null</c> when unassessed.</param>
/// <param name="PublisherName">Publisher name from <c>feeds</c>, or <c>null</c> when the dataset has no feed row.</param>
public sealed record DatasetMetadata(
	string DatasetUrl,
	string? DatasetName,
	string? PublisherName)
{
	/// <summary>Publisher slug, e.g. <c>pub_freedom-leisure</c>, or <c>pub_unknown</c> when unnamed.</summary>
	public string PublisherId => "pub_" + AdminSlug.Of(PublisherName);

	/// <summary>
	/// Short display name: the stored <c>dataset_name</c>, falling back to the URL's host and then to
	/// the URL itself, so it is never empty.
	/// </summary>
	/// <remarks>
	/// Follows the shape of <see cref="FeedMetadata.FeedName"/> — derive a display name from the URL,
	/// fall back to the identifier — without sharing it: a feed wants the last path segment, a dataset
	/// wants the host.
	/// </remarks>
	public string Name
	{
		get
		{
			if (!string.IsNullOrWhiteSpace(DatasetName))
			{
				return DatasetName;
			}

			return Uri.TryCreate(DatasetUrl, UriKind.Absolute, out var uri) && uri.Host.Length > 0
				? uri.Host
				: DatasetUrl;
		}
	}
}
