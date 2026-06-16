using System.Net;

namespace MonitorApi.Tests;

public class AuthTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
	[Fact]
	public async Task NoToken_Returns403()
	{
		using var client = fixture.CreateAuthenticatedClient();

		var response = await client.GetAsync("/summary");

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task WrongToken_Returns403()
	{
		using var client = fixture.CreateAuthenticatedClient();

		var response = await client.GetAsync("/summary?token=not-the-real-token");

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task ValidToken_DoesNotReturn403()
	{
		using var client = fixture.CreateAuthenticatedClient();

		var response = await client.GetAsync(fixture.WithToken("/summary"));

		Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
	}
}
