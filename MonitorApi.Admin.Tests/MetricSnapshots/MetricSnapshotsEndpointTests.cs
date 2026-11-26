using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Google.Cloud.BigQuery.V2;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MonitorApi.Models.Admin;

namespace MonitorApi.Admin.Tests.MetricSnapshots;

/// <summary>
/// Live BigQuery tests for the metric snapshot store. Each writing test uses a source of its own,
/// unique to the run, and deletes it again afterwards, so the real series is never touched and two
/// runs can never collide.
/// </summary>
/// <remarks>
/// Never asserts on what the table already holds. The capture job adds a period a month and the
/// backfill will add more, so any assertion on totals or on a particular period would start failing
/// on a date nobody chose.
/// </remarks>
public class MetricSnapshotsEndpointTests(AdminApiFixture fixture) : IClassFixture<AdminApiFixture>
{
	private const string Route = "/admin/metric-snapshots";

	/// <summary>
	/// Every source this suite writes begins with this, and <see cref="DeleteTestRows"/> refuses any
	/// source that does not. A real capture therefore cannot be deleted by a mistake in a test.
	/// </summary>
	private const string TestSourcePrefix = "test_";

	private readonly AdminApiFixture _fixture = fixture;

	private static readonly JsonSerializerOptions JsonOptions =
		new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

	[Fact]
	public async Task List_ReturnsPagedEnvelope()
	{
		using var client = _fixture.CreateClient();
		var response = await client.GetAsync(_fixture.WithAdminToken(Route));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		var page = await response.Content.ReadFromJsonAsync<AdminPage<MetricSnapshotRow>>(JsonOptions);
		Assert.NotNull(page);
		Assert.NotNull(page.Data);
		Assert.True(page.Meta.Page >= 1);
		Assert.True(page.Meta.PageSize >= 1);
		Assert.True(page.Meta.Total >= 0);
	}

	/// <summary>
	/// The round trip that matters: a batch goes in, comes back carrying what BigQuery stored, and is
	/// then refused. The table has no uniqueness constraint, so that refusal is the only thing keeping
	/// a retried capture from doubling a period.
	/// </summary>
	[Fact]
	public async Task Create_WritesTheBatch_ReadsItBack_AndRefusesARepeat()
	{
		var source = NewTestSource();
		using var client = _fixture.CreateClient();

		try
		{
			var batch = new[]
			{
				new MetricSnapshotWrite
				{
					Period = new DateOnly(2026, 11, 1),
					Source = source,
					Field = "with_everything",
					Value = 12.5,
					Denominator = 242,
					SnapshotDate = new DateOnly(2026, 11, 25),
				},
				new MetricSnapshotWrite
				{
					Period = new DateOnly(2026, 11, 1),
					Source = source,
					Field = "bare_count",
					Value = 7,
				},
			};

			var created = await client.PostAsJsonAsync(_fixture.WithAdminToken(Route), batch, JsonOptions);
			Assert.Equal(HttpStatusCode.Created, created.StatusCode);

			var written = await created.Content.ReadFromJsonAsync<AdminPage<MetricSnapshotRow>>(JsonOptions);
			Assert.NotNull(written);
			Assert.Equal(2, written.Data.Count);

			var full = written.Data.Single(r => r.Field == "with_everything");
			Assert.Equal(new DateOnly(2026, 11, 1), full.Period);
			Assert.Equal(12.5, full.Value);
			Assert.Equal(242, full.Denominator);
			Assert.Equal(new DateOnly(2026, 11, 25), full.SnapshotDate);

			// captured_at is CURRENT_TIMESTAMP() in the insert, so it proves the row came back from
			// BigQuery rather than being echoed from the request.
			Assert.NotNull(full.CapturedAt);
			Assert.Equal(DateTimeKind.Utc, full.CapturedAt!.Value.Kind);

			// The columns a caller may leave out stay NULL rather than becoming zero.
			var bare = written.Data.Single(r => r.Field == "bare_count");
			Assert.Equal(7, bare.Value);
			Assert.Null(bare.Denominator);
			Assert.Null(bare.SnapshotDate);

			var listed = await client.GetFromJsonAsync<AdminPage<MetricSnapshotRow>>(
				_fixture.WithAdminToken($"{Route}?source={source}"),
				JsonOptions);
			Assert.NotNull(listed);
			Assert.Equal(2, listed.Data.Count);

			// No filter at all must mean every row rather than none. An omitted array filter reaches
			// BigQuery as a NULL array, and reading that as "match nothing" would empty the endpoint.
			// Asserted as a floor, never an exact count: the capture job adds a period every month.
			var unfiltered = await client.GetFromJsonAsync<AdminPage<MetricSnapshotRow>>(
				_fixture.WithAdminToken(Route),
				JsonOptions);
			Assert.NotNull(unfiltered);
			Assert.True(
				unfiltered.Meta.Total >= 2,
				$"an unfiltered list returned {unfiltered.Meta.Total} rows, having just written 2");

			// One measurement of the batch is enough to refuse the whole batch.
			var repeat = await client.PostAsJsonAsync(
				_fixture.WithAdminToken(Route),
				new[]
				{
					new MetricSnapshotWrite
					{
						Period = batch[0].Period,
						Source = batch[0].Source,
						Field = batch[0].Field,
						Value = 99,
					},
				},
				JsonOptions);
			Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);

			// ...and nothing was written: the refused value is nowhere to be seen.
			var after = await client.GetFromJsonAsync<AdminPage<MetricSnapshotRow>>(
				_fixture.WithAdminToken($"{Route}?source={source}"),
				JsonOptions);
			Assert.NotNull(after);
			Assert.Equal(2, after.Data.Count);
			Assert.DoesNotContain(after.Data, r => r.Value == 99);
		}
		finally
		{
			await DeleteTestRows(source);
		}
	}

	/// <summary>
	/// Omitting a filter must mean "every value", not "no values" — the array parameters are empty in
	/// that case, and an <c>IN UNNEST</c> over an empty array matches nothing.
	/// </summary>
	[Fact]
	public async Task List_FiltersBySourceFieldAndPeriodWindow()
	{
		var source = NewTestSource();
		using var client = _fixture.CreateClient();

		try
		{
			var batch = new[]
			{
				Row(source, new DateOnly(2026, 9, 1), "alpha", 1),
				Row(source, new DateOnly(2026, 10, 1), "alpha", 2),
				Row(source, new DateOnly(2026, 10, 1), "beta", 3),
			};

			var created = await client.PostAsJsonAsync(_fixture.WithAdminToken(Route), batch, JsonOptions);
			Assert.Equal(HttpStatusCode.Created, created.StatusCode);

			// Newest period first, as the endpoint documents.
			var all = await Fetch(client, $"?source={source}");
			Assert.Equal(3, all.Count);
			Assert.Equal(new DateOnly(2026, 10, 1), all[0].Period);
			Assert.Equal(new DateOnly(2026, 9, 1), all[^1].Period);

			var alpha = await Fetch(client, $"?source={source}&field=alpha");
			Assert.Equal(2, alpha.Count);
			Assert.All(alpha, r => Assert.Equal("alpha", r.Field));

			// Repeated and comma-separated spellings are the same filter, and OR within it.
			var repeated = await Fetch(client, $"?source={source}&field=alpha&field=beta");
			var commaSeparated = await Fetch(client, $"?source={source}&field=alpha,beta");
			Assert.Equal(3, repeated.Count);
			Assert.Equal(3, commaSeparated.Count);

			// from and to are inclusive.
			var october = await Fetch(client, $"?source={source}&from=2026-10-01");
			Assert.Equal(2, october.Count);
			Assert.All(october, r => Assert.Equal(new DateOnly(2026, 10, 1), r.Period));

			var september = await Fetch(client, $"?source={source}&to=2026-09-01");
			Assert.Single(september);

			var window = await Fetch(client, $"?source={source}&from=2026-09-01&to=2026-10-01");
			Assert.Equal(3, window.Count);

			// Different filters AND together.
			var narrowed = await Fetch(client, $"?source={source}&field=beta&from=2026-10-01");
			Assert.Single(narrowed);
			Assert.Equal(3, narrowed[0].Value);

			var outsideWindow = await Fetch(client, $"?source={source}&from=2026-11-01");
			Assert.Empty(outsideWindow);
		}
		finally
		{
			await DeleteTestRows(source);
		}
	}

	/// <summary>
	/// Every refusal below happens before the insert, so none of these writes anything and none needs
	/// cleaning up.
	/// </summary>
	[Fact]
	public async Task Create_RefusesAnEmptyBatch()
	{
		using var client = _fixture.CreateClient();
		var response = await client.PostAsJsonAsync(
			_fixture.WithAdminToken(Route),
			Array.Empty<MetricSnapshotWrite>(),
			JsonOptions);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Create_RefusesTheSameMeasurementTwiceInOneBatch()
	{
		var source = NewTestSource();
		using var client = _fixture.CreateClient();

		var duplicated = new[]
		{
			Row(source, new DateOnly(2026, 10, 1), "alpha", 1),
			Row(source, new DateOnly(2026, 10, 1), "alpha", 2),
		};

		var response = await client.PostAsJsonAsync(_fixture.WithAdminToken(Route), duplicated, JsonOptions);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

		// Refused in full, so the batch's other measurement is not written either.
		var listed = await Fetch(client, $"?source={source}");
		Assert.Empty(listed);
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public async Task Create_RefusesABlankSourceOrField(string blank)
	{
		using var client = _fixture.CreateClient();

		var blankSource = await client.PostAsJsonAsync(
			_fixture.WithAdminToken(Route),
			new[] { Row(blank, new DateOnly(2026, 10, 1), "alpha", 1) },
			JsonOptions);
		Assert.Equal(HttpStatusCode.BadRequest, blankSource.StatusCode);

		var blankField = await client.PostAsJsonAsync(
			_fixture.WithAdminToken(Route),
			new[] { Row(NewTestSource(), new DateOnly(2026, 10, 1), blank, 1) },
			JsonOptions);
		Assert.Equal(HttpStatusCode.BadRequest, blankField.StatusCode);
	}

	[Fact]
	public async Task Create_RefusesThePublicTokenAndNoTokenAtAll()
	{
		using var client = _fixture.CreateClient();
		var batch = new[] { Row(NewTestSource(), new DateOnly(2026, 10, 1), "alpha", 1) };

		var noToken = await client.PostAsJsonAsync(Route, batch, JsonOptions);
		Assert.Equal(HttpStatusCode.Forbidden, noToken.StatusCode);

		var publicToken = await client.PostAsJsonAsync(
			AdminApiFixture.WithToken(Route, _fixture.PublicAccessToken),
			batch,
			JsonOptions);
		Assert.Equal(HttpStatusCode.Forbidden, publicToken.StatusCode);
	}

	private static MetricSnapshotWrite Row(string source, DateOnly period, string field, double value) =>
		new() { Period = period, Source = source, Field = field, Value = value };

	/// <summary>A source no other run can be using, within the prefix the cleanup helper allows.</summary>
	private static string NewTestSource() => $"{TestSourcePrefix}{Guid.NewGuid():N}";

	private async Task<IReadOnlyList<MetricSnapshotRow>> Fetch(HttpClient client, string query)
	{
		var page = await client.GetFromJsonAsync<AdminPage<MetricSnapshotRow>>(
			_fixture.WithAdminToken(Route + query),
			JsonOptions);
		Assert.NotNull(page);
		return page.Data;
	}

	/// <summary>
	/// Removes the rows a test wrote. The API has no delete route, so this goes to the table directly
	/// — and refuses any source outside <see cref="TestSourcePrefix"/>, because a wrong filter here
	/// would take a real capture with it and the series cannot be recomputed from anywhere.
	/// </summary>
	private async Task DeleteTestRows(string source)
	{
		if (!source.StartsWith(TestSourcePrefix, StringComparison.Ordinal))
		{
			throw new InvalidOperationException(
				$"refusing to delete metric snapshots for source '{source}': not a test source.");
		}

		var options = _fixture.Services.GetRequiredService<IOptions<BigQueryOptions>>().Value;
		var client = await BigQueryClient.CreateAsync(options.ProjectId!, options.GoogleCredential);

		await client.ExecuteQueryAsync(
			$"DELETE FROM `{options.ProjectId}.{options.DatasetId}.{Tables.MetricSnapshots}` WHERE source = @source",
			[new BigQueryParameter("source", BigQueryDbType.String, source)]);
	}
}
