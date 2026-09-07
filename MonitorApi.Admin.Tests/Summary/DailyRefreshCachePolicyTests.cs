using MonitorApi;

namespace MonitorApi.Admin.Tests.Summary;

/// <summary>
/// The admin cache expires at a wall-clock time rather than after a fixed span, so every cached
/// response is discarded at the same moment each morning whenever it was stored.
/// </summary>
public class DailyRefreshCachePolicyTests
{
	private static readonly DailyRefreshCachePolicy Policy = new(new TimeOnly(7, 0));

	[Fact]
	public void BeforeTheRefresh_ExpiresThatMorning()
	{
		Assert.Equal(TimeSpan.FromHours(1), Policy.TimeUntilRefresh(new DateTime(2026, 9, 2, 6, 0, 0, DateTimeKind.Utc)));
	}

	[Fact]
	public void AfterTheRefresh_ExpiresTheFollowingMorning()
	{
		Assert.Equal(TimeSpan.FromHours(16), Policy.TimeUntilRefresh(new DateTime(2026, 9, 2, 15, 0, 0, DateTimeKind.Utc)));
	}

	[Fact]
	public void ExactlyAtTheRefresh_GetsTheWholeDayRatherThanExpiringImmediately()
	{
		Assert.Equal(TimeSpan.FromHours(24), Policy.TimeUntilRefresh(new DateTime(2026, 9, 2, 7, 0, 0, DateTimeKind.Utc)));
	}

	[Fact]
	public void JustBeforeMidnight_StillExpiresAtTheNextRefreshNotAtMidnight()
	{
		Assert.Equal(
			TimeSpan.FromHours(7) + TimeSpan.FromMinutes(1),
			Policy.TimeUntilRefresh(new DateTime(2026, 9, 2, 23, 59, 0, DateTimeKind.Utc)));
	}

	[Fact]
	public void ExpiryIsNeverZeroOrNegative()
	{
		for (var minute = 0; minute < 24 * 60; minute++)
		{
			var at = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc).AddMinutes(minute);
			Assert.InRange(Policy.TimeUntilRefresh(at), TimeSpan.FromMinutes(1), TimeSpan.FromDays(1));
		}
	}
}
