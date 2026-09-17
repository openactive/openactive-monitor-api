using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MonitorApi.Admin.Tests;

/// <summary>
/// The admin surface publishes its own OpenAPI document and Scalar page, so the dashboard developer
/// gets a reference containing only the admin endpoints.
/// </summary>
public class AdminApiReferenceTests(AdminApiFixture fixture) : IClassFixture<AdminApiFixture>
{
	private readonly AdminApiFixture _fixture = fixture;

	[Theory]
	[InlineData("/openapi/admin.json")]
	[InlineData("/scalar/admin")]
	public async Task AdminReference_IsServedAndNotTokenGated(string path)
	{
		using var client = _fixture.CreateClient();
		var response = await client.GetAsync(path);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Fact]
	public async Task AdminDocument_ContainsEveryAdminEndpointAndNothingElse()
	{
		var paths = await AdminDocumentPaths();

		Assert.Contains("/admin/single-feed-stall-incidents", paths);
		Assert.Contains("/admin/single-feed-stall-trend", paths);
		Assert.Contains("/admin/dataset-stall-incidents", paths);
		Assert.Contains("/admin/dataset-stall-trend", paths);
		Assert.Contains("/admin/feed-ingestion-error-incidents", paths);
		Assert.Contains("/admin/feed-ingestion-error-trend", paths);
		Assert.Contains("/admin/dataset-orphaned-children-incidents", paths);
		Assert.Contains("/admin/dataset-future-decline-incidents", paths);
		Assert.Contains("/admin/dataset-future-decline-trend", paths);
		Assert.Contains("/admin/feed-quality", paths);
		Assert.All(paths, path => Assert.StartsWith("/admin/", path));
	}

	[Theory]
	[InlineData("/admin/single-feed-stall-incidents", "page,page_size,lookback_days,stall_days,past_threshold_days,as_of")]
	[InlineData("/admin/dataset-stall-incidents", "page,page_size,lookback_days,stall_days,past_threshold_days,as_of")]
	[InlineData("/admin/dataset-stall-trend", "page,page_size,trend_days,lookback_days,stall_days,past_threshold_days,as_of")]
	[InlineData("/admin/feed-ingestion-error-incidents", "page,page_size,success_lookback_days,error_days,past_threshold_days,as_of")]
	[InlineData("/admin/feed-ingestion-error-trend", "page,page_size,trend_days,success_lookback_days,error_days,past_threshold_days,as_of")]
	// No date parameter of any kind: opportunities holds current state only, so a past date cannot be
	// answered and there is nothing to window.
	[InlineData("/admin/dataset-orphaned-children-incidents", "page,page_size,min_orphans,min_share,past_threshold_orphans")]
	[InlineData("/admin/dataset-future-decline-incidents", "page,page_size,window_days,drop_percent,qualify_window_days,qualify_drop_percent,past_threshold_drop_percent,min_future_opportunities,as_of")]
	[InlineData("/admin/dataset-future-decline-trend", "page,page_size,trend_days,window_days,drop_percent,qualify_window_days,qualify_drop_percent,past_threshold_drop_percent,min_future_opportunities,as_of")]
	// Not a monitor: feed_quality is current state, so there is no as_of and no threshold to tune —
	// only paging and the two identity filters.
	[InlineData("/admin/feed-quality", "page,page_size,dataset_url,publisher")]
	public async Task AdminDocument_DocumentsTheQueryParametersWithTheirDefaults(string path, string expected)
	{
		using var client = _fixture.CreateClient();
		var document = await client.GetFromJsonAsync<JsonElement>("/openapi/admin.json");

		var parameters = document
			.GetProperty("paths")
			.GetProperty(path)
			.GetProperty("get")
			.GetProperty("parameters")
			.EnumerateArray()
			.Select(p => p.GetProperty("name").GetString())
			.ToList();

		Assert.Equal(expected.Split(','), parameters);
	}

	[Fact]
	public async Task AdminDocument_DescribesTheSharedEnvelopeInSnakeCase()
	{
		using var client = _fixture.CreateClient();
		var document = await client.GetFromJsonAsync<JsonElement>("/openapi/admin.json");

		var schemas = document.GetProperty("components").GetProperty("schemas");
		var incident = schemas.GetProperty("StallIncident").GetProperty("properties")
			.EnumerateObject().Select(p => p.Name).ToList();

		Assert.Contains("monitor_id", incident);
		Assert.Contains("past_threshold", incident);
		Assert.Contains("quality_score", incident);

		var datasetStall = schemas.GetProperty("DatasetStallIncident").GetProperty("properties")
			.EnumerateObject().Select(p => p.Name).ToList();

		Assert.Contains("monitor_id", datasetStall);
		Assert.Contains("past_threshold", datasetStall);
		Assert.Contains("dataset_url", datasetStall);
		Assert.Contains("feed_count", datasetStall);
		Assert.Contains("trend", datasetStall);

		// Dataset-scoped like the orphaned-children monitor, so no single feed identifies it and there is
		// no dataset-level quality score to report. The feeds it accounts for are in detail.feeds.
		Assert.DoesNotContain("feed_id", datasetStall);
		Assert.DoesNotContain("feed_url", datasetStall);
		Assert.DoesNotContain("quality_score", datasetStall);

		var error = schemas.GetProperty("IngestionErrorIncident").GetProperty("properties")
			.EnumerateObject().Select(p => p.Name).ToList();

		Assert.Contains("monitor_id", error);
		Assert.Contains("past_threshold", error);

		var orphans = schemas.GetProperty("OrphanedChildrenIncident").GetProperty("properties")
			.EnumerateObject().Select(p => p.Name).ToList();

		Assert.Contains("monitor_id", orphans);
		Assert.Contains("past_threshold", orphans);
		Assert.Contains("dataset_url", orphans);
		Assert.Contains("missing_parent_count", orphans);

		// The lean-model decision, made executable. This monitor is per-dataset and has no history, so
		// these are absent rather than present-and-always-null; re-adding them as nullable fields would
		// commit the API to narrowing them later, which is a breaking change.
		Assert.DoesNotContain("feed_id", orphans);
		Assert.DoesNotContain("days_open", orphans);
		Assert.DoesNotContain("consecutive_days", orphans);
		Assert.DoesNotContain("first_detected", orphans);
		Assert.DoesNotContain("trend", orphans);
		Assert.DoesNotContain("quality_score", orphans);

		var decline = schemas.GetProperty("DatasetFutureDeclineIncident").GetProperty("properties")
			.EnumerateObject().Select(p => p.Name).ToList();

		Assert.Contains("monitor_id", decline);
		Assert.Contains("past_threshold", decline);
		Assert.Contains("dataset_url", decline);
		Assert.Contains("feed_count", decline);
		Assert.Contains("trend", decline);

		// Dataset-scoped for the same reasons as the two above: the publisher is who gets contacted, and
		// the feeds actually losing supply are in detail.feeds with their own figures.
		Assert.DoesNotContain("feed_id", decline);
		Assert.DoesNotContain("feed_url", decline);
		Assert.DoesNotContain("quality_score", decline);

		var declineDetail = schemas.GetProperty("DatasetFutureDeclineIncidentDetail").GetProperty("properties")
			.EnumerateObject().Select(p => p.Name).ToList();

		Assert.Contains("reason", declineDetail);
		Assert.Contains("drop", declineDetail);
		Assert.Contains("drop_percent", declineDetail);
		Assert.Contains("qualify_window_days", declineDetail);
		Assert.Contains("qualify_drop_percent", declineDetail);
		Assert.Contains("feeds", declineDetail);

		var declineFeed = schemas.GetProperty("DatasetFutureDeclineFeed").GetProperty("properties")
			.EnumerateObject().Select(p => p.Name).ToList();

		Assert.Contains("qualify_drop_percent", declineFeed);
		Assert.Contains("delta_in_window", declineFeed);

		var errorDetail = schemas.GetProperty("IngestionErrorIncidentDetail").GetProperty("properties")
			.EnumerateObject().Select(p => p.Name).ToList();

		Assert.Contains("error_code", errorDetail);
		Assert.Contains("error_message", errorDetail);
		Assert.Contains("last_completed", errorDetail);

		var quality = schemas.GetProperty("FeedQualityRow").GetProperty("properties")
			.EnumerateObject().Select(p => p.Name).ToList();

		Assert.Contains("dataset_url", quality);
		Assert.Contains("feed_version", quality);
		Assert.Contains("num_future_opportunity_items", quality);
		Assert.Contains("missing_required_fields", quality);
		Assert.Contains("last_assessed", quality);

		// Not an incident: feed_quality holds current state, so the monitor spine is absent rather than
		// present-and-always-null, for the reason OrphanedChildrenIncident documents.
		Assert.DoesNotContain("monitor_id", quality);
		Assert.DoesNotContain("first_detected", quality);
		Assert.DoesNotContain("days_open", quality);
		Assert.DoesNotContain("consecutive_days", quality);
		Assert.DoesNotContain("past_threshold", quality);
		Assert.DoesNotContain("trend", quality);

		var qualitySummary = schemas.GetProperty("FeedQualitySummary").GetProperty("properties")
			.EnumerateObject().Select(p => p.Name).ToList();

		Assert.Contains("total_feeds", qualitySummary);
		Assert.Contains("total_datasets", qualitySummary);
		Assert.Contains("average_score", qualitySummary);
		Assert.Contains("score_buckets", qualitySummary);
		Assert.Contains("feeds_with_future_data", qualitySummary);
		Assert.Contains("grade_breakdown", qualitySummary);

		var completeness = schemas.GetProperty("FeedQualityAverage").GetProperty("properties")
			.EnumerateObject().Select(p => p.Name).ToList();

		Assert.Contains("average", completeness);
		Assert.Contains("feeds_reporting", completeness);

		var meta = schemas.GetProperty("AdminPageMeta").GetProperty("properties")
			.EnumerateObject().Select(p => p.Name).ToList();

		Assert.Contains("snapshot_date", meta);
		Assert.Contains("page_size", meta);
	}

	private async Task<List<string>> AdminDocumentPaths()
	{
		using var client = _fixture.CreateClient();
		var document = await client.GetFromJsonAsync<JsonElement>("/openapi/admin.json");

		return document.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
	}
}
