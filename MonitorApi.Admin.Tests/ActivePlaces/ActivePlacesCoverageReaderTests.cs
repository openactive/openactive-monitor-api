using System.Text.Json;
using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.ActivePlaces;

/// <summary>
/// Deterministic tests for reading the published <c>active_places_coverage.json</c>. The report itself
/// is passed through unmodelled, so what is pinned here is the passing through — and the two dates
/// lifted out of it, which are what the response envelope is labelled with.
/// </summary>
public class ActivePlacesCoverageReaderTests
{
	private const string Report = """
		{
		  "schema_version": 1,
		  "generated_at": "2026-09-17T13:07:34+00:00",
		  "run_date": "2026-09-17",
		  "headline": { "coverage_pct": 26.4, "sites_total": 27857 }
		}
		""";

	[Fact]
	public void ReadsTheReportDates()
	{
		var report = ActivePlacesCoverageReader.Read(Report);

		Assert.Equal(new DateOnly(2026, 9, 17), report.RunDate);
		Assert.Equal(new DateTime(2026, 9, 17, 13, 7, 34, DateTimeKind.Utc), report.GeneratedAt);
		Assert.Equal(DateTimeKind.Utc, report.GeneratedAt.Kind);
	}

	/// <summary>An offset stamp is normalised, so <c>generated_at</c> is always comparable as UTC.</summary>
	[Fact]
	public void GeneratedAt_IsConvertedToUtc()
	{
		var report = ActivePlacesCoverageReader.Read("""{"generated_at": "2026-09-17T14:07:34+01:00"}""");

		Assert.Equal(new DateTime(2026, 9, 17, 13, 7, 34, DateTimeKind.Utc), report.GeneratedAt);
	}

	[Fact]
	public void ReportIsPassedThroughUnchanged()
	{
		var report = ActivePlacesCoverageReader.Read(Report);

		Assert.Equal(1, report.Document.GetProperty("schema_version").GetInt32());
		Assert.Equal(26.4, report.Document.GetProperty("headline").GetProperty("coverage_pct").GetDouble());
		Assert.Equal(27857, report.Document.GetProperty("headline").GetProperty("sites_total").GetInt32());
	}

	/// <summary>
	/// The document outlives the parse, so a caller reading it after the reader has returned must not be
	/// reading disposed memory.
	/// </summary>
	[Fact]
	public void ReportSurvivesTheParse()
	{
		var document = ActivePlacesCoverageReader.Read(Report).Document;

		Assert.Equal(JsonValueKind.Object, document.ValueKind);
		Assert.Contains("coverage_pct", document.GetRawText());
	}

	/// <summary>
	/// A section added upstream reaches the dashboard without a release here — the point of not
	/// modelling the report field by field.
	/// </summary>
	[Fact]
	public void UnknownSections_ArePassedThrough()
	{
		var report = ActivePlacesCoverageReader.Read("""{"coverage_by_something_new": [{"x": 1}]}""");

		Assert.Equal(1, report.Document.GetProperty("coverage_by_something_new")[0].GetProperty("x").GetInt32());
	}

	#region Missing and malformed

	/// <summary>
	/// The dates only label the answer, so a report that stopped carrying them is still worth serving:
	/// the run date falls back to the generation day, and that to today.
	/// </summary>
	[Fact]
	public void MissingRunDate_FallsBackToTheGenerationDay()
	{
		var report = ActivePlacesCoverageReader.Read("""{"generated_at": "2026-09-17T13:07:34+00:00"}""");

		Assert.Equal(new DateOnly(2026, 9, 17), report.RunDate);
	}

	[Theory]
	[InlineData("{}")]
	[InlineData("""{"run_date": "not a date", "generated_at": "not a stamp"}""")]
	[InlineData("""{"run_date": 20260917, "generated_at": 20260917}""")]
	public void UnreadableDates_FallBackToToday(string json)
	{
		var before = DateTime.UtcNow;

		var report = ActivePlacesCoverageReader.Read(json);

		Assert.InRange(report.GeneratedAt, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
		Assert.Equal(DateOnly.FromDateTime(report.GeneratedAt), report.RunDate);
	}

	/// <summary>
	/// A body that is not an object is a failed or redirected download rather than a schema change, so
	/// it fails loudly and the endpoint answers 502.
	/// </summary>
	[Theory]
	[InlineData("")]
	[InlineData("not json at all")]
	[InlineData("<html><body>404: Not Found</body></html>")]
	[InlineData("[1, 2, 3]")]
	[InlineData("\"a string\"")]
	public void ABodyThatIsNotAReport_Throws(string json) =>
		Assert.Throws<InvalidDataException>(() => ActivePlacesCoverageReader.Read(json));

	#endregion
}
