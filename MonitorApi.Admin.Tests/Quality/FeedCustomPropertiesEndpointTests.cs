using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MonitorApi.Models.Admin;

namespace MonitorApi.Admin.Tests.Quality;

/// <summary>
/// Live-data tests for <c>/admin/feed-custom-properties</c>. The rules and arithmetic are pinned by
/// <see cref="CustomPropertiesSummariserTests"/>; these check the wiring, the three-key envelope, the
/// filters and the invariants that must hold whatever the data looks like on the day.
/// </summary>
public class FeedCustomPropertiesEndpointTests(AdminApiFixture fixture) : IClassFixture<AdminApiFixture>
{
	private const string Route = "/admin/feed-custom-properties";

	private readonly AdminApiFixture _fixture = fixture;

	private static readonly JsonSerializerOptions JsonOptions =
		new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

	private async Task<AdminSummarisedPage<FeedCustomPropertiesRow, CustomPropertiesSummary>> Get(string query = "")
	{
		using var client = _fixture.CreateClient();
		var response = await client.GetAsync(_fixture.WithAdminToken(Route + query));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return (await response.Content.ReadFromJsonAsync<AdminSummarisedPage<FeedCustomPropertiesRow, CustomPropertiesSummary>>(JsonOptions))!;
	}

	#region Envelope

	[Fact]
	public async Task ReturnsTheDocumentedEnvelope()
	{
		var page = await Get();

		Assert.NotNull(page.Data);
		Assert.NotNull(page.Summary);
		Assert.Equal(1, page.Meta.Page);
		Assert.Equal(500, page.Meta.PageSize);
		Assert.True(page.Meta.Total >= page.Data.Count);
		Assert.Equal(DateTimeKind.Utc, page.Meta.GeneratedAt.Kind);
		Assert.True(page.Meta.SnapshotDate > DateOnly.MinValue);
	}

	[Fact]
	public async Task ExcludedFieldsAreAbsentFromThePayload()
	{
		using var client = _fixture.CreateClient();
		var json = await client.GetFromJsonAsync<JsonElement>(_fixture.WithAdminToken(Route + "?page_size=1"));

		var data = json.GetProperty("data");
		if (data.GetArrayLength() == 0)
		{
			return;
		}

		var row = data[0];
		Assert.False(row.TryGetProperty("vocab_source", out _));
		Assert.False(row.TryGetProperty("last_assessed", out _));

		var property = row.GetProperty("custom_properties")[0];
		Assert.False(property.TryGetProperty("property_kind", out _));
		Assert.False(property.TryGetProperty("occurrences", out _));
		Assert.False(property.TryGetProperty("entity_instances", out _));
		Assert.True(property.TryGetProperty("presence_pct", out _));
	}

	#endregion

	#region Rows

	[Fact]
	public async Task EveryRowUsesCustomPropertiesAndIsInternallyConsistent()
	{
		var page = await Get("?page_size=1000");

		Assert.All(page.Data, row =>
		{
			Assert.NotEmpty(row.FeedId);
			Assert.NotEmpty(row.DatasetUrl);
			Assert.NotEmpty(row.DatasetName);
			Assert.StartsWith("pub_", row.PublisherId);

			Assert.True(row.NumCustomProperties > 0, $"{row.FeedId} reported {row.NumCustomProperties} custom properties");
			Assert.True(row.SampledItems is null or >= 0);

			Assert.All(row.CustomProperties, p =>
			{
				Assert.False(string.IsNullOrWhiteSpace(p.Property));
				Assert.True(p.PresencePct is null or (>= 0 and <= 100), $"{row.FeedId} {p.Property} presence {p.PresencePct}");
			});

			// Sorted most widely present first within the feed.
			var presence = row.CustomProperties.Select(p => p.PresencePct ?? double.MinValue).ToList();
			Assert.Equal(presence.OrderByDescending(v => v), presence);
		});
	}

	[Fact]
	public async Task EachDatasetFeedIsReportedAtMostOnce()
	{
		var page = await Get("?page_size=1000");

		// (dataset_url, feed_id), not feed_id: one publisher serves the same feeds under two dataset URLs.
		Assert.Equal(page.Data.Count, page.Data.Select(r => (r.DatasetUrl, r.FeedId)).Distinct().Count());
	}

	[Fact]
	public async Task RowsAreOrderedMostCustomPropertiesFirst()
	{
		var page = await Get("?page_size=1000");

		var counts = page.Data.Select(r => r.NumCustomProperties!.Value).ToList();
		Assert.Equal(counts.OrderByDescending(c => c), counts);
	}

	#endregion

	#region Summary

	[Fact]
	public async Task SummaryCoversTheWholeResultSetRatherThanThePage()
	{
		var page = await Get("?page_size=1");

		Assert.Equal(page.Meta.Total, page.Summary.FeedsWithCustomProperties);
		Assert.True(page.Data.Count <= 1);
	}

	[Fact]
	public async Task SummaryFiguresAreConsistentWithEachOther()
	{
		var summary = (await Get()).Summary;

		Assert.True(summary.FeedsWithCustomProperties <= summary.FeedsAssessed);
		Assert.True(summary.DatasetsWithCustomProperties <= summary.DatasetsAssessed);
		Assert.True(summary.DatasetsWithCustomProperties <= summary.FeedsWithCustomProperties);

		if (summary.FeedsAssessed == 0)
		{
			Assert.Null(summary.FeedShare);
		}
		else
		{
			Assert.InRange(summary.FeedShare!.Value, 0, 1);
		}

		Assert.Equal(summary.DistinctCustomProperties, summary.PropertyBreakdown.Count);
		Assert.Equal(summary.DistinctCustomProperties, summary.NamespaceBreakdown.Sum(n => n.PropertyCount));
		Assert.All(summary.PropertyBreakdown, p =>
		{
			Assert.InRange(p.FeedCount, 1, summary.FeedsWithCustomProperties);
			Assert.True(p.DatasetCount <= p.FeedCount);
		});

		// Totally ordered, so the same request always renders the same list.
		Assert.Equal(
			summary.PropertyBreakdown.OrderByDescending(p => p.FeedCount).ThenBy(p => p.Property, StringComparer.Ordinal),
			summary.PropertyBreakdown);
	}

	[Fact]
	public async Task SummaryAgreesWithTheRowsItWasReducedFrom()
	{
		var page = await Get("?page_size=1000");
		if (page.Meta.Total > page.Data.Count)
		{
			return;
		}

		Assert.Equal(
			page.Data.Select(r => r.DatasetUrl).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
			page.Summary.DatasetsWithCustomProperties);
		Assert.Equal(page.Data.Sum(r => r.NumCustomPropertyUsages ?? 0), page.Summary.TotalCustomPropertyUsages);
		Assert.Equal(
			page.Data.SelectMany(r => r.CustomProperties).Select(p => p.Property).Distinct(StringComparer.Ordinal).Count(),
			page.Summary.DistinctCustomProperties);
	}

	#endregion

	#region Paging

	[Fact]
	public async Task PagesAreDisjointAndCoverTheWholeResultSet()
	{
		var first = await Get("?page=1&page_size=10");
		if (first.Meta.Total <= 10)
		{
			return;
		}

		var second = await Get("?page=2&page_size=10");

		Assert.Equal(10, first.Data.Count);
		Assert.Equal(first.Meta.Total, second.Meta.Total);
		Assert.Empty(first.Data.Select(r => (r.DatasetUrl, r.FeedId)).Intersect(second.Data.Select(r => (r.DatasetUrl, r.FeedId))));
		Assert.Equal(first.Summary.DistinctCustomProperties, second.Summary.DistinctCustomProperties);
	}

	[Fact]
	public async Task PagingArgumentsAreClamped()
	{
		var page = await Get("?page=0&page_size=99999");

		Assert.Equal(1, page.Meta.Page);
		Assert.Equal(1000, page.Meta.PageSize);
	}

	#endregion

	#region Filters

	[Fact]
	public async Task DatasetUrlFilter_NarrowsToThatDataset()
	{
		var all = await Get("?page_size=1000");
		if (all.Data.Count == 0)
		{
			return;
		}

		var dataset = all.Data[0].DatasetUrl;
		var filtered = await Get("?dataset_url=" + Uri.EscapeDataString(dataset));

		Assert.NotEmpty(filtered.Data);
		Assert.All(filtered.Data, r => Assert.Equal(dataset, r.DatasetUrl));
		Assert.True(filtered.Meta.Total <= all.Meta.Total);
		Assert.Equal(1, filtered.Summary.DatasetsWithCustomProperties);
		Assert.Equal(1, filtered.Summary.DatasetsAssessed);
	}

	[Fact]
	public async Task PublisherFilter_NarrowsToThatPublisher()
	{
		var all = await Get("?page_size=1000");
		var named = all.Data.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.PublisherName));
		if (named is null)
		{
			return;
		}

		var filtered = await Get("?publisher=" + Uri.EscapeDataString(named.PublisherName));

		Assert.NotEmpty(filtered.Data);
		Assert.All(filtered.Data, r => Assert.Equal(named.PublisherName, r.PublisherName));
		Assert.True(filtered.Meta.Total <= all.Meta.Total);
	}

	[Fact]
	public async Task RepeatedAndCommaSeparatedValuesAgree()
	{
		var all = await Get("?page_size=1000");
		var datasets = all.Data.Select(r => r.DatasetUrl).Distinct(StringComparer.Ordinal).Take(2).ToList();
		if (datasets.Count < 2)
		{
			return;
		}

		var escaped = datasets.Select(Uri.EscapeDataString).ToList();
		var repeated = await Get($"?dataset_url={escaped[0]}&dataset_url={escaped[1]}");
		var commaSeparated = await Get($"?dataset_url={escaped[0]},{escaped[1]}");

		Assert.Equal(repeated.Meta.Total, commaSeparated.Meta.Total);
		Assert.Equal(repeated.Data.Select(r => (r.DatasetUrl, r.FeedId)), commaSeparated.Data.Select(r => (r.DatasetUrl, r.FeedId)));
		Assert.Equal(2, repeated.Summary.DatasetsWithCustomProperties);
	}

	[Fact]
	public async Task UnmatchedFilter_IsAnEmptyPageWithAZeroedSummary()
	{
		var page = await Get("?dataset_url=" + Uri.EscapeDataString("https://no-such-dataset.invalid/"));

		Assert.Empty(page.Data);
		Assert.Equal(0, page.Meta.Total);
		Assert.Equal(0, page.Summary.FeedsAssessed);
		Assert.Equal(0, page.Summary.FeedsWithCustomProperties);
		Assert.Null(page.Summary.FeedShare);
		Assert.Empty(page.Summary.PropertyBreakdown);
		// The snapshot still dates the table, not the empty result.
		Assert.True(page.Meta.SnapshotDate > DateOnly.MinValue);
	}

	#endregion
}
