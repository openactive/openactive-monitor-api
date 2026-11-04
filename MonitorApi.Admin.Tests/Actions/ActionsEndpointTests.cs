using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MonitorApi.Models.Admin;
using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.Actions;

/// <summary>
/// Live-data tests for <c>/admin/actions</c>. The routing rules are covered by the router tests;
/// these only check things that hold whatever the data looks like on the day.
/// </summary>
public class ActionsEndpointTests(AdminApiFixture fixture) : IClassFixture<AdminApiFixture>
{
    private const string Route = "/admin/actions";

    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static readonly string[] KnownKinds =
    [
        MonitorActionKind.HttpEndpointFailure,
        MonitorActionKind.AuthenticationRequired,
        MonitorActionKind.DatasetStall,
        MonitorActionKind.SingleFeedStall,
        MonitorActionKind.IdPatternMismatch,
        MonitorActionKind.OrphanedChildren,
        MonitorActionKind.FutureDecline,
    ];

    private static readonly string[] FeedLevelKinds =
    [
        MonitorActionKind.HttpEndpointFailure,
        MonitorActionKind.AuthenticationRequired,
        MonitorActionKind.SingleFeedStall,
    ];

    private async Task<AdminPage<MonitorActionRow>> Get(string query = "")
    {
        using var client = fixture.CreateClient();
        var response = await client.GetAsync(fixture.WithAdminToken(Route + query));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AdminPage<MonitorActionRow>>(JsonOptions))!;
    }

    [Fact]
    public async Task ReturnsTheDocumentedEnvelope()
    {
        var page = await Get();

        Assert.NotNull(page.Data);
        Assert.Equal(1, page.Meta.Page);
        Assert.Equal(500, page.Meta.PageSize);
        Assert.True(page.Meta.Total >= page.Data.Count);
        Assert.True(page.Meta.SnapshotDate > DateOnly.MinValue);
    }

    [Fact]
    public async Task EveryRowMakesSense()
    {
        var page = await Get();

        Assert.All(page.Data, row =>
        {
            Assert.Contains(row.Kind, KnownKinds);
            Assert.NotEmpty(row.DatasetUrl);
            Assert.NotEmpty(row.DatasetName);
            Assert.StartsWith("pub_", row.PublisherId);
            Assert.NotNull(row.Evidence);

            if (FeedLevelKinds.Contains(row.Kind))
            {
                Assert.NotNull(row.FeedId);
            }
            else
            {
                Assert.Null(row.FeedId);
            }

            Assert.All(row.Suppressed, s => Assert.StartsWith("superseded_by:", s.Reason));
        });
    }

    [Fact]
    public async Task RowsComeWorstKindFirst()
    {
        var page = await Get();

        var priorities = page.Data.Select(r => ActionRouter.Priority(r.Kind)).ToList();
        Assert.Equal(priorities.Order(), priorities);
    }

    [Fact]
    public async Task KindFilter_OnlyReturnsThatKind()
    {
        var page = await Get("?kind=" + MonitorActionKind.DatasetStall);

        Assert.All(page.Data, row => Assert.Equal(MonitorActionKind.DatasetStall, row.Kind));
    }

    [Fact]
    public async Task Paging_ReturnsOneRowPerPage()
    {
        var all = await Get();
        var first = await Get("?page_size=1");

        Assert.Equal(1, first.Meta.PageSize);
        Assert.Equal(all.Meta.Total, first.Meta.Total);
        Assert.Equal(Math.Min(1, all.Meta.Total), first.Data.Count);
    }

    [Fact]
    public async Task AsOf_IsTheSnapshotDate()
    {
        var latest = await Get();
        var dayBefore = latest.Meta.SnapshotDate.AddDays(-1);

        var page = await Get($"?as_of={dayBefore:yyyy-MM-dd}");

        Assert.Equal(dayBefore, page.Meta.SnapshotDate);
    }
}