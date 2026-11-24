# BigQuery schema — `openactive-monitor.openactive_analytics`

A local snapshot of the analytics dataset's schema so you can write and review queries without going
to BigQuery for each one. Column names, types, nullability, partitioning, clustering and descriptions
come straight from `INFORMATION_SCHEMA`. Row counts and sizes are from `__TABLES__`.

**Snapshot taken 2026-11-23.** The schema changes when the upstream pipelines change. If a column
here doesn't match an error BigQuery gives you, the live table is right: regenerate this file (see
[Regenerating](#regenerating)) and don't work around the mismatch.

- Project `openactive-monitor`, dataset `openactive_analytics`, location `europe-west2`.
- Access is read-only. Run only `SELECT` statements, and always include a `LIMIT`.
- In code, refer to tables only via `Fq(Tables.X)` (`Tables.cs`). Never hardcode a table name.
- `JSON` columns are BigQuery `JSON`, not strings. Read them with `JSON_VALUE`, `JSON_QUERY` or
  `JSON_QUERY_ARRAY`, and unnest arrays with `UNNEST(JSON_QUERY_ARRAY(col))`.
- Nullable **NO** means the column is `REQUIRED`. Every other column can be `NULL`.
- **Partition** and **Cluster** columns: filter on them where you can, because they reduce the bytes scanned.

## Tables at a glance

| Table | Rows | Size | Grain | `Tables.` constant |
|---|---:|---:|---|---|
| [`active_opportunities_summary`](#active_opportunities_summary) | 9,161 | 2.1 MB | one row per (district, publisher, provider, activity/facility group), current state | `ActiveOpportunitiesSummary` |
| [`custom_properties`](#custom_properties) | 404 | 0.2 MB | one row per feed, current state | `CustomProperties` |
| [`feed_ingestion`](#feed_ingestion) | 190 | 7.7 MB | one row per daily feed-discovery run | `FeedIngestion` |
| [`feed_insights`](#feed_insights) | 97,725 | 93 MB | one row per (run_date, feed) | — |
| [`feed_quality`](#feed_quality) | 376 | 0.1 MB | one row per feed, current state | `FeedQuality` |
| [`feeds`](#feeds) | 506 | 0.3 MB | one row per feed, current state | `Feeds` |
| [`insight_category_counts`](#insight_category_counts) | 3,964,365 | 250 MB | one row per (run_date, category, scope, value) | — |
| [`insight_run_summary`](#insight_run_summary) | 231 | 0.1 MB | one row per insight run | `InsightRunSummary` |
| [`insight_sport_discipline`](#insight_sport_discipline) | 297,820 | 18 MB | one row per (run_date, sport_and_discipline, activity) | — |
| [`insight_sport_discipline_master`](#insight_sport_discipline_master) | 67,452 | 3.3 MB | one row per (run_date, sport/discipline) | — |
| [`opportunities`](#opportunities) | 9,693,480 | **15.1 GB** | one row per opportunity item, current state only | `Opportunities` |
| [`opportunity_ingestion`](#opportunity_ingestion) | 41,622 | 10 MB | one row per (ingestion_date, feed) crawl | `OpportunityIngestion` |
| [`opportunity_ingestion_backup_20260815`](#opportunity_ingestion_backup_20260815) | 46,727 | 9.3 MB | frozen backup, not updated | — |
| [`publisher_notifications`](#publisher_notifications) | 23 | <0.1 MB | one row per publisher notification | `PublisherNotifications` |
| [`socio_data`](#socio_data) | 413 | 0.1 MB | one row per area (district/region/country) | `SocioData` |

Row counts are from the snapshot date and change every day. Never assert on them in tests.

### Which code reads which table

| Table | Read by |
|---|---|
| `active_opportunities_summary` | `Controllers/ApiController.cs`, `Controllers/Admin/ActivityCoverageController.cs` |
| `custom_properties` | `Controllers/Admin/DataQualityController.cs` |
| `feed_ingestion` | `Controllers/Admin/EcosystemGrowthController.cs`, `Controllers/Admin/SummaryController.cs` |
| `feed_quality` | `Controllers/ApiController.cs`, `Controllers/Admin/DataQualityController.cs`, `Controllers/Admin/MonitorControllerBase.cs` |
| `feeds` | `Controllers/ApiController.cs`, `Controllers/Admin/DataQualityController.cs`, `Controllers/Admin/MonitorControllerBase.cs` |
| `insight_run_summary` | `Controllers/ApiController.cs` |
| `opportunities` | `Controllers/ApiController.cs`, `Controllers/Admin/MonitorControllerBase.cs` |
| `opportunity_ingestion` | `Controllers/Admin/EcosystemGrowthController.cs`, `Controllers/Admin/SummaryController.cs`, `Controllers/Admin/MonitorControllerBase.cs` |
| `publisher_notifications` | `Controllers/Admin/PublisherNotificationsController.cs` |
| `socio_data` | `Controllers/ApiController.cs` |

No code reads `feed_insights`, `insight_category_counts`, `insight_sport_discipline`,
`insight_sport_discipline_master` or `opportunity_ingestion_backup_20260815` yet. If you start using
one, add a constant for it to `Tables.cs`.

### Join keys

- **Feed:** `feed_id` (`opportunities`, `opportunity_ingestion`, `feed_quality`, `feed_insights`,
  `custom_properties`) = `feeds.id`.
- **Dataset:** `dataset_url` (`opportunities`, `feeds`, `feed_quality`, `feed_insights`,
  `custom_properties`, `publisher_notifications`). In `opportunity_ingestion` the column is
  `dataset_id`, not `dataset_url`.
- **Area:** `socio_data.area_code` = `district_code` / `region_code` / `country_code` on
  `opportunities` and `active_opportunities_summary` (ONS GSS codes).
- **Insight runs:** `run_date` is shared across the `feed_insights` and `insight_*` tables from the same run.

---

## active_opportunities_summary

Pre-aggregated counts of active opportunities, used for the analytics location, activity, organisation
and NHS trust breakdowns. Rebuilt in full each day, with no history.
Clustered by `district_name`, `provider`.

| Column | Type | Nullable | Notes |
|---|---|---|---|
| `district_name` | STRING | YES | cluster 1 |
| `publisher` | STRING | YES | |
| `provider` | STRING | YES | cluster 2 |
| `opportunity_count` | INT64 | YES | |
| `opportunity_count_narrow` | INT64 | YES | Like `opportunity_count` but excludes Slot rows. Each Slot's embedded (inline) superEvent facility is counted once, de-duplicated by superEvent `@id` within the group. |
| `is_activity` | BOOL | YES | TRUE if `activity_or_facility` holds activities. FALSE if it holds facilities (FacilityUse / IndividualFacilityUse / Slot). |
| `activity_or_facility` | JSON | YES | JSON array of activity labels, or of facility types for FacilityUse / IndividualFacilityUse / Slot |
| `organization_names` | JSON | YES | JSON array of the distinct `organization_name` values in the group |
| `district_code` | STRING | YES | LAD code from `000-district-region-country.json` |
| `region_code` | STRING | YES | |
| `region_name` | STRING | YES | |
| `country_code` | STRING | YES | |
| `country_name` | STRING | YES | |
| `nhstrust_name` | STRING | YES | NHS Trust name (TrustName), resolved from the opportunity's location via `000-location-nhstrusts.geojson`. NULL when the location is outside every trust. |
| `nhstrust_code` | STRING | YES | NHS Trust code (TrustCd) for the same trust as `nhstrust_name` |

## custom_properties

Non-standard (custom or beta) properties found in a sample of each feed's opportunities. One row per
feed, current state. Clustered by `dataset_url`, `feed_id`.

| Column | Type | Nullable | Notes |
|---|---|---|---|
| `dataset_url` | STRING | **NO** | cluster 1 |
| `dataset_name` | STRING | YES | |
| `publisher_name` | STRING | YES | |
| `feed_id` | STRING | **NO** | cluster 2 |
| `feed_url` | STRING | YES | |
| `feed_type` | STRING | YES | |
| `is_regular` | BOOL | YES | |
| `sampled_items` | INT64 | YES | Opportunity rows sampled for this feed |
| `num_custom_properties` | INT64 | YES | Distinct custom property names the feed uses |
| `num_custom_property_usages` | INT64 | YES | Distinct (entity_type, property) pairs |
| `custom_properties` | ARRAY&lt;STRUCT&gt; | **NO** | One entry per (property, entity_type). Fields below. |
| &nbsp;&nbsp;`.property` | STRING | | Property key as published, e.g. `beta:formattedDescription` |
| &nbsp;&nbsp;`.property_kind` | STRING | | `beta` \| `prefixed` \| `unprefixed` |
| &nbsp;&nbsp;`.namespace` | STRING | | Prefix before `:`. NULL if the key is unprefixed or a full URI. |
| &nbsp;&nbsp;`.entity_type` | STRING | | `@type` of the object the property belongs to |
| &nbsp;&nbsp;`.occurrences` | INT64 | | Sampled entity instances that contain the property |
| &nbsp;&nbsp;`.entity_instances` | INT64 | | Sampled instances of `entity_type` in the feed |
| &nbsp;&nbsp;`.presence_pct` | FLOAT64 | | `occurrences / entity_instances * 100` |
| `vocab_source` | STRING | YES | Vocabulary used to decide which properties are known |
| `last_assessed` | TIMESTAMP | **NO** | |

## feed_ingestion

One row per daily feed-discovery run, recording which catalogues, datasets and feeds were found.
Ingestion dates run from 2026-05-07 to the present. There is no partitioning or clustering.

| Column | Type | Nullable | Notes |
|---|---|---|---|
| `ingestion_date` | TIMESTAMP | YES | Feed ingestion date |
| `number_of_feeds` | INT64 | YES | Number of ingested feeds |
| `feed_ids` | JSON | YES | JSON array of ingested feed ids |
| `number_of_catalogues` | INT64 | YES | Number of main catalogues |
| `catalogues` | JSON | YES | JSON array of catalogue URLs |
| `number_of_datasets` | INT64 | YES | Number of datasets |
| `datasets` | JSON | YES | JSON array of dataset URLs |

## feed_insights

Per-feed insight metrics for each insight run. **Partitioned by `run_date`**, clustered by
`run_date`, `dataset_url`, `feed_id`.

| Column | Type | Nullable | Notes |
|---|---|---|---|
| `run_date` | TIMESTAMP | **NO** | partition; cluster 1 |
| `feed_id` | STRING | **NO** | cluster 3 |
| `dataset_url` | STRING | YES | cluster 2 |
| `dataset_name` | STRING | YES | |
| `publisher_name` | STRING | YES | |
| `feed_url` | STRING | YES | |
| `license_url` | STRING | YES | |
| `logo_url` | STRING | YES | |
| `feed_type` | STRING | YES | |
| `event_type` | STRING | YES | `superevent` \| `subevent` \| NULL |
| `is_regular` | BOOL | YES | |
| `status` | STRING | YES | Latest `opportunity_ingestion` status (`COMPLETE` \| `ERROR` \| `WARNING`) |
| `num_items` | INT64 | YES | |
| `num_analysis_items` | INT64 | YES | |
| `num_opportunity_items` | INT64 | YES | |
| `num_future_opportunity_items` | INT64 | YES | |
| `num_future_week_opportunity_items` | INT64 | YES | |
| `num_opportunity_start_dates` | INT64 | YES | |
| `num_future_opportunity_start_dates` | INT64 | YES | |
| `num_future_week_opportunity_start_dates` | INT64 | YES | |
| `item_kinds_counts` | JSON | YES | |
| `item_types_counts` | JSON | YES | |
| `organizer_names_counts` | JSON | YES | |
| `activities_counts` | JSON | YES | |
| `facilities_counts` | JSON | YES | |
| `accessibilities_counts` | JSON | YES | |
| `regions_counts` | JSON | YES | |
| `districts_counts` | JSON | YES | |
| `trusts_counts` | JSON | YES | |

## feed_quality

Quality assessment of each feed: completeness, vocabulary adherence, grade and score. One row per
feed, current state. Clustered by `dataset_url`, `feed_id`, `grade`.

| Column | Type | Nullable | Notes |
|---|---|---|---|
| `dataset_url` | STRING | **NO** | cluster 1 |
| `dataset_name` | STRING | YES | |
| `feed_id` | STRING | **NO** | cluster 2 |
| `feed_type` | STRING | YES | |
| `feed_url` | STRING | YES | |
| `is_regular` | BOOL | YES | |
| `status` | STRING | YES | `OK` \| `WARNING` \| `ERROR` |
| `warnings` | JSON | YES | |
| `errors` | JSON | YES | |
| `missing_required_fields` | JSON | YES | Object keyed by opportunity kind. Each value is an array of missing required field names. |
| `location_completeness` | FLOAT64 | YES | |
| `start_date_completeness` | FLOAT64 | YES | |
| `end_date_completeness` | FLOAT64 | YES | |
| `activities_completeness` | FLOAT64 | YES | |
| `facilities_completeness` | FLOAT64 | YES | |
| `age_range_completeness` | FLOAT64 | YES | |
| `level_completeness` | FLOAT64 | YES | |
| `accessibility_support_completeness` | FLOAT64 | YES | |
| `gender_restriction_completeness` | FLOAT64 | YES | |
| `activity_vocabulary_adherence` | JSON | YES | How well activity values (`opportunities.activity_json`, last 5 days) match the OpenActive Activity List: `{adherence_pct, items, valid_id, invalid_id, other_scheme, label_match_only, unrecognised, top_unmatched}`, where `adherence_pct = valid_id / items * 100`. NULL when no item has an activity or the vocabulary couldn't be fetched. |
| `facility_vocabulary_adherence` | JSON | YES | The same for `facilityType` values (`opportunities.facility_json`, last 5 days) against the OpenActive Facility Types list. Same shape as `activity_vocabulary_adherence`. |
| `num_future_opportunity_items` | INT64 | YES | |
| `grade` | STRING | YES | cluster 3. `None` \| `Bronze` \| `Silver` \| `Gold`. Observed values are `Bronze`, `Silver`, `Gold` and NULL. |
| `feed_version` | STRING | YES | Detected OpenActive spec version. Observed values are `Unknown`, `V0.x`, `V2.0`. |
| `score` | FLOAT64 | YES | Normalised quality score from 0 to 100, based on which required, recommended and optional properties are present |
| `last_assessed` | TIMESTAMP | **NO** | |

## feeds

Registry of known feeds. One row per feed, current state. There is no partitioning or clustering.

| Column | Type | Nullable | Notes |
|---|---|---|---|
| `id` | STRING | YES | Feed id. Joins to `feed_id` everywhere else. |
| `url` | STRING | YES | |
| `type` | STRING | YES | Observed: `CourseInstance`, `Event`, `FacilityUse`, `OnDemandEvent`, `ScheduledSession`, `SessionSeries`, `Slot`, and empty string |
| `dataset_name` | STRING | YES | |
| `dataset_url` | STRING | YES | |
| `provider` | STRING | YES | |
| `license_url` | STRING | YES | |
| `logo_url` | STRING | YES | |
| `publisher_name` | STRING | YES | |
| `rpde_version` | STRING | YES | URL, e.g. `https://openactive.io/realtime-paged-data-exchange/1.0/`, or empty string |
| `model_version` | STRING | YES | |
| `is_regular` | BOOL | YES | |
| `last_access` | DATE | YES | |

## insight_category_counts

Value counts per category for each insight run. This is the largest insight table, so always filter
on `run_date`. **Partitioned by `run_date`**, clustered by `run_date`, `category`, `scope`.

| Column | Type | Nullable | Notes |
|---|---|---|---|
| `run_date` | TIMESTAMP | **NO** | partition; cluster 1 |
| `category` | STRING | **NO** | cluster 2. `item_kind` \| `item_type` \| `organizer` \| `activity` \| `facility` \| `accessibility` \| `activity_facility` \| `region` \| `district` \| `trust` |
| `scope` | STRING | **NO** | cluster 3. `all` \| `regular` \| `preview` |
| `value` | STRING | YES | |
| `count` | INT64 | YES | |
| `percentage` | FLOAT64 | YES | |

## insight_run_summary

Ecosystem-wide totals for each insight run. Run dates go from 2026-05-11 to the present.
**Partitioned by `run_date`**, clustered by `run_date`.

Most metrics come in three variants: the plain total, `_regular` and `_preview`. `_narrow` totals
exclude Slot rows, the same way as `active_opportunities_summary.opportunity_count_narrow`. `sad`
stands for sport and discipline.

| Column | Type | Nullable |
|---|---|---|
| `run_date` | TIMESTAMP | **NO** (partition; cluster 1) |
| `num_publishers`, `num_publishers_regular`, `num_publishers_preview` | INT64 | YES |
| `num_datasets`, `num_datasets_regular`, `num_datasets_preview` | INT64 | YES |
| `num_feeds`, `num_feeds_regular`, `num_feeds_preview` | INT64 | YES |
| `num_feeds_with_analysed_data`, `…_regular`, `…_preview` | INT64 | YES |
| `num_feeds_with_future_opportunity_items`, `…_regular`, `…_preview` | INT64 | YES |
| `total_num_items`, `total_num_items_regular`, `total_num_items_preview` | INT64 | YES |
| `total_num_future_opportunity_items`, `…_regular`, `…_preview` | INT64 | YES |
| `total_num_future_week_opportunity_items`, `…_regular`, `…_preview` | INT64 | YES |
| `total_num_item_kinds`, `total_num_item_types`, `total_num_organizer_names` | INT64 | YES |
| `total_num_activities`, `total_num_facilities`, `total_num_accessibilities` | INT64 | YES |
| `total_num_regions`, `total_num_districts`, `total_num_trusts` | INT64 | YES |
| `total_num_items_with_kinds`, `total_num_items_with_types`, `total_num_items_with_organizer_names` | INT64 | YES |
| `total_num_items_with_activities`, `total_num_items_with_facilities`, `total_num_items_with_accessibilities` | INT64 | YES |
| `total_num_items_with_regions`, `total_num_items_with_districts`, `total_num_items_with_trusts` | INT64 | YES |
| `total_num_items_with_sad`, `total_num_items_without_sad` | INT64 | YES |
| `total_num_activities_with_sad`, `total_num_activities_without_sad` | INT64 | YES |
| `num_sad`, `num_sad_matched`, `num_sad_unmatched` | INT64 | YES |
| `percentage_sad_matched`, `percentage_sad_unmatched` | FLOAT64 | YES |
| `total_num_items_narrow`, `total_num_future_opportunity_items_narrow`, `total_num_future_week_opportunity_items_narrow` | INT64 | YES |

`…` expands to the full column name, e.g. `num_feeds_with_analysed_data_regular`.

## insight_sport_discipline

How activities map to Sport England sport-and-discipline (SAD) entries for each insight run.
**Partitioned by `run_date`**, clustered by `run_date`, `is_matched`.

| Column | Type | Nullable | Notes |
|---|---|---|---|
| `run_date` | TIMESTAMP | **NO** | partition; cluster 1 |
| `sport_and_discipline` | STRING | YES | |
| `activity` | STRING | YES | |
| `is_matched` | BOOL | **NO** | cluster 2 |
| `count_items` | INT64 | YES | |
| `count_activities` | INT64 | YES | Only set for matched rollup rows |
| `percentage_items` | FLOAT64 | YES | |
| `percentage_activities` | FLOAT64 | YES | |

## insight_sport_discipline_master

The full sport-and-discipline list for each run, with whether any feed matched each entry.
**Partitioned by `run_date`**, clustered by `run_date`.

| Column | Type | Nullable | Notes |
|---|---|---|---|
| `run_date` | TIMESTAMP | **NO** | partition; cluster 1 |
| `sport` | STRING | YES | |
| `discipline` | STRING | YES | |
| `sport_and_discipline` | STRING | YES | |
| `is_matched_by_any_feed` | BOOL | **NO** | |

## opportunities

Every opportunity item currently in the feeds. Clustered by `dataset_url`, `feed_id`, `id`.

> **Warning:** This is by far the largest table at about 15 GB logical size. `json_data` and
> `inherited_data` take up most of that. Select only the columns you need, never `SELECT *`, and
> aggregate per entity in SQL so the table is scanned once. Do a `dry_run` first if you're unsure
> how many bytes a query will scan.
>
> **The table has no history.** It holds one row per item, current state only, and has no
> `ingestion_date`. Don't build trends, an `as_of` parameter or date windows over it. `last_updated`
> is the date the row was last upserted, not a snapshot date.

Observed `kind` distribution (2026-11-23): Slot 7.90M, ScheduledSession 1.22M, Event 326k,
SessionSeries 138k, NULL 60k, CourseInstance 29k, FacilityUse 11.5k, IndividualFacilityUse 3.9k,
HeadlineEvent 2k, OnDemandEvent 312.

Column names are **camelCase** where they mirror OpenActive properties, e.g. `startDate`,
`has_superEvent`, `accessibilitySupport`.

| Column | Type | Nullable | Notes |
|---|---|---|---|
| `dataset_url` | STRING | YES | cluster 1. Dataset URL. |
| `feed_id` | STRING | YES | cluster 2. Feed identifier (normalised feed URL). |
| `id` | STRING | YES | cluster 3. Item ID from the RPDE feed. |
| `data_id` | STRING | YES | Data ID from the RPDE feed. It can differ from the RPDE item ID. |
| `kind` | STRING | YES | Kind of opportunity, e.g. `Event`, `FacilityUse` |
| `modified` | INT64 | YES | Modified timestamp from the RPDE feed |
| `json_data` | JSON | YES | Opportunity data exactly as ingested from the feed, with no transformation or normalisation. **Large.** |
| `inherited_data` | JSON | YES | **Large.** |
| `activity` | JSON | YES | Activity labels for the opportunity |
| `facility` | JSON | YES | Facility label for the opportunity |
| `location` | JSON | YES | Geographical location of the opportunity |
| `startDate` | TIMESTAMP | YES | Start date and time |
| `endDate` | TIMESTAMP | YES | End date and time |
| `ageRange` | JSON | YES | Age range |
| `level` | JSON | YES | Difficulty level |
| `has_superEvent` | JSON | YES | SuperEvent URL, or the superEvent data inline |
| `has_subEvent` | JSON | YES | SubEvent URLs, or the subEvent data inline |
| `district_name` | STRING | YES | |
| `region_name` | STRING | YES | |
| `last_updated` | DATE | YES | UTC date when the ingest-opportunities job last upserted this row |
| `publisher_name` | STRING | YES | |
| `district_code` | STRING | YES | |
| `region_code` | STRING | YES | |
| `country_code` | STRING | YES | |
| `country_name` | STRING | YES | |
| `accessibilitySupport` | JSON | YES | |
| `genderRestriction` | STRING | YES | |
| `organization_name` | STRING | YES | |
| `organization_json` | JSON | YES | |
| `nhstrust_name` | STRING | YES | |
| `nhstrust_code` | STRING | YES | |
| `isAccessibleForFree` | BOOL | YES | |
| `activity_json` | JSON | YES | Raw activity objects, used by vocabulary adherence |
| `facility_json` | JSON | YES | Raw facilityType objects, used by vocabulary adherence |

## opportunity_ingestion

One row per feed for each daily opportunity crawl: RPDE paging position, change counts, totals and
status. There is no partitioning or clustering.

> **Data caveat:** history only starts at 2026-08-20. Six days are missing, one is duplicated, and
> 2026-11-04 is a partial crawl (134 datasets against the usual ~167). Long lookbacks such as the
> 120-day stall monitors are correct in code but the data can't exercise them yet. Never assert on
> absolute counts or long histories in tests. Older rows (before 2026-08-15) are in
> `opportunity_ingestion_backup_20260815`, which has a different schema.

| Column | Type | Nullable | Notes |
|---|---|---|---|
| `dataset_id` | STRING | YES | The dataset URL. The column is named `dataset_id` here, not `dataset_url`. |
| `feed_id` | STRING | YES | |
| `kind` | STRING | YES | Observed: `CourseInstance`, `Event`, `FacilityUse`, `OnDemandEvent`, `ScheduledSession`, `SessionSeries`, `Slot`, and empty string |
| `ingestion_date` | TIMESTAMP | YES | |
| `afterTimestamp` | STRING | YES | RPDE paging cursor |
| `afterId` | STRING | YES | RPDE paging cursor |
| `updated` | INT64 | YES | |
| `deleted` | INT64 | YES | |
| `status` | STRING | YES | `COMPLETE` \| `ERROR` \| `WARNING` |
| `afterChangeNumber` | INT64 | YES | RPDE paging cursor |
| `actual_deletes` | INT64 | YES | |
| `pending_deletes` | INT64 | YES | |
| `total_opportunities` | INT64 | YES | |
| `total_future_opportunities` | INT64 | YES | |
| `error_code` | STRING | YES | Observed: `403`, `404`, `500`, `502`, `BATCH_FAILED`, `CONNECTION_ERROR`, `EMPTY_PAGE`, `MISSING_ITEMS`, `NOT_PROCESSED`, `REQUEST_FAILED`, `SELF_LOOP`, NULL |
| `warning_message` | STRING | YES | |

## opportunity_ingestion_backup_20260815

A frozen copy of `opportunity_ingestion` taken on 2026-08-14 and never updated. It has only the
first 10 columns of the live table and no totals, deletes or error columns. Nothing in the API uses it.

| Column | Type | Nullable |
|---|---|---|
| `dataset_id` | STRING | YES |
| `feed_id` | STRING | YES |
| `kind` | STRING | YES |
| `ingestion_date` | TIMESTAMP | YES |
| `afterTimestamp` | STRING | YES |
| `afterId` | STRING | YES |
| `updated` | INT64 | YES |
| `deleted` | INT64 | YES |
| `status` | STRING | YES |
| `afterChangeNumber` | INT64 | YES |

## publisher_notifications

The steward work queue: one row per publisher with open monitoring problems, plus the outreach
status. There is no partitioning or clustering.

| Column | Type | Nullable | Notes |
|---|---|---|---|
| `id` | STRING | **NO** | Unique id for this notification row, e.g. `pn_example-leisure` |
| `publisher_id` | STRING | YES | Publisher slug, in the same style as the monitors, e.g. `pub_example-leisure` |
| `publisher_name` | STRING | YES | Display name of the publisher |
| `problem_count` | INT64 | YES | How many monitoring problems this publisher currently has |
| `monitors` | ARRAY&lt;STRING&gt; | **NO** | Ids of the monitors involved, e.g. `single_feed_stall`, `feed_ingestion_error` |
| `oldest_days_open` | INT64 | YES | Age in days of the longest-running problem |
| `status` | STRING | YES | Work status: `open`, `closed` or `in_progress`. Only `open` is in the data so far. |
| `stakeholder` | STRING | YES | Who is responsible for contacting this publisher, i.e. who sent the email |
| `contact` | STRING | YES | Who was emailed. Currently a dummy address. |
| `date_contacted` | DATE | YES | Date the email was sent |
| `notes` | STRING | YES | Extra notes from the steward |
| `problems` | JSON | YES | JSON list of the problems and evidence used in the email draft |
| `created_at` | TIMESTAMP | YES | When the row was first created, UTC |
| `updated_at` | TIMESTAMP | YES | When the row was last changed, UTC |
| `dataset_url` | STRING | YES | |

## socio_data

Population, Index of Multiple Deprivation 2025 (`imd25_*`) and Active Lives Survey (`als_*`) figures
for each area. This is a static load from 2026-07-15. There is no partitioning or clustering.

`area_code` is an ONS GSS code at several levels: local authority districts (`E06`–`E09`, `N09`,
`S12`, `W06`), regions (`E12`), combined authorities (`E47`) and countries/nations (`E92`, `N92`,
`S92`, `W92`, `K02`–`K04`). It joins to `district_code`, `region_code` or `country_code`.

| Column | Type | Nullable |
|---|---|---|
| `area_code` | STRING | YES |
| `area_name` | STRING | YES |
| `total_population` | INT64 | YES |
| `imd25_average_score` | FLOAT64 | YES |
| `imd25_rank_of_average_score` | FLOAT64 | YES |
| `imd25_pct_lsoas_in_most_deprived_10pct` | FLOAT64 | YES |
| `imd25_extent` | FLOAT64 | YES |
| `imd25_local_concentration` | FLOAT64 | YES |
| `als_respondents` | FLOAT64 | YES |
| `als_active_pop` | FLOAT64 | YES |
| `als_fairly_active_pop` | FLOAT64 | YES |
| `als_inactive_pop` | FLOAT64 | YES |
| `als_survey_adult_population` | FLOAT64 | YES |
| `als_active_rate` | FLOAT64 | YES |
| `als_fairly_active_rate` | FLOAT64 | YES |
| `als_inactive_rate` | FLOAT64 | YES |
| `als_active_rate_change_12m` | FLOAT64 | YES |
| `als_inactive_rate_change_12m` | FLOAT64 | YES |

---

## Regenerating

These queries are cheap because they read metadata only. Run them through the BigQuery MCP
`execute_sql` tool. The MCP caps results at about 50 rows, so the column query aggregates to one row
per table.

```sql
-- Tables, row counts, sizes
SELECT table_id, row_count, ROUND(size_bytes/1e6,1) AS size_mb,
       TIMESTAMP_MILLIS(last_modified_time) AS last_modified
FROM `openactive-monitor.openactive_analytics.__TABLES__`
ORDER BY table_id
LIMIT 50;

-- Columns (including nested STRUCT fields): path|type|nullable|partition|cluster|description
WITH f AS (
  SELECT p.table_name, p.column_name, p.field_path, p.data_type, p.description,
         c.ordinal_position, c.is_nullable, c.is_partitioning_column, c.clustering_ordinal_position
  FROM `openactive-monitor.openactive_analytics.INFORMATION_SCHEMA.COLUMN_FIELD_PATHS` p
  JOIN `openactive-monitor.openactive_analytics.INFORMATION_SCHEMA.COLUMNS` c
    USING (table_name, column_name)
)
SELECT table_name,
  STRING_AGG(FORMAT('%s|%s|%s|%s|%s|%s', field_path, data_type,
      IF(field_path = column_name, is_nullable, ''),
      IF(field_path = column_name AND is_partitioning_column = 'YES', 'P', ''),
      IFNULL(IF(field_path = column_name, CAST(clustering_ordinal_position AS STRING), NULL), ''),
      IFNULL(description, '')), '\n' ORDER BY ordinal_position, field_path) AS cols
FROM f
GROUP BY table_name
ORDER BY table_name
LIMIT 50;
```

When you regenerate, update the snapshot date at the top and the "Which code reads which table" map
(`grep -rnoE 'Tables\.[A-Za-z]+' Controllers Services Models`).
