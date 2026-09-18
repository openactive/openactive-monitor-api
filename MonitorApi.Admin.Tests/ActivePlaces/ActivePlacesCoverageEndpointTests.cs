using System.Net;
using System.Text.Json;

namespace MonitorApi.Admin.Tests.ActivePlaces;

/// <summary>
/// Live tests for <c>/admin/active-places-coverage</c>. The report is passed through unmodelled, so
/// these check the envelope around it, the dates lifted out of it, and that the document the dashboard
/// reads is internally consistent — never that a figure has a particular value.
/// </summary>
/// <remarks>
/// Deliberately navigated with <see cref="JsonDocument"/> rather than deserialised into a type: that is
/// how the dashboard has to read it, and a test that modelled the report would defeat the point of not
/// modelling it.
/// </remarks>
public class ActivePlacesCoverageEndpointTests(AdminApiFixture fixture) : IClassFixture<AdminApiFixture>
{
	private const string Route = "/admin/active-places-coverage";

	private readonly AdminApiFixture _fixture = fixture;

	private async Task<JsonDocument> Get()
	{
		using var client = _fixture.CreateClient();
		var response = await client.GetAsync(_fixture.WithAdminToken(Route));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
	}

	#region Envelope

	/// <summary>
	/// The single-document envelope: the same <c>meta</c> as every paged endpoint, with the paging
	/// fields fixed at one row on one page.
	/// </summary>
	[Fact]
	public async Task ReturnsTheDocumentEnvelope()
	{
		using var response = await Get();
		var meta = response.RootElement.GetProperty("meta");

		Assert.Equal(JsonValueKind.Object, response.RootElement.GetProperty("data").ValueKind);
		Assert.Equal(1, meta.GetProperty("page").GetInt32());
		Assert.Equal(1, meta.GetProperty("page_size").GetInt32());
		Assert.Equal(1, meta.GetProperty("total").GetInt32());
		Assert.EndsWith("Z", meta.GetProperty("generated_at").GetString());
	}

	/// <summary>
	/// The one endpoint on this surface whose <c>generated_at</c> is the data's own: it says when the
	/// analysis ran, not when the response was assembled.
	/// </summary>
	[Fact]
	public async Task MetaDatesComeFromTheReport()
	{
		using var response = await Get();
		var data = response.RootElement.GetProperty("data");
		var meta = response.RootElement.GetProperty("meta");

		Assert.Equal(
			data.GetProperty("run_date").GetString(),
			meta.GetProperty("snapshot_date").GetString());
		Assert.Equal(
			data.GetProperty("generated_at").GetDateTimeOffset().UtcDateTime,
			meta.GetProperty("generated_at").GetDateTime());
	}

	#endregion

	#region The report

	[Fact]
	public async Task CarriesTheSectionsTheDashboardReads()
	{
		using var response = await Get();
		var data = response.RootElement.GetProperty("data");

		Assert.True(data.GetProperty("schema_version").GetInt32() >= 1);

		foreach (var section in new[]
		{
			"source", "parameters", "headline", "channels", "distance_sensitivity", "coverage_by_region",
			"coverage_by_local_authority", "coverage_by_ownership", "coverage_by_management",
			"coverage_by_facility_type", "publishers", "coordinate_provenance", "unmatched", "data_quality",
		})
		{
			Assert.True(data.TryGetProperty(section, out var value), $"the report has no {section}");
			Assert.NotEqual(JsonValueKind.Null, value.ValueKind);
		}
	}

	[Fact]
	public async Task HeadlineFiguresAddUp()
	{
		using var response = await Get();
		var headline = response.RootElement.GetProperty("data").GetProperty("headline");

		var total = headline.GetProperty("sites_total").GetInt32();
		var matched = headline.GetProperty("sites_matched").GetInt32();
		var missing = headline.GetProperty("sites_missing").GetInt32();

		Assert.True(total > 0);
		Assert.Equal(total, matched + missing);
		Assert.Equal(
			Math.Round(matched * 100.0 / total, 1),
			headline.GetProperty("coverage_pct").GetDouble(),
			1);

		Assert.Equal(
			headline.GetProperty("venues_total").GetInt32(),
			headline.GetProperty("venues_matched").GetInt32() + headline.GetProperty("venues_unmatched").GetInt32());
	}

	/// <summary>
	/// Every per-area breakdown is the same four counts cut a different way, so each row has to be
	/// internally consistent and no cut may claim more matched sites than the headline.
	/// </summary>
	[Theory]
	[InlineData("coverage_by_region")]
	[InlineData("coverage_by_local_authority")]
	[InlineData("coverage_by_ownership")]
	[InlineData("coverage_by_management")]
	[InlineData("coverage_by_facility_type")]
	public async Task BreakdownRowsAreInternallyConsistent(string section)
	{
		using var response = await Get();
		var data = response.RootElement.GetProperty("data");
		var rows = data.GetProperty(section);

		Assert.Equal(JsonValueKind.Array, rows.ValueKind);
		Assert.NotEmpty(rows.EnumerateArray());

		foreach (var row in rows.EnumerateArray())
		{
			var total = row.GetProperty("sites_total").GetInt32();
			var matched = row.GetProperty("sites_matched").GetInt32();

			Assert.True(total > 0);
			Assert.Equal(total, matched + row.GetProperty("sites_missing").GetInt32());
			Assert.InRange(row.GetProperty("coverage_pct").GetDouble(), 0, 100);
		}
	}

	/// <summary>
	/// The sensitivity table is the report's central caveat, so it must actually contain the buffer the
	/// analysis ran with, and coverage must rise with the threshold.
	/// </summary>
	[Fact]
	public async Task DistanceSensitivityIsMonotonicAndFlagsTheBufferInUse()
	{
		using var response = await Get();
		var data = response.RootElement.GetProperty("data");

		var thresholds = data.GetProperty("distance_sensitivity").EnumerateArray().ToList();
		var configured = Assert.Single(thresholds.Where(t => t.GetProperty("is_configured_buffer").GetBoolean()));

		Assert.Equal(
			data.GetProperty("parameters").GetProperty("buffer_metres").GetDouble(),
			configured.GetProperty("threshold_metres").GetDouble());

		for (var i = 1; i < thresholds.Count; i++)
		{
			Assert.True(
				thresholds[i].GetProperty("threshold_metres").GetDouble() >
				thresholds[i - 1].GetProperty("threshold_metres").GetDouble());
			Assert.True(
				thresholds[i].GetProperty("sites_matched").GetInt32() >=
				thresholds[i - 1].GetProperty("sites_matched").GetInt32());
		}
	}

	#endregion
}
