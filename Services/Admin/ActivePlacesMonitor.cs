// Everything the Active Places coverage surface is made of except the fetching: the site/venue mapping
// row, the coverage report, and the two pure readers that turn the published CSV and JSON into them.
// Same one-file convention as OrphanedChildrenMonitor.cs and FeedQualityMonitor.cs, and the same split
// between *reading* and *fetching* enforced by type rather than by file — nothing here touches the
// network, BigQuery or ASP.NET, so every parsing rule is unit tested from a string literal.
//
// Unlike its siblings in this folder this is not a monitor and, unlike all of them, it is not backed by
// BigQuery: the analysis runs elsewhere and publishes two files, which ActivePlacesSource fetches and
// these readers parse. Nothing here detects an incident, opens one, or tracks one over time.

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MonitorApi.Services.Admin;

/// <summary>
/// One row of the published site mapping: an Active Places site paired with an OpenActive venue that
/// covers it, on one of the three match channels.
/// </summary>
/// <remarks>
/// A <em>pair</em>, not a site — a site matched by two publishers appears twice, and a venue near two
/// sites likewise. The published CSV carries more columns than this; the ones left out are the
/// geography and classification duplicates (<c>region_code</c>, <c>region_name</c>,
/// <c>management_type_group</c>), the internal identifiers (<c>venue_id</c>), the raw facility type
/// codes (<c>ap_facility_types</c>, which the export ships no lookup for), the venue point/dataset
/// counts (<c>oa_point_count</c>, <c>oa_dataset_count</c>) and <c>is_primary_for_site</c>.
///
/// Every value is read exactly as published: nothing here is recomputed, rescaled or rounded, and a
/// blank cell becomes <c>null</c> or an empty list rather than a zero.
/// </remarks>
/// <param name="SiteId">Active Places site identifier. The only column required to be present.</param>
/// <param name="SiteName">Active Places site name, as published — upper case.</param>
/// <param name="Postcode">The site's postcode.</param>
/// <param name="LocalAuthorityCode">ONS code of the site's local authority, e.g. <c>E07000223</c>.</param>
/// <param name="LocalAuthorityName">Name of that local authority.</param>
/// <param name="OwnershipTypeGroup">Active Places ownership group, e.g. <c>Local Authority</c>, <c>Education</c>.</param>
/// <param name="SiteLat">Latitude of the site, as recorded by Active Places.</param>
/// <param name="SiteLng">Longitude of the site.</param>
/// <param name="ApFacilityCount">Facilities the site holds, per Active Places.</param>
/// <param name="OaLocationNames">Names the OpenActive venue is published under, one per clustered point that carried a name.</param>
/// <param name="OaLat">Latitude of the OpenActive venue — the centre of its point cluster.</param>
/// <param name="OaLng">Longitude of the OpenActive venue.</param>
/// <param name="OaDatasetUrls">Datasets publishing at that venue.</param>
/// <param name="OaPublisherNames">Publishers of those datasets.</param>
/// <param name="OaPostalCodes">Postcodes the venue's points carry, declared or recovered from a centroid.</param>
/// <param name="OaKinds">Opportunity kinds published at the venue, e.g. <c>ScheduledSession</c>.</param>
/// <param name="OaOpportunityCount">Opportunity items published at the venue.</param>
/// <param name="OaLocationJson">
/// The raw <c>location</c> JSON of each point in the venue's cluster, byte for byte as published, so a
/// row can be traced back to its opportunities with <c>WHERE TO_JSON_STRING(location) = '&lt;value&gt;'</c>.
/// </param>
/// <param name="DistanceMetres">Distance between site and venue, in metres.</param>
/// <param name="MatchMethod">Which channel matched: <c>spatial</c>, <c>spatial_and_postcode</c>, <c>spatial_centroid_only</c>, <c>postcode</c> or <c>name</c>.</param>
/// <param name="NameSimilarity">Name agreement, 0–1, for a <c>name</c> match; <c>null</c> on every other channel.</param>
/// <param name="SpatialMatch">Whether the venue is inside the spatial buffer.</param>
/// <param name="PostcodeMatch">Whether site and venue share a postcode.</param>
/// <param name="IsPrimaryForVenue">Whether this is the venue's strongest pair, ranked on channel then distance.</param>
/// <param name="IsMutualBest">Whether each side is the other's strongest pair — the most confident rows in the file.</param>
public sealed record ActivePlacesSiteMapping(
	string SiteId,
	string? SiteName,
	string? Postcode,
	string? LocalAuthorityCode,
	string? LocalAuthorityName,
	string? OwnershipTypeGroup,
	double? SiteLat,
	double? SiteLng,
	int? ApFacilityCount,
	IReadOnlyList<string> OaLocationNames,
	double? OaLat,
	double? OaLng,
	IReadOnlyList<string> OaDatasetUrls,
	IReadOnlyList<string> OaPublisherNames,
	IReadOnlyList<string> OaPostalCodes,
	IReadOnlyList<string> OaKinds,
	long? OaOpportunityCount,
	IReadOnlyList<string> OaLocationJson,
	double? DistanceMetres,
	string? MatchMethod,
	double? NameSimilarity,
	bool SpatialMatch,
	bool PostcodeMatch,
	bool IsPrimaryForVenue,
	bool IsMutualBest);

/// <summary>
/// The published coverage report, plus the two dates lifted out of it for the response envelope.
/// </summary>
/// <param name="Document">
/// The report exactly as published, passed through to the caller untouched. Deliberately not modelled
/// field by field: it is generated by the analysis job and carries its own <c>schema_version</c>, so a
/// new breakdown appearing upstream reaches the dashboard without a release here.
/// </param>
/// <param name="RunDate">The day the analysis ran — <c>run_date</c>, the report's own snapshot date.</param>
/// <param name="GeneratedAt">When it ran — <c>generated_at</c>, in UTC.</param>
public sealed record ActivePlacesCoverageReport(JsonElement Document, DateOnly RunDate, DateTime GeneratedAt);

/// <summary>
/// Reads the published <c>active_places_coverage.json</c>.
/// </summary>
public static class ActivePlacesCoverageReader
{
	/// <summary>
	/// Parses the report and lifts <c>run_date</c> and <c>generated_at</c> out of it.
	/// </summary>
	/// <remarks>
	/// Both dates fall back to today rather than failing: they label the answer, and a report whose
	/// figures are readable is worth serving even if the producer stopped stamping it. A body that is
	/// not a JSON object at all is a different matter and throws — that is a broken or redirected
	/// download, not a schema change.
	/// </remarks>
	/// <exception cref="InvalidDataException">The body is not a JSON object.</exception>
	public static ActivePlacesCoverageReport Read(string json)
	{
		JsonDocument parsed;
		try
		{
			parsed = JsonDocument.Parse(json);
		}
		catch (JsonException e)
		{
			throw new InvalidDataException("The Active Places coverage report is not valid JSON.", e);
		}

		using (parsed)
		{
			var root = parsed.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				throw new InvalidDataException(
					$"The Active Places coverage report is a JSON {root.ValueKind}, not an object.");
			}

			var generatedAt = ReadGeneratedAt(root) ?? DateTime.UtcNow;

			return new ActivePlacesCoverageReport(
				// Cloned because the JsonDocument backing it is disposed with this scope.
				root.Clone(),
				ReadRunDate(root) ?? DateOnly.FromDateTime(generatedAt),
				generatedAt);
		}
	}

	private static DateOnly? ReadRunDate(JsonElement root) =>
		root.TryGetProperty("run_date", out var value) &&
		value.ValueKind == JsonValueKind.String &&
		DateOnly.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var runDate)
			? runDate
			: null;

	private static DateTime? ReadGeneratedAt(JsonElement root) =>
		root.TryGetProperty("generated_at", out var value) &&
		value.ValueKind == JsonValueKind.String &&
		DateTime.TryParse(
			value.GetString(),
			CultureInfo.InvariantCulture,
			DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
			out var generatedAt)
			? DateTime.SpecifyKind(generatedAt, DateTimeKind.Utc)
			: null;
}

/// <summary>
/// Reads the published <c>site_oa_mapping.csv</c> into <see cref="ActivePlacesSiteMapping"/> rows.
/// </summary>
/// <remarks>
/// Columns are located by header name, not by position, so a column added or reordered upstream changes
/// nothing here, and a column dropped upstream leaves its field null rather than shifting every other
/// value along by one. Only <c>site_id</c> is required.
/// </remarks>
public static class ActivePlacesMappingParser
{
	/// <summary>
	/// The separator the analysis joins multi-valued cells with. <c>oa_location_names</c> pads it with
	/// spaces and the other columns do not, so the parts are trimmed either way.
	/// </summary>
	private const char ValueSeparator = '|';

	/// <summary>Parses the whole file. An empty body yields no rows.</summary>
	/// <exception cref="InvalidDataException">The file has no <c>site_id</c> column.</exception>
	public static List<ActivePlacesSiteMapping> Parse(string csv)
	{
		var records = Csv.Read(csv);
		if (records.Count == 0)
		{
			return [];
		}

		var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		var header = records[0];
		for (var i = 0; i < header.Count; i++)
		{
			// First wins: a duplicated header name is the producer's slip, and silently switching to the
			// later column would change what the endpoint returns without anything failing.
			columns.TryAdd(header[i].Trim(), i);
		}

		if (!columns.ContainsKey("site_id"))
		{
			throw new InvalidDataException(
				"The Active Places site mapping CSV has no site_id column; its header is: " +
				string.Join(", ", header));
		}

		var rows = new List<ActivePlacesSiteMapping>(records.Count - 1);
		for (var i = 1; i < records.Count; i++)
		{
			var record = records[i];
			// A trailing newline leaves a one-cell empty record; so does a blank line mid-file.
			if (record.Count == 1 && record[0].Length == 0)
			{
				continue;
			}

			var siteId = Text(record, columns, "site_id");
			if (siteId is null)
			{
				continue;
			}

			rows.Add(new ActivePlacesSiteMapping(
				siteId,
				Text(record, columns, "site_name"),
				Text(record, columns, "postcode"),
				Text(record, columns, "local_authority_code"),
				Text(record, columns, "local_authority_name"),
				Text(record, columns, "ownership_type_group"),
				Number(record, columns, "site_lat"),
				Number(record, columns, "site_lng"),
				Integer(record, columns, "ap_facility_count"),
				List(record, columns, "oa_location_names"),
				Number(record, columns, "oa_lat"),
				Number(record, columns, "oa_lng"),
				List(record, columns, "oa_dataset_urls"),
				List(record, columns, "oa_publisher_names"),
				List(record, columns, "oa_postal_codes"),
				List(record, columns, "oa_kinds"),
				Count(record, columns, "oa_opportunity_count"),
				JsonList(record, columns, "oa_location_json"),
				Number(record, columns, "distance_metres"),
				Text(record, columns, "match_method"),
				Number(record, columns, "name_similarity"),
				Flag(record, columns, "spatial_match"),
				Flag(record, columns, "postcode_match"),
				Flag(record, columns, "is_primary_for_venue"),
				Flag(record, columns, "is_mutual_best")));
		}

		return rows;
	}

	#region Cells

	/// <summary>The raw cell, or <c>null</c> when the column is absent, short or blank.</summary>
	private static string? Text(IReadOnlyList<string> record, Dictionary<string, int> columns, string name)
	{
		if (!columns.TryGetValue(name, out var index) || index >= record.Count)
		{
			return null;
		}

		var value = record[index];
		return string.IsNullOrWhiteSpace(value) ? null : value;
	}

	private static double? Number(IReadOnlyList<string> record, Dictionary<string, int> columns, string name) =>
		double.TryParse(Text(record, columns, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
			? value
			: null;

	private static int? Integer(IReadOnlyList<string> record, Dictionary<string, int> columns, string name) =>
		int.TryParse(Text(record, columns, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
			? value
			: null;

	private static long? Count(IReadOnlyList<string> record, Dictionary<string, int> columns, string name) =>
		long.TryParse(Text(record, columns, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
			? value
			: null;

	/// <summary>
	/// A boolean cell as written by the analysis (<c>True</c>/<c>False</c>). Anything else, including a
	/// blank, reads as <c>false</c>: these columns are derived from <c>match_method</c> and are always
	/// populated, so there is no third state to preserve.
	/// </summary>
	private static bool Flag(IReadOnlyList<string> record, Dictionary<string, int> columns, string name) =>
		bool.TryParse(Text(record, columns, name), out var value) && value;

	/// <summary>Splits a multi-valued cell, dropping blank parts. A blank cell gives an empty list.</summary>
	private static List<string> List(IReadOnlyList<string> record, Dictionary<string, int> columns, string name)
	{
		var value = Text(record, columns, name);
		if (value is null)
		{
			return [];
		}

		return value
			.Split(ValueSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.ToList();
	}

	/// <summary>
	/// Splits a cell holding several JSON documents joined with the separator, which
	/// <see cref="List"/> cannot do: a separator inside a JSON string — an address containing one —
	/// would cut a document in half. Splits only at nesting depth zero and outside strings, and keeps
	/// each document byte for byte so it can still be matched against the stored value.
	/// </summary>
	private static List<string> JsonList(IReadOnlyList<string> record, Dictionary<string, int> columns, string name)
	{
		var value = Text(record, columns, name);
		if (value is null)
		{
			return [];
		}

		var parts = new List<string>();
		var depth = 0;
		var inString = false;
		var escaped = false;
		var start = 0;

		for (var i = 0; i < value.Length; i++)
		{
			var c = value[i];

			if (escaped)
			{
				escaped = false;
			}
			else if (inString)
			{
				if (c == '\\')
				{
					escaped = true;
				}
				else if (c == '"')
				{
					inString = false;
				}
			}
			else if (c == '"')
			{
				inString = true;
			}
			else if (c is '{' or '[')
			{
				depth++;
			}
			else if (c is '}' or ']')
			{
				depth--;
			}
			else if (c == ValueSeparator && depth == 0)
			{
				Add(value[start..i]);
				start = i + 1;
			}
		}

		Add(value[start..]);

		return parts;

		void Add(string part)
		{
			var trimmed = part.Trim();
			if (trimmed.Length > 0)
			{
				parts.Add(trimmed);
			}
		}
	}

	#endregion

	/// <summary>
	/// The minimum of RFC 4180 the published file actually needs: quoted fields, doubled quotes inside
	/// them, and the newlines that appear inside <c>oa_location_names</c> — a line-by-line split would
	/// tear those rows in two.
	/// </summary>
	private static class Csv
	{
		public static List<List<string>> Read(string text)
		{
			var records = new List<List<string>>();
			if (text.Length == 0)
			{
				return records;
			}

			// A UTF-8 BOM would otherwise become part of the first header name.
			var start = text[0] == '﻿' ? 1 : 0;

			var record = new List<string>();
			var field = new StringBuilder();
			var quoted = false;

			for (var i = start; i < text.Length; i++)
			{
				var c = text[i];

				if (quoted)
				{
					if (c != '"')
					{
						field.Append(c);
					}
					else if (i + 1 < text.Length && text[i + 1] == '"')
					{
						field.Append('"');
						i++;
					}
					else
					{
						quoted = false;
					}

					continue;
				}

				switch (c)
				{
					case '"':
						quoted = true;
						break;
					case ',':
						record.Add(field.ToString());
						field.Clear();
						break;
					case '\r':
						// Swallowed: CRLF ends the record on the LF, and a lone CR is not a separator
						// anything in this file uses.
						break;
					case '\n':
						record.Add(field.ToString());
						field.Clear();
						records.Add(record);
						record = [];
						break;
					default:
						field.Append(c);
						break;
				}
			}

			// The last record only ends in a newline if the file happens to be terminated with one.
			if (field.Length > 0 || record.Count > 0)
			{
				record.Add(field.ToString());
				records.Add(record);
			}

			return records;
		}
	}
}

/// <summary>
/// Selects the mapping rows a request asked for, and puts them in the order the endpoint promises.
/// </summary>
/// <remarks>
/// Pure, and separate from the controller, because the combining rule is worth pinning: values within
/// one filter are OR'd and different filters are AND'd, as everywhere else on this API, but two of the
/// columns being filtered hold several values themselves — a row matches <c>publisher</c> when
/// <em>any</em> of its publishers is <em>any</em> of the requested ones.
/// </remarks>
public static class ActivePlacesMappingFilter
{
	/// <summary>
	/// Applies the filters and orders what survives. An empty filter collection matches everything.
	/// </summary>
	/// <param name="rows">Every published row.</param>
	/// <param name="siteIds">Active Places site identifiers, matched exactly.</param>
	/// <param name="localAuthorityCodes">ONS local authority codes, matched ignoring case.</param>
	/// <param name="publishers">Publisher names, matched exactly against any publisher on the row.</param>
	/// <param name="matchMethods">Match channels, matched ignoring case.</param>
	public static List<ActivePlacesSiteMapping> Apply(
		IEnumerable<ActivePlacesSiteMapping> rows,
		IReadOnlyCollection<string> siteIds,
		IReadOnlyCollection<string> localAuthorityCodes,
		IReadOnlyCollection<string> publishers,
		IReadOnlyCollection<string> matchMethods)
	{
		var wantedSites = new HashSet<string>(siteIds, StringComparer.Ordinal);
		var wantedAuthorities = new HashSet<string>(localAuthorityCodes, StringComparer.OrdinalIgnoreCase);
		var wantedPublishers = new HashSet<string>(publishers, StringComparer.Ordinal);
		var wantedMethods = new HashSet<string>(matchMethods, StringComparer.OrdinalIgnoreCase);

		return rows
			.Where(row =>
				(wantedSites.Count == 0 || wantedSites.Contains(row.SiteId)) &&
				(wantedAuthorities.Count == 0 ||
					(row.LocalAuthorityCode is not null && wantedAuthorities.Contains(row.LocalAuthorityCode))) &&
				(wantedPublishers.Count == 0 || row.OaPublisherNames.Any(wantedPublishers.Contains)) &&
				(wantedMethods.Count == 0 ||
					(row.MatchMethod is not null && wantedMethods.Contains(row.MatchMethod))))
			// A total order, so paging cannot drop or repeat a row: sites alphabetically, each site's
			// pairs nearest first, and the venue's location JSON — unique per venue — settling the rest.
			.OrderBy(row => row.SiteName ?? "", StringComparer.Ordinal)
			.ThenBy(row => row.SiteId, StringComparer.Ordinal)
			.ThenBy(row => row.DistanceMetres ?? double.MaxValue)
			.ThenBy(row => string.Join('|', row.OaLocationJson), StringComparer.Ordinal)
			.ToList();
	}
}
