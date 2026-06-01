namespace MonitorApi.Models;

public class PaginatedResponse<T>
{
	public required IReadOnlyList<T> Items { get; init; }

	public required int Offset { get; init; }

	public required int Limit { get; init; }

	public required bool HasMore { get; init; }
}
