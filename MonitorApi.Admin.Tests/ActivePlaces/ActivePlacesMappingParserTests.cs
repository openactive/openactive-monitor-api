using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.ActivePlaces;

/// <summary>
/// Deterministic tests for reading the published <c>site_oa_mapping.csv</c>, run against hand-written
/// files. These need no BigQuery credentials, no admin token and no network — every parsing rule the
/// endpoint relies on is pinned here, so the live endpoint tests only have to check the wiring and the
/// envelope.
/// </summary>
public class ActivePlacesMappingParserTests
{
	/// <summary>
	/// The published header, in the published order. Tests state a whole row against it so that what
	/// each assertion is about stays visible.
	/// </summary>
	private const string Header =
		"site_id,site_name,postcode,local_authority_code,local_authority_name,region_code,region_name," +
		"ownership_type_group,management_type_group,site_lat,site_lng,ap_facility_count,ap_facility_types," +
		"venue_id,oa_location_names,oa_lat,oa_lng,oa_point_count,oa_dataset_count,oa_dataset_urls," +
		"oa_publisher_names,oa_postal_codes,oa_kinds,oa_opportunity_count,oa_location_json,distance_metres," +
		"match_method,name_similarity,spatial_match,postcode_match,is_primary_for_site,is_primary_for_venue," +
		"is_mutual_best";

	private const string Row =
		"1011203,ADUR INDOOR BOWLING CLUB LTD,BN42 4NT,E07000223,Adur,E12000008,South East,Commercial," +
		"Commercial,50.83428,-0.2271475,1,3,9997,srwa Indoor double courts,50.8343,-0.2243,1,1," +
		"https://data.englandnetball.co.uk/,England Netball,BN42 4NT,ScheduledSession,50," +
		"\"{\"\"latitude\"\":50.834343,\"\"longitude\"\":-0.224378}\",195.2,spatial,,True,False,True,False,False";

	private static ActivePlacesSiteMapping ParseOne(string row) =>
		Assert.Single(ActivePlacesMappingParser.Parse(Header + "\n" + row));

	#region Columns

	[Fact]
	public void ReadsEveryServedColumn()
	{
		var mapping = ParseOne(Row);

		Assert.Equal("1011203", mapping.SiteId);
		Assert.Equal("ADUR INDOOR BOWLING CLUB LTD", mapping.SiteName);
		Assert.Equal("BN42 4NT", mapping.Postcode);
		Assert.Equal("E07000223", mapping.LocalAuthorityCode);
		Assert.Equal("Adur", mapping.LocalAuthorityName);
		Assert.Equal("Commercial", mapping.OwnershipTypeGroup);
		Assert.Equal(50.83428, mapping.SiteLat);
		Assert.Equal(-0.2271475, mapping.SiteLng);
		Assert.Equal(1, mapping.ApFacilityCount);
		Assert.Equal(["srwa Indoor double courts"], mapping.OaLocationNames);
		Assert.Equal(50.8343, mapping.OaLat);
		Assert.Equal(-0.2243, mapping.OaLng);
		Assert.Equal(["https://data.englandnetball.co.uk/"], mapping.OaDatasetUrls);
		Assert.Equal(["England Netball"], mapping.OaPublisherNames);
		Assert.Equal(["BN42 4NT"], mapping.OaPostalCodes);
		Assert.Equal(["ScheduledSession"], mapping.OaKinds);
		Assert.Equal(50, mapping.OaOpportunityCount);
		Assert.Equal(["""{"latitude":50.834343,"longitude":-0.224378}"""], mapping.OaLocationJson);
		Assert.Equal(195.2, mapping.DistanceMetres);
		Assert.Equal("spatial", mapping.MatchMethod);
		Assert.Null(mapping.NameSimilarity);
		Assert.True(mapping.SpatialMatch);
		Assert.False(mapping.PostcodeMatch);
		Assert.False(mapping.IsPrimaryForVenue);
		Assert.False(mapping.IsMutualBest);
	}

	/// <summary>
	/// Columns are found by header name, so a column added, removed or moved upstream does not shift
	/// every other value along by one.
	/// </summary>
	[Fact]
	public void LocatesColumnsByNameNotPosition()
	{
		var rows = ActivePlacesMappingParser.Parse(
			"match_method,unknown_new_column,site_id,distance_metres\nname,ignored,42,7.5");

		var mapping = Assert.Single(rows);
		Assert.Equal("42", mapping.SiteId);
		Assert.Equal("name", mapping.MatchMethod);
		Assert.Equal(7.5, mapping.DistanceMetres);
		// Absent columns leave their fields empty rather than failing the whole file.
		Assert.Null(mapping.SiteName);
		Assert.Empty(mapping.OaPublisherNames);
	}

	[Fact]
	public void WithoutASiteIdColumn_Throws()
	{
		var e = Assert.Throws<InvalidDataException>(() =>
			ActivePlacesMappingParser.Parse("site_name,distance_metres\nSOMEWHERE,10"));

		Assert.Contains("site_id", e.Message);
	}

	#endregion

	#region Blanks

	/// <summary>A blank cell is "not published", so it becomes null or an empty list — never a zero.</summary>
	[Fact]
	public void BlankCells_AreNullOrEmpty_NotZero()
	{
		var mapping = ParseOne("1011203,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,");

		Assert.Equal("1011203", mapping.SiteId);
		Assert.Null(mapping.SiteName);
		Assert.Null(mapping.SiteLat);
		Assert.Null(mapping.ApFacilityCount);
		Assert.Null(mapping.OaOpportunityCount);
		Assert.Null(mapping.DistanceMetres);
		Assert.Null(mapping.NameSimilarity);
		Assert.Null(mapping.MatchMethod);
		Assert.Empty(mapping.OaLocationNames);
		Assert.Empty(mapping.OaPostalCodes);
		Assert.Empty(mapping.OaLocationJson);
	}

	/// <summary>
	/// The flags are derived from <c>match_method</c> upstream and always written, so they have no third
	/// state to preserve: anything unreadable is false.
	/// </summary>
	[Theory]
	[InlineData("True", true)]
	[InlineData("true", true)]
	[InlineData("False", false)]
	[InlineData("", false)]
	[InlineData("yes", false)]
	public void Flags_ReadAsWrittenAndDefaultToFalse(string cell, bool expected)
	{
		var mapping = ParseOne($"1,,,,,,,,,,,,,,,,,,,,,,,,,,,,{cell},{cell},True,{cell},{cell}");

		Assert.Equal(expected, mapping.SpatialMatch);
		Assert.Equal(expected, mapping.PostcodeMatch);
		Assert.Equal(expected, mapping.IsPrimaryForVenue);
		Assert.Equal(expected, mapping.IsMutualBest);
	}

	[Fact]
	public void NameSimilarity_IsReadOnlyWhenPublished()
	{
		Assert.Equal(0.8333333333333333, ParseOne(Row.Replace(",spatial,,", ",name,0.8333333333333333,")).NameSimilarity);
		Assert.Null(ParseOne(Row).NameSimilarity);
	}

	#endregion

	#region Multi-valued cells

	[Fact]
	public void PipeJoinedCells_BecomeLists()
	{
		var rows = ActivePlacesMappingParser.Parse(
			"site_id,oa_publisher_names,oa_kinds,oa_postal_codes\n" +
			"1,England Netball|Playwaze|TeamUp,ScheduledSession|SessionSeries,NW4 1PX|NW41PX");

		var mapping = Assert.Single(rows);
		Assert.Equal(["England Netball", "Playwaze", "TeamUp"], mapping.OaPublisherNames);
		Assert.Equal(["ScheduledSession", "SessionSeries"], mapping.OaKinds);
		Assert.Equal(["NW4 1PX", "NW41PX"], mapping.OaPostalCodes);
	}

	/// <summary>
	/// <c>oa_location_names</c> pads the separator with spaces where the other columns do not, and the
	/// analysis leaves an empty part where a clustered point carried no name.
	/// </summary>
	[Fact]
	public void LocationNames_AreTrimmedAndBlankPartsDropped()
	{
		var rows = ActivePlacesMappingParser.Parse(
			"site_id,oa_location_names\n1,Ripley Leisure Centre | Ripley Studio |");

		Assert.Equal(["Ripley Leisure Centre", "Ripley Studio"], Assert.Single(rows).OaLocationNames);
	}

	/// <summary>
	/// The JSON column cannot be split like the others: publishers put addresses in <c>location</c>, and
	/// an address containing a pipe would cut a document in half.
	/// </summary>
	[Fact]
	public void LocationJson_SplitsBetweenDocumentsNotInsideThem()
	{
		var withPipeInside = """{"address":"Unit 3 | Rear of 12 High St","latitude":53.0,"longitude":-1.4}""";
		var second = """{"latitude":53.047151,"longitude":-1.405868}""";

		var rows = ActivePlacesMappingParser.Parse(
			"site_id,oa_location_json\n1,\"" + (withPipeInside + "|" + second).Replace("\"", "\"\"") + "\"");

		Assert.Equal([withPipeInside, second], Assert.Single(rows).OaLocationJson);
	}

	/// <summary>
	/// An escaped quote inside a JSON string must not be read as the string ending, or the following
	/// separator would be taken for a document boundary.
	/// </summary>
	[Fact]
	public void LocationJson_HonoursEscapedQuotesInsideStrings()
	{
		var awkward = """{"address":"Miners\" Welfare | Annexe","latitude":53.0}""";

		var rows = ActivePlacesMappingParser.Parse(
			"site_id,oa_location_json\n1,\"" + awkward.Replace("\"", "\"\"") + "\"");

		Assert.Equal([awkward], Assert.Single(rows).OaLocationJson);
	}

	/// <summary>
	/// Kept byte for byte, because the value is the lookup key back into <c>opportunities</c> —
	/// re-serialising it would stop <c>TO_JSON_STRING(location)</c> matching.
	/// </summary>
	[Fact]
	public void LocationJson_IsNotReformatted()
	{
		var published = """{"longitude":-1.40, "latitude":53.0,"postal_code":"DE75 7DT"}""";

		var rows = ActivePlacesMappingParser.Parse(
			"site_id,oa_location_json\n1,\"" + published.Replace("\"", "\"\"") + "\"");

		Assert.Equal([published], Assert.Single(rows).OaLocationJson);
	}

	#endregion

	#region File shape

	/// <summary>
	/// Some publishers put the whole address in the venue name, newlines and all, so the file cannot be
	/// read a line at a time.
	/// </summary>
	[Fact]
	public void QuotedFields_MayContainNewlinesCommasAndQuotes()
	{
		var rows = ActivePlacesMappingParser.Parse(
			"site_id,oa_location_names,match_method\n" +
			"1,\"Shoreham - Quayside Centre\nUpper Kingston Lane, next left\n\"\"The Barn\"\"\",spatial\n" +
			"2,Elsewhere,name");

		Assert.Equal(2, rows.Count);
		Assert.Equal(
			["Shoreham - Quayside Centre\nUpper Kingston Lane, next left\n\"The Barn\""],
			rows[0].OaLocationNames);
		Assert.Equal("spatial", rows[0].MatchMethod);
		Assert.Equal("2", rows[1].SiteId);
	}

	[Theory]
	[InlineData("\n")]
	[InlineData("\r\n")]
	public void BothLineEndings_AreRead(string newline)
	{
		var rows = ActivePlacesMappingParser.Parse(
			string.Join(newline, "site_id,match_method", "1,spatial", "2,name") + newline);

		Assert.Equal(["1", "2"], rows.Select(r => r.SiteId));
	}

	[Fact]
	public void EmptyOrHeaderOnlyFiles_YieldNoRows()
	{
		Assert.Empty(ActivePlacesMappingParser.Parse(""));
		Assert.Empty(ActivePlacesMappingParser.Parse(Header));
		Assert.Empty(ActivePlacesMappingParser.Parse(Header + "\n"));
		Assert.Empty(ActivePlacesMappingParser.Parse(Header + "\n\n"));
	}

	/// <summary>A byte order mark would otherwise become part of the first header name.</summary>
	[Fact]
	public void ByteOrderMark_IsIgnored()
	{
		var rows = ActivePlacesMappingParser.Parse("﻿site_id,match_method\n1,spatial");

		Assert.Equal("1", Assert.Single(rows).SiteId);
	}

	/// <summary>A row without a site is not a pair, so it is skipped rather than reported as one.</summary>
	[Fact]
	public void RowsWithoutASiteId_AreSkipped()
	{
		var rows = ActivePlacesMappingParser.Parse("site_id,match_method\n,spatial\n1,name");

		Assert.Equal("1", Assert.Single(rows).SiteId);
	}

	/// <summary>A short row is missing values, not misaligned — the columns it does have still read.</summary>
	[Fact]
	public void ShortRows_KeepTheValuesTheyHave()
	{
		var rows = ActivePlacesMappingParser.Parse("site_id,site_name,distance_metres\n1,SOMEWHERE");

		var mapping = Assert.Single(rows);
		Assert.Equal("SOMEWHERE", mapping.SiteName);
		Assert.Null(mapping.DistanceMetres);
	}

	#endregion
}
