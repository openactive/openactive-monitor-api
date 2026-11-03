using MonitorApi.Models.Admin;

namespace MonitorApi.Services.Admin;

/// <summary>Turns router actions into the rows <c>/admin/actions</c> returns.</summary>
public static class MonitorActionRows
{
    public static MonitorActionRow From(MonitorAction action, DatasetMetadata? dataset, FeedMetadata? feed)
    {
        // The feed's own row is the most specific place to get the publisher from.
        var publisherName = feed?.PublisherName ?? dataset?.PublisherName;
        var datasetInfo = dataset ?? new DatasetMetadata(action.DatasetUrl, null, null);

        return new MonitorActionRow
        {
            Kind = action.Kind,
            DatasetUrl = action.DatasetUrl,
            DatasetName = datasetInfo.Name,
            PublisherId = "pub_" + AdminSlug.Of(publisherName),
            PublisherName = publisherName ?? "",
            FeedId = action.FeedId,
            FeedName = action.FeedId is null ? null : feed?.FeedName ?? action.FeedId,
            FeedType = feed?.FeedType,
            FeedUrl = feed?.FeedUrl,
            FirstDetected = action.FirstDetected,
            ConsecutiveDays = action.ConsecutiveDays,
            Share = action.Share,
            SeasonalCheck = action.SeasonalCheck,
            Evidence = Evidence(action.Incident),
            Suppressed = action.Suppressed,
        };
    }

    private static Dictionary<string, object?> Evidence(object incident) => incident switch
    {
        FeedIngestionError e => new()
        {
            ["last_completed"] = e.LastCompleted,
            ["error_code"] = e.ErrorCode,
            ["error_message"] = e.ErrorMessage,
        },
        AuthRequired a => new()
        {
            ["first_failed"] = a.FirstFailed,
            ["error_code"] = a.ErrorCode,
            ["error_message"] = a.ErrorMessage,
        },
        DatasetStall s => new()
        {
            ["last_published"] = s.LastPublished,
            ["feed_count"] = s.Feeds.Count,
        },
        SingleFeedStall s => new()
        {
            ["last_published"] = s.LastPublished,
        },
        IdPatternIncident i => new()
        {
            ["orphan_count"] = i.Orphans.OrphanCount,
            ["orphan_share"] = i.Orphans.OrphanShare,
            ["by_kind"] = i.ByKind.Select(k => new Dictionary<string, object?>
            {
                ["kind"] = k.Kind,
                ["referenced_patterns"] = k.Mismatch.ReferencedPatterns,
                ["parent_patterns"] = k.Mismatch.ParentPatterns,
                ["example_referenced_ids"] = k.Mismatch.ExampleReferencedIds,
                ["example_parent_ids"] = k.Mismatch.ExampleParentIds,
            }).ToList(),
        },
        OrphanedChildren o => new()
        {
            ["orphan_count"] = o.OrphanCount,
            ["child_count"] = o.ChildCount,
            ["missing_parent_count"] = o.MissingParentCount,
            ["example_missing_parents"] = o.MissingParents.Select(p => p.MissingId).ToList(),
        },
        DatasetFutureDecline d => new()
        {
            ["start_total"] = d.StartTotal,
            ["current_total"] = d.CurrentTotal,
            ["drop"] = d.Drop,
            ["drop_percent"] = d.DropPercent,
        },
        _ => [],
    };
}