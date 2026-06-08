using System.Text.Json;

namespace MonitorApi.Models;

internal static class BigQueryValueParser
{
	public static JsonElement? ParseJson(object? cell)
	{
		if (cell is null)
		{
			return null;
		}

		if (cell is JsonElement e)
		{
			return e;
		}

		if (cell is string s && !string.IsNullOrWhiteSpace(s))
		{
			using var doc = JsonDocument.Parse(s);
			return doc.RootElement.Clone();
		}

		return null;
	}

	public static double? AsDouble(object? value)
	{
		if (value is null)
		{
			return null;
		}

		if (value is double d)
		{
			return d;
		}

		if (value is float f)
		{
			return f;
		}

		if (value is decimal m)
		{
			return (double)m;
		}

		if (double.TryParse(value.ToString(), out var parsed))
		{
			return parsed;
		}

		return null;
	}

	public static long? AsLong(object? value)
	{
		if (value is null)
		{
			return null;
		}

		if (value is long l)
		{
			return l;
		}

		if (value is int i)
		{
			return i;
		}

		if (value is short s)
		{
			return s;
		}

		if (long.TryParse(value.ToString(), out var parsed))
		{
			return parsed;
		}

		return null;
	}
}
