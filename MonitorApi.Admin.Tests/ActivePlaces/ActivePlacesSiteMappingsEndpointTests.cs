using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MonitorApi.Models.Admin;

namespace MonitorApi.Admin.Tests.ActivePlaces;

/// <summary>
/// Live tests for <c>/admin/active-places-site-mappings</c>. Every parsing and filtering rule is pinned
/// by <see cref="ActivePlacesMappingParserTests"/> and <see cref="ActivePlacesMappingFilterTests"/>
/// against hand-written input; these check the wiring, the envelope, and the invariants that must hold
/// whatever the published report says on the day.
/// </summary>
/// <remarks>
/// Unlike the rest of this suite these need no BigQuery credentials — the endpoint reads no table — but
/// they do need the admin token and network access to wherever <c>ActivePlaces</c> is configured to
/// fetch from.
/// </remarks>
public class ActivePlacesSiteMappingsEndpointTests(AdminApiFixture fixture) : IClassFixture<AdminApiFixture>
{
	private const string Route = "/admin/active-places-site-mappings";

	/// <summary>The channels the analysis publishes. A new one here is a change worth noticing.</summary>
	private static readonly string[] MatchMethods =
		["spatial", "spatial_and_postcode", "spatial_centroid_only", "postcode", "name"];

	private readonly AdminApiFixture _fixture = fixture;

	private static readonly JsonSerializerOptions JsonOptions =
		new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

	private async Task<AdminPage<ActivePlacesSiteMappingRow>> Get(string query = "")
	{
		using var client = _fixture.CreateClient();
		var response = await client.GetAsync(_fixture.WithAdminToken(Route + query));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return (await response.Content.ReadFromJsonAsync<AdminPage<ActivePlacesSiteMappingRow>>(JsonOptions))!;
	}

	#region Envelope

	[Fact]
	public async Task ReturnsTheDocumentedEnvelope()
	{
		var page = await Get();

		Assert.NotNull(page.Data);
		Assert.Equal(1, page.Meta.Page);
		Assert.Equal(500, page.Meta.PageSize);
		Assert.True(page.Meta.Total >= page.Data.Count);
		Assert.Equal(DateTimeKind.Utc, page.Meta.GeneratedAt.Kind);
		Assert.True(page.Meta.SnapshotDate > DateOnly.MinValue);
	}

	/// <summary>
	/// Both endpoints describe one run of the analysis, so they must date themselves identically —
	/// the mapping endpoint takes its snapshot date from the coverage report for exactly that reason.
	/// </summary>
	[Fact]
	public async Task SnapshotDate_IsTheCoverageReportsRunDate()
	{
		using var client = _fixture.CreateClient();
		using var coverage = JsonDocument.Parse(
			await client.GetStringAsync(_fixture.WithAdminToken("/admin/active-places-coverage")));

		var page = await Get();

		Assert.Equal(
			coverage.RootElement.GetProperty("meta").GetProperty("snapshot_date").GetString(),
			page.Meta.SnapshotDate.ToString("yyyy-MM-dd"));
	}

	#endregion

	#region Rows

	[Fact]
	public async Task EveryRowIsInternallyConsistent()
	{
		var page = await Get("?page_size=1000");

		Assert.All(page.Data, row =>
		{
			Assert.NotEmpty(row.SiteId);

			// Arrays are always present, however little the venue published.
			Assert.NotNull(row.OaLocationNames);
			Assert.NotNull(row.OaDatasetUrls);
			Assert.NotNull(row.OaPublisherNames);
			Assert.NotNull(row.OaPostalCodes);
			Assert.NotNull(row.OaKinds);
			Assert.NotNull(row.OaLocationJson);

			Assert.True(row.DistanceMetres is null or >= 0, $"{row.SiteId} is {row.DistanceMetres}m away");
			Assert.True(row.OaOpportunityCount is null or >= 0);
			Assert.True(row.ApFacilityCount is null or >= 0);
			Assert.True(row.SiteLat is null or (>= 49 and <= 61), $"{row.SiteId} sits at {row.SiteLat}");
			Assert.True(row.OaLat is null or (>= 49 and <= 61));

			if (row.MatchMethod is not null)
			{
				Assert.Contains(row.MatchMethod, MatchMethods);
			}
		});
	}

	/// <summary>Names were only ever compared on the name channel, so a score anywhere else is a bug.</summary>
	[Fact]
	public async Task NameSimilarity_IsPresentOnlyForNameMatches()
	{
		var page = await Get("?page_size=1000");

		Assert.All(page.Data, row =>
		{
			if (row.MatchMethod == "name")
			{
				Assert.True(row.NameSimilarity is >= 0 and <= 1, $"{row.SiteId} scored {row.NameSimilarity}");
			}
			else
			{
				Assert.Null(row.NameSimilarity);
			}
		});
	}

	/// <summary>
	/// The two flags restate what the channel already says, and the dashboard filters on both, so they
	/// have to agree.
	/// </summary>
	[Fact]
	public async Task MatchFlagsAgreeWithTheChannel()
	{
		var page = await Get("?page_size=1000");

		Assert.All(page.Data, row =>
		{
			if (row.MatchMethod is "spatial" or "spatial_and_postcode" or "spatial_centroid_only")
			{
				Assert.True(row.SpatialMatch, $"{row.SiteId} matched by {row.MatchMethod} but is not spatial");
			}

			if (row.MatchMethod == "spatial_and_postcode")
			{
				Assert.True(row.PostcodeMatch);
			}

			if (row.MatchMethod == "postcode")
			{
				Assert.True(row.PostcodeMatch);
				Assert.False(row.SpatialMatch);
			}
		});
	}

	/// <summary>
	/// The value is the key back into <c>opportunities</c>, so it has to survive the round trip as
	/// valid JSON rather than as something reformatted on the way through.
	/// </summary>
	[Fact]
	public async Task LocationJson_IsUsableAsPublished()
	{
		var page = await Get("?page_size=1000");

		Assert.All(page.Data.SelectMany(row => row.OaLocationJson), json =>
		{
			using var parsed = JsonDocument.Parse(json);
			Assert.Equal(JsonValueKind.Object, parsed.RootElement.ValueKind);
		});
	}

	#endregion

	#region Paging

	[Fact]
	public async Task PagesAreDisjointAndCoverEveryRow()
	{
		var first = await Get("?page_size=200");
		var second = await Get("?page=2&page_size=200");

		Assert.Equal(first.Meta.Total, second.Meta.Total);
		if (first.Meta.Total <= 200)
		{
			return;
		}

		var firstKeys = first.Data.Select(Key).ToList();
		var secondKeys = second.Data.Select(Key).ToList();

		Assert.Equal(200, firstKeys.Count);
		Assert.Empty(firstKeys.Intersect(secondKeys));
		// The order is total, so the page boundary falls in the same place every time.
		Assert.True(
			string.CompareOrdinal(first.Data[^1].SiteName ?? "", second.Data[0].SiteName ?? "") <= 0);

		static string Key(ActivePlacesSiteMappingRow row) =>
			row.SiteId + "|" + row.DistanceMetres + "|" + string.Join(',', row.OaLocationJson);
	}

	[Fact]
	public async Task OutOfRangePagingIsClampedNotRejected()
	{
		var page = await Get("?page=0&page_size=99999");

		Assert.Equal(1, page.Meta.Page);
		Assert.Equal(1000, page.Meta.PageSize);
	}

	[Fact]
	public async Task APageBeyondTheEndIsEmptyButStillCounts()
	{
		var page = await Get("?page=100000&page_size=1000");

		Assert.Empty(page.Data);
		Assert.True(page.Meta.Total > 0);
	}

	#endregion

	#region Filters

	[Fact]
	public async Task MatchMethodFilter_ReturnsOnlyThatChannel()
	{
		var page = await Get("?match_method=name&page_size=1000");

		Assert.NotEmpty(page.Data);
		Assert.All(page.Data, row => Assert.Equal("name", row.MatchMethod));
		Assert.True(page.Meta.Total < (await Get()).Meta.Total);
	}

	[Fact]
	public async Task PublisherFilter_ReturnsOnlyRowsThatPublisherReaches()
	{
		var publisher = (await Get("?page_size=1000")).Data
			.SelectMany(row => row.OaPublisherNames)
			.First();

		var page = await Get("?publisher=" + Uri.EscapeDataString(publisher) + "&page_size=1000");

		Assert.NotEmpty(page.Data);
		Assert.All(page.Data, row => Assert.Contains(publisher, row.OaPublisherNames));
	}

	[Fact]
	public async Task SiteAndLocalAuthorityFilters_NarrowToThatSiteAndArea()
	{
		var sample = (await Get()).Data.First(row => row.LocalAuthorityCode is not null);

		var bySite = await Get("?site_id=" + Uri.EscapeDataString(sample.SiteId));
		Assert.NotEmpty(bySite.Data);
		Assert.All(bySite.Data, row => Assert.Equal(sample.SiteId, row.SiteId));

		var byArea = await Get("?local_authority_code=" + Uri.EscapeDataString(sample.LocalAuthorityCode!));
		Assert.NotEmpty(byArea.Data);
		Assert.All(byArea.Data, row => Assert.Equal(sample.LocalAuthorityCode, row.LocalAuthorityCode));
		Assert.True(byArea.Meta.Total >= bySite.Meta.Total);
	}

	/// <summary>Both the repeated and the comma-separated form, as every filter on this API accepts.</summary>
	[Fact]
	public async Task MultiValuedFilters_AcceptBothForms()
	{
		var repeated = await Get("?match_method=name&match_method=postcode&page_size=1000");
		var commaSeparated = await Get("?match_method=name,postcode&page_size=1000");

		Assert.Equal(repeated.Meta.Total, commaSeparated.Meta.Total);
		Assert.All(repeated.Data, row => Assert.Contains(row.MatchMethod, new[] { "name", "postcode" }));

		// OR within one parameter: the pair is at least as large as either on its own.
		var justName = await Get("?match_method=name");
		Assert.True(repeated.Meta.Total >= justName.Meta.Total);
	}

	[Fact]
	public async Task DifferentFilters_AreAnded()
	{
		var byMethod = await Get("?match_method=spatial");
		var sample = byMethod.Data.First(row => row.LocalAuthorityCode is not null);

		var both = await Get(
			"?match_method=spatial&local_authority_code=" + Uri.EscapeDataString(sample.LocalAuthorityCode!));

		Assert.All(both.Data, row =>
		{
			Assert.Equal("spatial", row.MatchMethod);
			Assert.Equal(sample.LocalAuthorityCode, row.LocalAuthorityCode);
		});
		Assert.True(both.Meta.Total <= byMethod.Meta.Total);
	}

	[Fact]
	public async Task AFilterMatchingNothing_IsAnEmptyPageNotAnError()
	{
		var page = await Get("?publisher=" + Uri.EscapeDataString("No Such Publisher Exists"));

		Assert.Empty(page.Data);
		Assert.Equal(0, page.Meta.Total);
		Assert.True(page.Meta.SnapshotDate > DateOnly.MinValue);
	}

	#endregion

	#region Agreement with the coverage report

	/// <summary>
	/// The two endpoints are two views of one analysis run: the mapping file is the pairs the report
	/// counts, so the headline figures have to be reproducible from the rows.
	/// </summary>
	[Fact]
	public async Task RowsReproduceTheReportsHeadlineCounts()
	{
		using var client = _fixture.CreateClient();
		using var coverage = JsonDocument.Parse(
			await client.GetStringAsync(_fixture.WithAdminToken("/admin/active-places-coverage")));

		if (!coverage.RootElement.GetProperty("data").TryGetProperty("headline", out var headline))
		{
			return;
		}

		var sites = new HashSet<string>(StringComparer.Ordinal);
		var pairs = 0;
		for (var page = 1; ; page++)
		{
			var rows = (await Get($"?page={page}&page_size=1000")).Data;
			if (rows.Count == 0)
			{
				break;
			}

			pairs += rows.Count;
			foreach (var row in rows)
			{
				sites.Add(row.SiteId);
			}
		}

		if (headline.TryGetProperty("pairs", out var reportedPairs))
		{
			Assert.Equal(reportedPairs.GetInt32(), pairs);
		}

		if (headline.TryGetProperty("sites_matched", out var reportedSites))
		{
			Assert.Equal(reportedSites.GetInt32(), sites.Count);
		}
	}

	#endregion
}
