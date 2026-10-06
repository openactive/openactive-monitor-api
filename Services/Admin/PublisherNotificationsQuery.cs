using System.Collections;
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
            Monitors = [],
            Problems = null,
            DateContacted = null,
            CreatedAt = null,
            UpdatedAt = null,


        };
}