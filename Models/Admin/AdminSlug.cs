using System.Text;

namespace MonitorApi.Models.Admin;

/// <summary>
/// The slug form used in admin identifiers such as <c>publisher_id</c>.
/// </summary>
/// <remarks>
/// Shared so that a publisher gets the same <c>pub_</c> slug whether it is reached through a feed or
/// through a dataset. The dashboard groups incidents on that value across monitors, so two
/// implementations drifting apart would silently split one publisher in two.
/// </remarks>
public static class AdminSlug
{
	/// <summary>Slug used when there is no name to derive one from.</summary>
	public const string Unknown = "unknown";

	/// <summary>
	/// Lower-cases, keeps letters and digits, and collapses every other run of characters to a single
	/// hyphen with no leading or trailing one. Returns <see cref="Unknown"/> for null, blank, or
	/// entirely non-alphanumeric input.
	/// </summary>
	public static string Of(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return Unknown;
		}

		var slug = new StringBuilder(value.Length);
		var pendingSeparator = false;

		foreach (var c in value)
		{
			if (char.IsLetterOrDigit(c))
			{
				if (pendingSeparator && slug.Length > 0)
				{
					slug.Append('-');
				}
				pendingSeparator = false;
				slug.Append(char.ToLowerInvariant(c));
			}
			else
			{
				pendingSeparator = true;
			}
		}

		return slug.Length == 0 ? Unknown : slug.ToString();
	}
}
