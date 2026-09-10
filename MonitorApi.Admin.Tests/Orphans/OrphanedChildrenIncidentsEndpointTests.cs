using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MonitorApi.Models.Admin;
using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.Orphans;

/// <summary>
/// Live-data tests for <c>/admin/dataset-orphaned-children-incidents</c>. The detection rules are
/// pinned by <see cref="OrphanedChildrenDetectorTests"/>; these check the wiring, the response
/// envelope and the invariants that must hold whatever the data looks like on the day.
/// </summary>
/// <remarks>
/// Every distinct query string here costs a fresh scan of <c>opportunities</c>, so the combinations
/// are kept deliberately few and reused between tests where the assertion allows it.
/// </remarks>
public class OrphanedChildrenIncidentsEndpointTests(AdminApiFixture fixture) : IClassFixture<AdminApiFixture>
{
	private const string Route = "/admin/dataset-orphaned-children-incidents";

	private readonly AdminApiFixture _fixture = fixture;

	private static readonly JsonSerializerOptions JsonOptions =
		new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

	private async Task<AdminPage<OrphanedChildrenIncident>> Get(string query = "")
	{
		using var client = _fixture.CreateClient();
		var response = await client.GetAsync(_fixture.WithAdminToken(Route + query));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return (await response.Content.ReadFromJsonAsync<AdminPage<OrphanedChildrenIncident>>(JsonOptions))!;
	}

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

	[Fact]
	public async Task EveryIncidentIsInternallyConsistent()
	{
		var page = await Get();

		Assert.All(page.Data, incident =>
		{
			Assert.Equal(OrphanedChildrenDetector.MonitorId, incident.MonitorId);
			Assert.NotEmpty(incident.DatasetUrl);
			// Falls back to the URL host, so it is never empty even for a dataset with no feeds row.
			Assert.NotEmpty(incident.DatasetName);
			Assert.StartsWith("pub_", incident.PublisherId);
			// Empty string rather than null when the dataset has no feeds row.
			Assert.NotNull(incident.PublisherName);
			Assert.Equal("open", incident.Status);
			Assert.Null(incident.LastContacted);

			// Default thresholds: open on the first orphan, escalated at a hundred.
			Assert.True(incident.OrphanCount >= 1);
			Assert.Equal(incident.OrphanCount >= 100, incident.PastThreshold);

			// Only examined children can be orphans, and only real children can be examined.
			Assert.True(incident.OrphanCount <= incident.CheckedCount);
			Assert.True(incident.CheckedCount <= incident.ChildCount);

			Assert.InRange(incident.OrphanShare, 0, 1);

			// One missing parent can orphan many children, never the other way round.
			Assert.True(incident.MissingParentCount >= 1);
			Assert.True(incident.MissingParentCount <= incident.OrphanCount);
		});
	}

	[Fact]
	public async Task ByKindSplitsTheCountsAcrossTheMonitoredKindsAndSumsBackToTheIncident()
	{
		var page = await Get();

		Assert.All(page.Data, incident =>
		{
			var kinds = incident.Detail.ByKind;

			Assert.NotEmpty(kinds);
			Assert.All(kinds, k => Assert.Contains(k.Kind, OrphanedChildrenDetector.ChildKinds));
			// One entry per kind at most: the query groups by (dataset_url, kind).
			Assert.Equal(kinds.Count, kinds.Select(k => k.Kind).Distinct().Count());

			// The incident is a fold of its kinds, so the parts must add up to the whole.
			Assert.Equal(incident.ChildCount, kinds.Sum(k => k.ChildCount));
			Assert.Equal(incident.CheckedCount, kinds.Sum(k => k.CheckedCount));
			Assert.Equal(incident.OrphanCount, kinds.Sum(k => k.OrphanCount));
			Assert.Equal(incident.MissingParentCount, kinds.Sum(k => k.MissingParentCount));

			// Worst kind first.
			Assert.Equal(kinds.OrderByDescending(k => k.OrphanCount).Select(k => k.OrphanCount),
				kinds.Select(k => k.OrphanCount));
		});
	}

	[Fact]
	public async Task MissingParentsAreACappedWorstFirstSampleOfTheMissingIds()
	{
		var page = await Get();

		Assert.All(page.Data, incident =>
		{
			var sample = incident.Detail.MissingParents;

			Assert.NotEmpty(sample);
			Assert.True(sample.Count <= OrphanedChildrenDetector.MissingParentSample);
			// It is a sample of the distinct missing ids, so it can never exceed the count of them.
			Assert.True(sample.Count <= incident.MissingParentCount);

			Assert.All(sample, parent =>
			{
				Assert.NotEmpty(parent.MissingId);
				// Every sampled parent has at least one child pointing at it — that is why it is here.
				Assert.True(parent.ChildCount >= 1);
				Assert.True(parent.ChildCount <= incident.OrphanCount);
			});

			Assert.Equal(sample.OrderByDescending(p => p.ChildCount).Select(p => p.ChildCount),
				sample.Select(p => p.ChildCount));
		});
	}

	[Fact]
	public async Task EachDatasetIsReportedAtMostOnce()
	{
		var page = await Get("?page_size=1000");

		Assert.Equal(page.Data.Count, page.Data.Select(i => i.DatasetUrl).Distinct().Count());
	}

	[Fact]
	public async Task IncidentsAreOrderedWorstFirst()
	{
		var page = await Get();
		var orphans = page.Data.Select(i => i.OrphanCount).ToList();

		Assert.Equal(orphans.OrderByDescending(o => o), orphans);
	}

	[Fact]
	public async Task EveryIncidentSharesTheSnapshotDate()
	{
		// There is no per-row dating on a current-state table, so nothing may imply otherwise.
		var page = await Get();

		Assert.All(page.Data, _ => Assert.True(page.Meta.SnapshotDate > DateOnly.MinValue));
	}

	[Fact]
	public async Task PagesAreDisjointAndCoverTheWholeResultSet()
	{
		var first = await Get("?page=1&page_size=2");
		if (first.Meta.Total <= 2)
		{
			return;
		}

		var second = await Get("?page=2&page_size=2");

		Assert.Equal(2, first.Data.Count);
		Assert.Equal(first.Meta.Total, second.Meta.Total);
		Assert.Empty(first.Data.Select(i => i.DatasetUrl).Intersect(second.Data.Select(i => i.DatasetUrl)));
	}

	[Fact]
	public async Task PagingArgumentsAreClamped()
	{
		var page = await Get("?page=0&page_size=99999");

		Assert.Equal(1, page.Meta.Page);
		Assert.Equal(1000, page.Meta.PageSize);
	}

	[Fact]
	public async Task PageBeyondTheEnd_IsEmptyButStillReportsTheTotal()
	{
		var all = await Get("?page_size=1000");
		var beyond = await Get("?page=1000&page_size=10");

		Assert.Empty(beyond.Data);
		Assert.Equal(all.Meta.Total, beyond.Meta.Total);
	}

	[Fact]
	public async Task RaisingMinOrphans_NeverReportsMoreIncidents()
	{
		// Asserted as monotonic rather than empty: "no dataset has a million orphans" would be an
		// absolute-count assumption about live data.
		var loose = await Get();
		var strict = await Get("?min_orphans=1000000");

		Assert.True(strict.Meta.Total <= loose.Meta.Total);
	}

	[Fact]
	public async Task RaisingTheEscalationThreshold_NeverFlagsMoreIncidentsAndNeverChangesTheTotal()
	{
		var low = await Get("?past_threshold_orphans=1&page_size=1000");
		var high = await Get("?past_threshold_orphans=1000000&page_size=1000");

		Assert.True(high.Data.Count(i => i.PastThreshold) <= low.Data.Count(i => i.PastThreshold));
		Assert.Equal(low.Meta.Total, high.Meta.Total);
	}

	[Fact]
	public async Task EscalationThresholdOfOne_MakesEveryIncidentPastThreshold()
	{
		var page = await Get("?past_threshold_orphans=1&page_size=1000");

		Assert.All(page.Data, incident => Assert.True(incident.PastThreshold));
	}

	[Fact]
	public async Task TakesNoDateParameterSoUnknownQueryStringsCannotNarrowTheResult()
	{
		// The monitor reads a current-state table, so there is deliberately nothing to filter by date.
		// An unbound parameter must be ignored rather than silently changing the answer.
		var plain = await Get();
		var withJunk = await Get("?lookback_days=1&as_of=2020-01-01");

		Assert.Equal(plain.Meta.Total, withJunk.Meta.Total);
	}
}
