using System.Net;

namespace MonitorApi.Admin.Tests;

/// <summary>
/// The admin surface is gated on <c>Api:AdminToken</c> and must not accept the public analytics token,
/// nor should the public surface accept the admin token.
/// </summary>
public class AdminAuthTests(AdminApiFixture fixture) : IClassFixture<AdminApiFixture>
{
	private readonly AdminApiFixture _fixture = fixture;

	public static TheoryData<string> AdminRoutes() =>
	[
		"/admin/summary",
		"/admin/single-feed-stall-incidents",
		"/admin/single-feed-stall-trend",
	];

	[Theory]
	[MemberData(nameof(AdminRoutes))]
	public async Task NoToken_IsForbidden(string route)
	{
		using var client = _fixture.CreateClient();
		var response = await client.GetAsync(route);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Theory]
	[MemberData(nameof(AdminRoutes))]
	public async Task WrongToken_IsForbidden(string route)
	{
		using var client = _fixture.CreateClient();
		var response = await client.GetAsync(AdminApiFixture.WithToken(route, "not-the-admin-token"));

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Theory]
	[MemberData(nameof(AdminRoutes))]
	public async Task PublicAccessToken_IsNotAcceptedByAdminEndpoints(string route)
	{
		Assert.NotEqual(_fixture.PublicAccessToken, _fixture.AdminToken);

		using var client = _fixture.CreateClient();
		var response = await client.GetAsync(AdminApiFixture.WithToken(route, _fixture.PublicAccessToken));

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Theory]
	[MemberData(nameof(AdminRoutes))]
	public async Task AdminToken_IsAccepted(string route)
	{
		using var client = _fixture.CreateClient();
		var response = await client.GetAsync(_fixture.WithAdminToken(route));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Fact]
	public async Task AdminToken_IsNotAcceptedByPublicEndpoints()
	{
		using var client = _fixture.CreateClient();
		var response = await client.GetAsync(AdminApiFixture.WithToken("/publishers", _fixture.AdminToken));

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}
}
