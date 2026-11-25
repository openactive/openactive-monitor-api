using System.Globalization;
using System.Text.Json;

namespace MonitorApi.Services.Admin;

/// <summary>A GitHub account as it appears on an issue, pull request, comment or review.</summary>
/// <param name="Login">The account's login.</param>
/// <param name="IsBot">Whether GitHub reports the account as a bot (<c>type: "Bot"</c>).</param>
public sealed record GitHubUser(string Login, bool IsBot);

/// <summary>One pull request from the REST list endpoint.</summary>
public sealed record GitHubPullRequest(
	int Number,
	string Title,
	string HtmlUrl,
	GitHubUser? Author,
	DateTime CreatedAt,
	DateTime? ClosedAt,
	DateTime? MergedAt,
	IReadOnlyList<string> Assignees,
	IReadOnlyList<string> RequestedReviewers);

/// <summary>Something a person said on a pull request: a comment, a review, or a review comment.</summary>
public sealed record GitHubInteraction(string Login, DateTime At);

/// <summary>A pull request together with every interaction on it.</summary>
public sealed record GitHubPullRequestActivity(GitHubPullRequest PullRequest, IReadOnlyList<GitHubInteraction> Interactions);

/// <summary>One issue from the REST list endpoint. Pull requests are never represented as this.</summary>
public sealed record GitHubIssue(
	string Repo,
	int Number,
	string Title,
	string HtmlUrl,
	GitHubUser? Author,
	DateTime CreatedAt,
	DateTime? ClosedAt,
	bool IsOpen,
	IReadOnlyList<string> Assignees);

/// <summary>
/// Reads GitHub REST API responses into plain records. Pure: no network, so every shape is testable
/// from a string literal. Anything that is not the expected JSON throws
/// <see cref="InvalidDataException"/>, which the controller turns into a <c>502</c>.
/// </summary>
public static class GitHubParser
{
	/// <summary>Reads a page of <c>GET /repos/{repo}/pulls</c>.</summary>
	public static List<GitHubPullRequest> ParsePullRequests(string json) =>
		ReadArray(json, "pull requests", item => new GitHubPullRequest(
			Number: RequiredInt(item, "number"),
			Title: OptionalString(item, "title") ?? "",
			HtmlUrl: OptionalString(item, "html_url") ?? "",
			Author: User(item, "user"),
			CreatedAt: RequiredDate(item, "created_at"),
			ClosedAt: OptionalDate(item, "closed_at"),
			MergedAt: OptionalDate(item, "merged_at"),
			Assignees: Logins(item, "assignees"),
			RequestedReviewers: Logins(item, "requested_reviewers")));

	/// <summary>
	/// Reads a page of <c>GET /repos/{repo}/issues</c>. That endpoint also lists pull requests; those
	/// carry a <c>pull_request</c> key and are dropped.
	/// </summary>
	public static List<GitHubIssue> ParseIssues(string json, string repo) =>
		ReadArray(json, "issues", item => item.TryGetProperty("pull_request", out _)
			? null
			: new GitHubIssue(
				Repo: repo,
				Number: RequiredInt(item, "number"),
				Title: OptionalString(item, "title") ?? "",
				HtmlUrl: OptionalString(item, "html_url") ?? "",
				Author: User(item, "user"),
				CreatedAt: RequiredDate(item, "created_at"),
				ClosedAt: OptionalDate(item, "closed_at"),
				IsOpen: !string.Equals(OptionalString(item, "state"), "closed", StringComparison.OrdinalIgnoreCase),
				Assignees: Logins(item, "assignees")));

	/// <summary>
	/// Reads a page of issue comments (<c>/issues/{n}/comments</c>) or review comments
	/// (<c>/pulls/{n}/comments</c>). Both date a comment by <c>created_at</c>.
	/// </summary>
	public static List<GitHubInteraction> ParseComments(string json) =>
		ReadArray(json, "comments", item =>
		{
			var user = User(item, "user");
			return user is null ? null : new GitHubInteraction(user.Login, RequiredDate(item, "created_at"));
		});

	/// <summary>
	/// Reads a page of <c>/pulls/{n}/reviews</c>. A <c>PENDING</c> review has not been submitted, so
	/// the author has not yet said anything visible; it is dropped.
	/// </summary>
	public static List<GitHubInteraction> ParseReviews(string json) =>
		ReadArray(json, "reviews", item =>
		{
			var user = User(item, "user");
			var submitted = OptionalDate(item, "submitted_at");
			if (user is null || submitted is null ||
				string.Equals(OptionalString(item, "state"), "PENDING", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			return new GitHubInteraction(user.Login, submitted.Value);
		});

	/// <summary>
	/// The URL of the next page from a <c>Link</c> response header, or <c>null</c> on the last page.
	/// </summary>
	public static string? NextPageUrl(string? linkHeader)
	{
		if (string.IsNullOrWhiteSpace(linkHeader))
		{
			return null;
		}

		foreach (var part in linkHeader.Split(','))
		{
			var segments = part.Split(';', StringSplitOptions.TrimEntries);
			if (segments.Length < 2 || !segments.Skip(1).Any(s => s == "rel=\"next\""))
			{
				continue;
			}

			var url = segments[0];
			if (url.StartsWith('<') && url.EndsWith('>'))
			{
				return url[1..^1];
			}
		}

		return null;
	}

	private static List<T> ReadArray<T>(string json, string what, Func<JsonElement, T?> read) where T : class
	{
		try
		{
			using var document = JsonDocument.Parse(json);
			if (document.RootElement.ValueKind != JsonValueKind.Array)
			{
				throw new InvalidDataException($"GitHub returned {document.RootElement.ValueKind} where a list of {what} was expected.");
			}

			var results = new List<T>();
			foreach (var item in document.RootElement.EnumerateArray())
			{
				if (item.ValueKind != JsonValueKind.Object)
				{
					throw new InvalidDataException($"GitHub returned a {item.ValueKind} in a list of {what}.");
				}
				if (read(item) is { } value)
				{
					results.Add(value);
				}
			}
			return results;
		}
		catch (JsonException e)
		{
			throw new InvalidDataException($"GitHub returned unreadable {what}: {e.Message}", e);
		}
	}

	private static GitHubUser? User(JsonElement item, string property)
	{
		if (!item.TryGetProperty(property, out var user) || user.ValueKind != JsonValueKind.Object)
		{
			return null;
		}

		var login = OptionalString(user, "login");
		return string.IsNullOrEmpty(login)
			? null
			: new GitHubUser(login, string.Equals(OptionalString(user, "type"), "Bot", StringComparison.Ordinal));
	}

	private static List<string> Logins(JsonElement item, string property)
	{
		if (!item.TryGetProperty(property, out var users) || users.ValueKind != JsonValueKind.Array)
		{
			return [];
		}

		return users.EnumerateArray()
			.Where(u => u.ValueKind == JsonValueKind.Object)
			.Select(u => OptionalString(u, "login"))
			.OfType<string>()
			.Where(l => l.Length > 0)
			.ToList();
	}

	private static string? OptionalString(JsonElement item, string property) =>
		item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	private static int RequiredInt(JsonElement item, string property) =>
		item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)
			? n
			: throw new InvalidDataException($"GitHub returned an item without a numeric '{property}'.");

	private static DateTime RequiredDate(JsonElement item, string property) =>
		OptionalDate(item, property) ?? throw new InvalidDataException($"GitHub returned an item without '{property}'.");

	private static DateTime? OptionalDate(JsonElement item, string property)
	{
		var text = OptionalString(item, property);
		if (text is null)
		{
			return null;
		}

		return DateTime.TryParse(text, CultureInfo.InvariantCulture,
			DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
			? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
			: throw new InvalidDataException($"GitHub returned an unreadable '{property}': {text}");
	}
}
