using System.Globalization;
using Google.Cloud.BigQuery.V2;
using MonitorApi.Models;
using MonitorApi.Models.Admin;

namespace MonitorApi.Services.Admin;

/// <summary>
/// SQL and row parsing for the metric snapshot store. Table names are passed in already fully
/// qualified; every caller-supplied value travels as a <see cref="BigQueryParameter"/>.
/// </summary>
internal static class MetricSnapshotsQuery
{
	/// <summary>Column list, in table order. Shared so SELECT and INSERT cannot drift apart.</summary>
	private const string Columns = "period, source, field, value, denominator, snapshot_date, captured_at";

	/// <summary>
	/// Read the series, narrowed by any combination of source, field and period range. An empty array
	/// parameter means "no filter on this column" rather than "match nothing".
	/// </summary>
	public static string ListSql(string table) =>
		$"""
		SELECT {Columns}
		FROM {table}
		WHERE (ARRAY_LENGTH(@sources) = 0 OR source IN UNNEST(@sources))
		  AND (ARRAY_LENGTH(@fields) = 0 OR field IN UNNEST(@fields))
		  AND (@from IS NULL OR period >= @from)
		  AND (@to IS NULL OR period <= @to)
		ORDER BY period DESC, source, field
		""";

	/// <summary>Values for <see cref="ListSql"/>.</summary>
	public static BigQueryParameter[] ListParameters(
		IReadOnlyList<string> sources,
		IReadOnlyList<string> fields,
		DateOnly? from,
		DateOnly? to) =>
	[
		StringArray("sources", sources),
		StringArray("fields", fields),
		Date("from", from),
		Date("to", to),
	];

	/// <summary>
	/// Every row within the box a batch spans: the periods it uses, crossed with the sources and the
	/// fields it uses. Deliberately over-selects, because the three columns are matched independently
	/// rather than as triples — the caller intersects the result with the batch's own keys, which is
	/// exact. The create endpoint runs it twice: once before the insert to find clashes, and once
	/// after to read the written rows back.
	/// </summary>
	public static string BoxSql(string table) =>
		$"""
		SELECT {Columns}
		FROM {table}
		WHERE period IN UNNEST(@periods)
		  AND source IN UNNEST(@sources)
		  AND field IN UNNEST(@fields)
		""";

	/// <summary>Values for <see cref="BoxSql"/>, taken from the rows being written.</summary>
	public static BigQueryParameter[] BoxParameters(IReadOnlyList<MetricSnapshotWrite> rows) =>
	[
		new("periods", BigQueryDbType.Array, rows.Select(r => r.Period.ToDateTime(TimeOnly.MinValue)).Distinct().ToList())
		{
			ArrayElementType = BigQueryDbType.Date,
		},
		StringArray("sources", rows.Select(r => r.Source).Distinct().ToList()),
		StringArray("fields", rows.Select(r => r.Field).Distinct().ToList()),
	];

	/// <summary>
	/// One multi-row INSERT for the whole batch, so a capture of fifteen fields costs one statement
	/// rather than fifteen. Parameter names are generated from the row index, never from input.
	/// </summary>
	/// <remarks>
	/// <c>captured_at</c> is <c>CURRENT_TIMESTAMP()</c> and not a parameter: the row records when the
	/// API wrote it, which is not something a caller should be able to claim.
	/// </remarks>
	public static (string Sql, BigQueryParameter[] Parameters) Insert(
		string table,
		IReadOnlyList<MetricSnapshotWrite> rows)
	{
		var values = new List<string>(rows.Count);
		var parameters = new List<BigQueryParameter>(rows.Count * 6);

		for (var i = 0; i < rows.Count; i++)
		{
			var row = rows[i];
			values.Add(
				$"(@period_{i}, @source_{i}, @field_{i}, @value_{i}, @denominator_{i}, @snapshot_date_{i}, CURRENT_TIMESTAMP())");

			parameters.Add(Date($"period_{i}", row.Period));
			parameters.Add(new($"source_{i}", BigQueryDbType.String, row.Source));
			parameters.Add(new($"field_{i}", BigQueryDbType.String, row.Field));
			parameters.Add(new($"value_{i}", BigQueryDbType.Float64, row.Value));
			parameters.Add(new($"denominator_{i}", BigQueryDbType.Float64, row.Denominator));
			parameters.Add(Date($"snapshot_date_{i}", row.SnapshotDate));
		}

		var sql =
			$"""
			INSERT INTO {table} ({Columns})
			VALUES {string.Join(",\n       ", values)}
			""";

		return (sql, parameters.ToArray());
	}

	/// <summary>
	/// The identity of a measurement: period, source and field together. The table has no primary
	/// key, so this is what the create endpoint checks against to keep one row per measure per month.
	/// </summary>
	public static string Key(DateOnly period, string source, string field) =>
		$"{period:yyyy-MM-dd}|{source}|{field}";

	/// <summary>The key of a row about to be written.</summary>
	public static string Key(MetricSnapshotWrite row) => Key(row.Period, row.Source, row.Field);

	/// <summary>The key of a row read back from <see cref="BoxSql"/>.</summary>
	public static string Key(Dictionary<string, object> row) =>
		Key(ParseDate(row.GetValueOrDefault("period")) ?? default, (string)row["source"], (string)row["field"]);

	/// <summary>
	/// Turns one BigQuery row into the wire model. <c>period</c>, <c>source</c> and <c>field</c> are
	/// REQUIRED in the table and so are always present; the rest may be absent when NULL.
	/// </summary>
	public static MetricSnapshotRow ParseRow(Dictionary<string, object> row) =>
		new()
		{
			Period = ParseDate(row.GetValueOrDefault("period")) ?? default,
			Source = (string)row["source"],
			Field = (string)row["field"],
			Value = BigQueryValueParser.AsDouble(row.GetValueOrDefault("value")),
			Denominator = BigQueryValueParser.AsDouble(row.GetValueOrDefault("denominator")),
			SnapshotDate = ParseDate(row.GetValueOrDefault("snapshot_date")),
			CapturedAt = ParseTimestamp(row.GetValueOrDefault("captured_at")),
		};

	private static BigQueryParameter StringArray(string name, IReadOnlyList<string> values) =>
		new(name, BigQueryDbType.Array, values.ToList()) { ArrayElementType = BigQueryDbType.String };

	private static BigQueryParameter Date(string name, DateOnly? value) =>
		new(name, BigQueryDbType.Date, value?.ToDateTime(TimeOnly.MinValue));

	private static DateOnly? ParseDate(object? value) => value switch
	{
		DateTime dateTime => DateOnly.FromDateTime(dateTime),
		DateOnly day => day,
		not null when value.ToString() is { Length: > 0 } text &&
			DateOnly.TryParse(text, CultureInfo.InvariantCulture, out var parsed) => parsed,
		_ => null,
	};

	private static DateTime? ParseTimestamp(object? value) =>
		value is DateTime dt ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : null;
}
