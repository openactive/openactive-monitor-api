using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MonitorApi.Models.Admin;

namespace MonitorApi.Admin.Tests.Notifications;

/// <summary>
/// Live BigQuery tests for publisher notification CRUD. Seeds a unique dummy row, checks envelopes
/// and round-trips, then deletes it. Never asserts absolute estate counts.
/// </summary>
public class PublisherNotificationsEndpointTests(AdminApiFixture fixture) : IClassFixture<AdminApiFixture>
{
	private const string ListRoute = "/admin/publisher-notifications";
	private const string SummaryRoute = "/admin/publisher-notifications/summary";

	private readonly AdminApiFixture _fixture = fixture;

	private static readonly JsonSerializerOptions JsonOptions =
		new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

	[Fact]
	public async Task List_ReturnsPagedEnvelope()
	{
		using var client = _fixture.CreateClient();
		var response = await client.GetAsync(_fixture.WithAdminToken(ListRoute));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		var page = await response.Content.ReadFromJsonAsync<AdminPage<PublisherNotificationRow>>(JsonOptions);
		Assert.NotNull(page);
		Assert.NotNull(page.Data);
		Assert.True(page.Meta.Page >= 1);
		Assert.True(page.Meta.PageSize >= 1);
		Assert.True(page.Meta.Total >= 0);
	}

	[Fact]
	public async Task Summary_ReturnsDocumentEnvelopeWithNonNegativeCounts()
	{
		using var client = _fixture.CreateClient();
		var response = await client.GetAsync(_fixture.WithAdminToken(SummaryRoute));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		var document = await response.Content.ReadFromJsonAsync<AdminDocument<PublisherNotificationSummary>>(JsonOptions);
		Assert.NotNull(document);
		Assert.Equal(1, document.Meta.Page);
		Assert.Equal(1, document.Meta.PageSize);
		Assert.Equal(1, document.Meta.Total);

		var summary = document.Data;
		Assert.True(summary.Publishers >= 0);
		Assert.True(summary.Open >= 0);
		Assert.True(summary.InProgress >= 0);
		Assert.True(summary.Closed >= 0);
		Assert.True(summary.NeverContacted >= 0);
		Assert.True(summary.Alerts >= 0);
	}

	[Fact]
	public async Task CreateUpdateDelete_RoundTrip()
	{
		var id = $"pn_test-{Guid.NewGuid():N}";
		using var client = _fixture.CreateClient();

		try
		{
			var createBody = new PublisherNotificationBody
			{
				Id = id,
				PublisherId = "pub_test-dummy",
				PublisherName = "Test Dummy Publisher",
				ProblemCount = 1,
				Monitors = ["single_feed_stall"],
				OldestDaysOpen = 3,
				Status = "open",
				Stakeholder = "Test Steward",
				Contact = "dummy.publisher@example.org",
				Notes = "endpoint test seed",
			};

			var createResponse = await client.PostAsJsonAsync(
				_fixture.WithAdminToken(ListRoute),
				createBody,
				JsonOptions);
			Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

			var created = await createResponse.Content
				.ReadFromJsonAsync<AdminDocument<PublisherNotificationRow>>(JsonOptions);
			Assert.NotNull(created);
			Assert.Equal(id, created.Data.Id);
			Assert.Equal("open", created.Data.Status);
			Assert.NotNull(created.Data.CreatedAt);
			Assert.NotNull(created.Data.UpdatedAt);

			var listResponse = await client.GetAsync(_fixture.WithAdminToken(ListRoute));
			Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
			var list = await listResponse.Content.ReadFromJsonAsync<AdminPage<PublisherNotificationRow>>(JsonOptions);
			Assert.Contains(list!.Data, row => row.Id == id);

			var updateBody = new PublisherNotificationBody
			{
				Id = id,
				PublisherId = createBody.PublisherId,
				PublisherName = createBody.PublisherName,
				ProblemCount = 2,
				Monitors = ["single_feed_stall", "feed_ingestion_error"],
				OldestDaysOpen = 5,
				Status = "in_progress",
				Stakeholder = "Test Steward",
				Contact = "dummy.publisher@example.org",
				DateContacted = DateOnly.FromDateTime(DateTime.UtcNow),
				Notes = "endpoint test updated",
			};

			var updateResponse = await client.PatchAsJsonAsync(
				_fixture.WithAdminToken($"{ListRoute}/{id}"),
				updateBody,
				JsonOptions);
			Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

			var updated = await updateResponse.Content
				.ReadFromJsonAsync<AdminDocument<PublisherNotificationRow>>(JsonOptions);
			Assert.NotNull(updated);
			Assert.Equal("in_progress", updated.Data.Status);
			Assert.Equal(2, updated.Data.ProblemCount);
			Assert.Equal(2, updated.Data.Monitors.Count);
			Assert.Equal("endpoint test updated", updated.Data.Notes);
		}
		finally
		{
			var deleteResponse = await client.DeleteAsync(
				_fixture.WithAdminToken($"{ListRoute}/{id}"));
			Assert.True(
				deleteResponse.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound,
				$"cleanup delete returned {deleteResponse.StatusCode}");
		}

		var missing = await client.DeleteAsync(_fixture.WithAdminToken($"{ListRoute}/{id}"));
		Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

		var updateMissing = await client.PatchAsJsonAsync(
			_fixture.WithAdminToken($"{ListRoute}/{id}"),
			new PublisherNotificationBody { Id = id, Status = "closed" },
			JsonOptions);
		Assert.Equal(HttpStatusCode.NotFound, updateMissing.StatusCode);
	}
}
