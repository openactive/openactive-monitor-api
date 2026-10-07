using System.Collections;
using Google.Cloud.BigQuery.V2;
using MonitorApi.Models;
using MonitorApi.Models.Admin;

namespace MonitorApi.Services.Admin;

/// <summary>SQL for the publisher notification list. Table names are passed in already fully qualified.</summary>
internal static class PublisherNotificationsQuery
{
    public static string ListSql(string table) =>
        $"""
        SELECT id,  
               publisher_id,
               publisher_name,
               problem_count,
               monitors,
               oldest_days_open,
               status,
               stakeholder,
               contact,
               date_contacted,
               notes,
               problems,
               created_at,
               updated_at
        FROM {table}
        ORDER BY oldest_days_open DESC NULLS LAST, publisher_name
        """;

    /// <summary> Fetch one row by id. </summary>
    public static string GetByIdSql(string table) =>
        $"""
        SELECT id, publisher_id, publisher_name, problem_count, monitors,
               oldest_days_open, status, stakeholder, contact, date_contacted,
               notes, problems, created_at, updated_at
        FROM {table}
        WHERE id = @id
        """;

    /// <summary> Status counts and alert totals for the summary endpoint. </summary>
    public static string SummarySql(string table) =>
       $"""
       SELECT COUNT(*) AS publishers,
              COUNTIF(status = 'open') AS open,
              COUNTIF(status = 'in_progress') AS in_progress,
              COUNTIF(status = 'closed') AS closed,
              COUNTIF(date_contacted IS NULL) AS never_contacted,
              COUNTIF(
                status IN ('open', 'in_progress')
                AND (
                  date_contacted IS NULL
                  OR date_contacted < DATE_SUB(CURRENT_DATE(), INTERVAL 5 DAY)
                  OR IFNULL(oldest_days_open, 0) >= 5
                )
              ) AS alerts
       FROM {table}
       """;

    /// <summary> Insert one notification row. Timestamps come from BigQuery. </summary>
    public static string InsertSql(string table) =>
       $"""
       INSERT INTO {table} (
         id, publisher_id, publisher_name, problem_count, monitors,
         oldest_days_open, status, stakeholder, contact, date_contacted,
         notes, problems, created_at, updated_at
       ) VALUES (
         @id, @publisher_id, @publisher_name, @problem_count, @monitors,
         @oldest_days_open, @status, @stakeholder, @contact, @date_contacted,
         @notes, PARSE_JSON(@problems), CURRENT_TIMESTAMP(), CURRENT_TIMESTAMP()
       )
       """;

    // <summary> Values for InsertSql's @parameters. </summary>
    public static BigQueryParameter[] InsertParameters(
        string id,
        string? publisherId,
        string? publisherName,
        long? problemCount,
        IReadOnlyList<string> monitors,
        long? oldestDaysOpen,
        string? status,
        string? stakeholder,
        string? contact,
        DateOnly? dateContacted,
        string? notes,
        string? problemsJson) =>
    [
        new("id", BigQueryDbType.String, id),
        new("publisher_id", BigQueryDbType.String, publisherId),
        new("publisher_name", BigQueryDbType.String, publisherName),
        new("problem_count", BigQueryDbType.Int64, problemCount),
        new("monitors", BigQueryDbType.Array, monitors.ToList()) { ArrayElementType = BigQueryDbType.String},
        new("oldest_days_open", BigQueryDbType.Int64, oldestDaysOpen),
        new("status", BigQueryDbType.String, status),
        new("stakeholder", BigQueryDbType.String, stakeholder),
        new("contact", BigQueryDbType.String, contact),
        new("date_contacted", BigQueryDbType.Date, dateContacted?.ToDateTime(TimeOnly.MinValue)),
        new("notes", BigQueryDbType.String, notes),
        new("problems", BigQueryDbType.String, problemsJson ?? "null"),
    ];

    /// <summary> Update steward fields on one row. </summary>
    public static string UpdateSql(string table) =>
       $"""
       UPDATE {table}
       SET status = @status,
            stakeholder = @stakeholder,
            contact = @contact,
            date_contacted = @date_contacted,
            notes = @notes,
            updated_at = CURRENT_TIMESTAMP()
       WHERE id = @id
       """;

    /// <summary> Values for UpdateSql's parameters. </summary>
    public static BigQueryParameter[] UpdateParameters(
        string id,
        string? status,
        string? stakeholder,
        string? contact,
        DateOnly? dateContacted,
        string? notes) =>
    [
        new("id", BigQueryDbType.String, id),
        new("status", BigQueryDbType.String, status),
        new("stakeholder", BigQueryDbType.String, stakeholder),
        new("contact", BigQueryDbType.String, contact),
        new("date_contacted", BigQueryDbType.Date, dateContacted?.ToDateTime(TimeOnly.MinValue)),
        new("notes", BigQueryDbType.String, notes),
    ];

    /// <summary>
    /// Reads the single summary row. Missing row or NULL columns become zero.
    /// </summary>
    public static PublisherNotificationSummary ParseSummary(Dictionary<string, object>? row) =>
        new()
        {
            Publishers = (int)(BigQueryValueParser.AsLong(row?.GetValueOrDefault("publishers")) ?? 0),
            Open = (int)(BigQueryValueParser.AsLong(row?.GetValueOrDefault("open")) ?? 0),
            InProgress = (int)(BigQueryValueParser.AsLong(row?.GetValueOrDefault("in_progress")) ?? 0),
            Closed = (int)(BigQueryValueParser.AsLong(row?.GetValueOrDefault("closed")) ?? 0),
            NeverContacted = (int)(BigQueryValueParser.AsLong(row?.GetValueOrDefault("never_contacted")) ?? 0),
            Alerts = (int)(BigQueryValueParser.AsLong(row?.GetValueOrDefault("alerts")) ?? 0),
        };

    /// <summary>
    /// Turns one BigQuery row dictionary into the wire model.
    /// <c>id</c> is required; every other column is nullable and may be absent when NULL.
    /// </summary>
    public static PublisherNotificationRow ParseRow(Dictionary<string, object> row) =>
        new()
        {
            Id = (string)row["id"],
            PublisherId = row.GetValueOrDefault("publisher_id") as string,
            PublisherName = row.GetValueOrDefault("publisher_name") as string,
            ProblemCount = BigQueryValueParser.AsLong(row.GetValueOrDefault("problem_count")),
            OldestDaysOpen = BigQueryValueParser.AsLong(row.GetValueOrDefault("oldest_days_open")),
            Status = row.GetValueOrDefault("status") as string,
            Stakeholder = row.GetValueOrDefault("stakeholder") as string,
            Contact = row.GetValueOrDefault("contact") as string,
            Notes = row.GetValueOrDefault("notes") as string,
            Monitors = ParseMonitors(row.GetValueOrDefault("monitors")),
            Problems = BigQueryValueParser.ParseJson(row.GetValueOrDefault("problems")),
            DateContacted = ParseDate(row.GetValueOrDefault("date_contacted")),
            CreatedAt = ParseTimestamp(row.GetValueOrDefault("created_at")),
            UpdatedAt = ParseTimestamp(row.GetValueOrDefault("updated_at")),
        };

    /// <summary>
    /// BigQuery REPEATED STRING arrives as a sequence of strings.
    /// Missing or unexpected shapes become an empty list, never null.
    /// </summary>
    private static IReadOnlyList<string> ParseMonitors(object? cell)
    {
        if (cell is null)
            return [];

        if (cell is string single)
            return [single];

        if (cell is not IEnumerable items)
            return [];

        var monitors = new List<string>();
        foreach (var item in items)
        {
            if (item is string s && s.Length > 0)
                monitors.Add(s);
            else if (item?.ToString() is { Length: > 0 } text)
                monitors.Add(text);
        }
        return monitors;
    }

    private static DateOnly? ParseDate(object? value) => value switch
    {
        DateTime dateTime => DateOnly.FromDateTime(dateTime),
        DateOnly day => day,
        not null when value.ToString() is { Length: > 0 } text &&
            DateOnly.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    private static DateTime? ParseTimestamp(object? value) =>
    value is DateTime dt ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : null;
}