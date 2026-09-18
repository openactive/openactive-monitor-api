using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.ActivePlaces;

/// <summary>
/// Deterministic tests for which mapping rows a request selects and the order they come back in. The
/// combining rule is the API's throughout — values within one filter are OR'd, different filters are
/// AND'd — but two of the columns hold several values themselves, which is what these pin.
/// </summary>
public class ActivePlacesMappingFilterTests
{
	/// <summary>
	/// A pair with everything defaulted, so each test states only the fields it is about.
	/// </summary>
	private static ActivePlacesSiteMapping Pair(
		string siteId = "1",
		string? siteName = "SOMEWHERE",
		string? localAuthorityCode = "E07000223",
		string[]? publishers = null,
		string? matchMethod = "spatial",
		double? distanceMetres = 100,
		string? locationJson = null) =>
		new(
			siteId,
			siteName,
			Postcode: null,
			localAuthorityCode,
			LocalAuthorityName: null,
			OwnershipTypeGroup: null,
			SiteLat: null,
			SiteLng: null,
			ApFacilityCount: null,
			OaLocationNames: [],
			OaLat: null,
			OaLng: null,
			OaDatasetUrls: [],
			OaPublisherNames: publishers ?? ["England Netball"],
			OaPostalCodes: [],
			OaKinds: [],
			OaOpportunityCount: null,
			OaLocationJson: locationJson is null ? [] : [locationJson],
			distanceMetres,
			matchMethod,
			NameSimilarity: null,
			SpatialMatch: true,
			PostcodeMatch: false,
			IsPrimaryForVenue: true,
			IsMutualBest: true);

	private static List<ActivePlacesSiteMapping> Apply(
		IEnumerable<ActivePlacesSiteMapping> rows,
		string[]? siteIds = null,
		string[]? localAuthorities = null,
		string[]? publishers = null,
		string[]? matchMethods = null) =>
		ActivePlacesMappingFilter.Apply(rows, siteIds ?? [], localAuthorities ?? [], publishers ?? [], matchMethods ?? []);

	#region Filters

	[Fact]
	public void NoFilters_KeepEverything()
	{
		var rows = new[] { Pair(siteId: "1"), Pair(siteId: "2") };

		Assert.Equal(2, Apply(rows).Count);
	}

	/// <summary>A row matches when any of its publishers is any of the requested ones.</summary>
	[Fact]
	public void Publisher_MatchesAnyPublisherOnTheRow()
	{
		var rows = new[]
		{
			Pair(siteId: "1", publishers: ["England Netball", "Playwaze", "TeamUp"]),
			Pair(siteId: "2", publishers: ["GLL"]),
			Pair(siteId: "3", publishers: []),
		};

		Assert.Equal(["1"], Apply(rows, publishers: ["Playwaze"]).Select(r => r.SiteId));
		Assert.Equal(["1", "2"], Apply(rows, publishers: ["Playwaze", "GLL"]).Select(r => r.SiteId));
		Assert.Empty(Apply(rows, publishers: ["Better"]));
	}

	/// <summary>Publisher names are matched exactly, as everywhere else on this API.</summary>
	[Fact]
	public void Publisher_IsMatchedExactly()
	{
		var rows = new[] { Pair(publishers: ["England Netball"]) };

		Assert.Empty(Apply(rows, publishers: ["england netball"]));
		Assert.Empty(Apply(rows, publishers: ["England"]));
	}

	/// <summary>
	/// ONS codes and the channel names are identifiers with one published spelling, so case is not worth
	/// failing a request over.
	/// </summary>
	[Fact]
	public void LocalAuthorityAndMatchMethod_IgnoreCase()
	{
		var rows = new[] { Pair(localAuthorityCode: "E07000223", matchMethod: "spatial_and_postcode") };

		Assert.Single(Apply(rows, localAuthorities: ["e07000223"]));
		Assert.Single(Apply(rows, matchMethods: ["SPATIAL_AND_POSTCODE"]));
	}

	[Fact]
	public void DifferentFilters_AreAnded()
	{
		var rows = new[]
		{
			Pair(siteId: "1", publishers: ["GLL"], matchMethod: "spatial"),
			Pair(siteId: "2", publishers: ["GLL"], matchMethod: "name"),
			Pair(siteId: "3", publishers: ["Better"], matchMethod: "name"),
		};

		Assert.Equal(["2"], Apply(rows, publishers: ["GLL"], matchMethods: ["name"]).Select(r => r.SiteId));
	}

	[Fact]
	public void SiteId_IsMatchedExactly()
	{
		var rows = new[] { Pair(siteId: "1011203"), Pair(siteId: "20001851") };

		Assert.Equal(["1011203"], Apply(rows, siteIds: ["1011203"]).Select(r => r.SiteId));
		Assert.Empty(Apply(rows, siteIds: ["101"]));
	}

	/// <summary>
	/// A filtered column can be blank in the published file, and a row with nothing there matches
	/// nothing rather than everything.
	/// </summary>
	[Fact]
	public void RowsMissingTheFilteredValue_AreExcluded()
	{
		var rows = new[] { Pair(localAuthorityCode: null, matchMethod: null) };

		Assert.Empty(Apply(rows, localAuthorities: ["E07000223"]));
		Assert.Empty(Apply(rows, matchMethods: ["spatial"]));
		Assert.Single(Apply(rows));
	}

	#endregion

	#region Order

	/// <summary>
	/// Paging needs a total order: sites alphabetically, each site's pairs nearest first, and the
	/// venue's location JSON settling anything still tied.
	/// </summary>
	[Fact]
	public void RowsAreOrderedBySiteThenDistance()
	{
		var rows = new[]
		{
			Pair(siteId: "2", siteName: "BEACON CENTRE", distanceMetres: 10),
			Pair(siteId: "1", siteName: "ALPHA LEISURE", distanceMetres: 300),
			Pair(siteId: "1", siteName: "ALPHA LEISURE", distanceMetres: 12),
		};

		Assert.Equal(
			[("1", 12d), ("1", 300d), ("2", 10d)],
			Apply(rows).Select(r => (r.SiteId, r.DistanceMetres!.Value)));
	}

	[Fact]
	public void RowsTiedOnSiteAndDistance_AreOrderedByVenue()
	{
		var rows = new[]
		{
			Pair(locationJson: """{"latitude":2}"""),
			Pair(locationJson: """{"latitude":1}"""),
		};

		Assert.Equal(
			["""{"latitude":1}""", """{"latitude":2}"""],
			Apply(rows).Select(r => r.OaLocationJson.Single()));
	}

	/// <summary>A row with no distance sorts last within its site rather than first.</summary>
	[Fact]
	public void RowsWithoutADistance_SortLastWithinTheirSite()
	{
		var rows = new[]
		{
			Pair(distanceMetres: null, locationJson: "a"),
			Pair(distanceMetres: 900, locationJson: "b"),
		};

		Assert.Equal(["b", "a"], Apply(rows).Select(r => r.OaLocationJson.Single()));
	}

	#endregion
}
