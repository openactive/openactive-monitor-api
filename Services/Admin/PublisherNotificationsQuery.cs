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

    /// <summary>Delete one row by id.</summary>
    public static string DeleteSql(string table) =>
        $"""
		DELETE FROM {table}
		WHERE id = @id
		""";

    /// <summary>Values for GetByIdSql / DeleteSql.</summary>
    public static BigQueryParameter IdParameter(string id) =>
        new("id", BigQueryDbType.String, id);

    /// <summary>Status counts and alert totals (Slack thresholds on problems[], plus contact follow-up).</summary>
    public static string SummarySql(string table) =>
       $"""
       SELECT COUNT(*) AS publishers,
              COUNTIF(status = 'open') AS open,
              COUNTIF(status = 'in_progress') AS in_progress,
              COUNTIF(status = 'closed') AS closed,
              COUNTIF(date_contacted IS NULL AND status IN ('open', 'in_progress')) AS never_contacted,
              COUNTIF(
                status IN ('open', 'in_progress')
                AND (
                  date_contacted IS NULL
                  OR date_contacted < DATE_SUB(CURRENT_DATE(), INTERVAL 5 DAY)
                  OR EXISTS (
                    SELECT 1
                    FROM UNNEST(IF(problems IS NULL, [], JSON_QUERY_ARRAY(problems))) AS problem
                    WHERE
                      (
                        JSON_VALUE(problem, '$.monitor_id') = 'dataset_stall'
                        AND SAFE_CAST(JSON_VALUE(problem, '$.days_open') AS INT64) >= 5
                      )
                      OR (
                        JSON_VALUE(problem, '$.monitor_id') = 'single_feed_stall'
                        AND JSON_VALUE(problem, '$.feed_type') IN ('Slot', 'ScheduledSession')
                        AND SAFE_CAST(JSON_VALUE(problem, '$.days_open') AS INT64) >= 10
                      )
                      OR (
                        JSON_VALUE(problem, '$.monitor_id') IN ('feed_ingestion_error', 'http_endpoint_failure')
                        AND SAFE_CAST(JSON_VALUE(problem, '$.days_open') AS INT64) >= 10
                      )
                  )
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

    /// <summary>Values for InsertSql's @parameters.</summary>
    public static BigQueryParameter[] InsertParameters(PublisherNotificationBody body) =>
        WriteParameters(body.Id, body);

     /// <summary>
    /// Partial update of one row. A NULL parameter keeps the column's current value, so a caller only
    /// changes what it sends. Id comes from the URL; created_at is never touched.
    /// </summary>
    public static string UpdateSql(string table) =>
        $"""
        UPDATE {table}
        SET publisher_id = COALESCE(@publisher_id, publisher_id),
            publisher_name = COALESCE(@publisher_name, publisher_name),
            problem_count = COALESCE(@problem_count, problem_count),
            monitors = IF(@set_monitors, @monitors, monitors),
            oldest_days_open = COALESCE(@oldest_days_open, oldest_days_open),
            status = COALESCE(@status, status),
            stakeholder = COALESCE(@stakeholder, stakeholder),
            contact = COALESCE(@contact, contact),
            date_contacted = COALESCE(@date_contacted, date_contacted),
            notes = COALESCE(@notes, notes),
            problems = COALESCE(PARSE_JSON(@problems), problems),
            updated_at = CURRENT_TIMESTAMP()
        WHERE id = @id
        """;

    /// <summary>Values for UpdateSql's @parameters. A null field becomes a NULL parameter, meaning "keep".</summary>
    public static BigQueryParameter[] UpdateParameters(string id, PublisherNotificationPatch patch) =>
    [
        new("id", BigQueryDbType.String, id),
        new("publisher_id", BigQueryDbType.String, patch.PublisherId),
        new("publisher_name", BigQueryDbType.String, patch.PublisherName),
        new("problem_count", BigQueryDbType.Int64, patch.ProblemCount),
        new("set_monitors", BigQueryDbType.Bool, patch.Monitors is not null),
        new("monitors", BigQueryDbType.Array, (patch.Monitors ?? []).ToList()) { ArrayElementType = BigQueryDbType.String },
        new("oldest_days_open", BigQueryDbType.Int64, patch.OldestDaysOpen),
        new("status", BigQueryDbType.String, patch.Status),
        new("stakeholder", BigQueryDbType.String, patch.Stakeholder),
        new("contact", BigQueryDbType.String, patch.Contact),
        new("date_contacted", BigQueryDbType.Date, patch.DateContacted?.ToDateTime(TimeOnly.MinValue)),
        new("notes", BigQueryDbType.String, patch.Notes),
        new("problems", BigQueryDbType.String, patch.Problems?.GetRawText()),
    ];

    private static BigQueryParameter[] WriteParameters(string id, PublisherNotificationBody body) =>
    [
        new("id", BigQueryDbType.String, id),
        new("publisher_id", BigQueryDbType.String, body.PublisherId),
        new("publisher_name", BigQueryDbType.String, body.PublisherName),
        new("problem_count", BigQueryDbType.Int64, body.ProblemCount),
        new("monitors", BigQueryDbType.Array, body.Monitors.ToList()) { ArrayElementType = BigQueryDbType.String },
        new("oldest_days_open", BigQueryDbType.Int64, body.OldestDaysOpen),
        new("status", BigQueryDbType.String, body.Status),
        new("stakeholder", BigQueryDbType.String, body.Stakeholder),
        new("contact", BigQueryDbType.String, body.Contact),
        new("date_contacted", BigQueryDbType.Date, body.DateContacted?.ToDateTime(TimeOnly.MinValue)),
        new("notes", BigQueryDbType.String, body.Notes),
        new("problems", BigQueryDbType.String, body.Problems?.GetRawText() ?? "null"),
    ];


    /// <summary>
    /// Saves scanned publishers. New publishers get a new open row; existing rows only have their
    /// problem fields replaced, so the steward's status, stakeholder, contact, date and notes stay put.
    /// </summary>
    public static string ScanMergeSql(string table) =>
        $"""
        MERGE {table} AS t
        USING (
          SELECT id,
                 @publisher_ids[OFFSET(i)] AS publisher_id,
                 @publisher_names[OFFSET(i)] AS publisher_name,
                 @problem_counts[OFFSET(i)] AS problem_count,
                 SPLIT(@monitors[OFFSET(i)], ',') AS monitors,
                 NULLIF(@oldest_days_open[OFFSET(i)], -1) AS oldest_days_open,
                 PARSE_JSON(@problems[OFFSET(i)]) AS problems
          FROM UNNEST(@ids) AS id WITH OFFSET AS i
        ) AS s
        ON t.id = s.id
        WHEN MATCHED THEN UPDATE SET
          publisher_id = s.publisher_id,
          publisher_name = s.publisher_name,
          problem_count = s.problem_count,
          monitors = s.monitors,
          oldest_days_open = s.oldest_days_open,
          problems = s.problems,
          updated_at = CURRENT_TIMESTAMP()
        WHEN NOT MATCHED THEN INSERT (
          id, publisher_id, publisher_name, problem_count, monitors,
          oldest_days_open, status, problems, created_at, updated_at
        ) VALUES (
          s.id, s.publisher_id, s.publisher_name, s.problem_count, s.monitors,
          s.oldest_days_open, 'open', s.problems, CURRENT_TIMESTAMP(), CURRENT_TIMESTAMP()
        )
        """;

    public static BigQueryParameter[] ScanMergeParameters(IReadOnlyList<ScannedPublisher> publishers) =>
    [
        Strings("ids", publishers.Select(p => p.Id)),
        Strings("publisher_ids", publishers.Select(p => p.PublisherId)),
        Strings("publisher_names", publishers.Select(p => p.PublisherName)),
        Longs("problem_counts", publishers.Select(p => (long)p.ProblemCount)),
        // BigQuery can't hold a list inside a list, so each publisher's monitors go in as one string.
        Strings("monitors", publishers.Select(p => string.Join(',', p.Monitors))),
        // Or nulls inside a list parameter, so "no oldest day" is sent as -1 and turned back into NULL.
        Longs("oldest_days_open", publishers.Select(p => (long)(p.OldestDaysOpen ?? -1))),
        Strings("problems", publishers.Select(p => p.ProblemsJson)),
    ];

    private static BigQueryParameter Strings(string name, IEnumerable<string> values) =>
        new(name, BigQueryDbType.Array, values.ToList()) { ArrayElementType = BigQueryDbType.String };

    private static BigQueryParameter Longs(string name, IEnumerable<long> values) =>
        new(name, BigQueryDbType.Array, values.ToList()) { ArrayElementType = BigQueryDbType.Int64 };

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