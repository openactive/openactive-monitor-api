# Admin API

The `/admin` endpoints feed the **admin dashboard**. They are a separate surface from the public
analytics endpoints documented in [development.md](development.md):

|                | Analytics platform            | Admin dashboard                     |
|----------------|-------------------------------|-------------------------------------|
| Routes         | `/summary`, `/opportunities`, …| `/admin/*`                          |
| Token          | `Api:AccessToken`             | `Api:AdminToken`                    |
| Controller     | `Controllers/ApiController.cs` | `Controllers/Admin/*`               |
| Response shape | bare arrays / objects         | `{ "data": [...], "meta": {...} }`  |
| Output cache   | 4 hours                       | until 07:00 UTC daily               |
| Tests          | `MonitorApi.Tests`            | `MonitorApi.Admin.Tests`            |

The two tokens are **not** interchangeable in either direction. If `Api:AdminToken` is not configured,
every admin endpoint refuses every request — it never falls back to the public token.

```text
http://localhost:5268/admin/summary?token=<AdminToken>
http://localhost:5268/admin/single-feed-stall-incidents?token=<AdminToken>
http://localhost:5268/admin/single-feed-stall-trend?token=<AdminToken>
http://localhost:5268/admin/dataset-stall-incidents?token=<AdminToken>
http://localhost:5268/admin/dataset-stall-trend?token=<AdminToken>
http://localhost:5268/admin/feed-ingestion-error-incidents?token=<AdminToken>
http://localhost:5268/admin/feed-ingestion-error-trend?token=<AdminToken>
http://localhost:5268/admin/dataset-orphaned-children-incidents?token=<AdminToken>
http://localhost:5268/admin/dataset-future-decline-incidents?token=<AdminToken>
http://localhost:5268/admin/dataset-future-decline-trend?token=<AdminToken>
```

## API reference

The admin endpoints have their own OpenAPI document and Scalar page, containing only `/admin` routes:

```text
http://localhost:5268/scalar/admin        # interactive reference
http://localhost:5268/openapi/admin.json  # raw OpenAPI document
```

`/scalar` serves both documents with a dropdown to switch between the analytics and admin APIs. Neither
reference is token-gated. A new admin controller appears here automatically by deriving from
`AdminControllerBase`, which carries the `[ApiExplorerSettings(GroupName = "admin")]` tag.

## Response envelope

Every admin endpoint returns the same envelope, so the dashboard can paginate any of them identically:

```json
{
  "data": [ ... ],
  "meta": {
    "snapshot_date": "2026-09-01",
    "generated_at": "2026-09-02T11:08:47Z",
    "page": 1,
    "page_size": 500,
    "total": 126
  }
}
```

- `snapshot_date` — the day the analysis ran against: the latest day present in the source table, not
  today. Data lags by up to a day, so these differ routinely. (`/admin/summary` is the exception: its
  snapshot is the day of `generated_at`, the latest `feed_ingestion` run.)
- `total` — rows across all pages, before paging.
- `page` is one-based; `page_size` defaults to 500 and is capped at 1000. Out-of-range values are
  clamped rather than rejected.

`/admin/summary` answers with a single object rather than a list, so its `data` is that object and its
paging fields are fixed at `page: 1, page_size: 1, total: 1`. The `meta` keys are the same either way.

## Caching

Admin responses are held in the output cache until **07:00 UTC**, then discarded, whatever time of day
they were stored. The monitors describe one ingestion day at a time and those numbers do not move again
until the overnight pipeline has landed, so a fixed sliding window would either serve yesterday's
figures past the refresh or re-scan the ingestion history for nothing.

Entries vary by the full query string, so changing any parameter (including the token) is a separate
entry. Only `200` responses are cached — a `403` from a bad token is not. **When checking a change by
hand, expect the previous body**: restart the app, or vary a parameter, to force a fresh query.

## Endpoints

### `GET /admin/summary`

The dashboard's landing figures: the size of the monitored estate, how much of it is currently
unhealthy, and one line per monitor. Takes no parameters.

```json
{
  "data": {
    "publishers_monitored": 179,
    "publishers_with_issues": 0,
    "open_incidents": null,
    "past_threshold": null,
    "feeds": 463,
    "datasets": 179,
    "monitors": [
      {
        "monitor_id": "single_feed_stall",
        "count": 126,
        "past_threshold_count": 119,
        "sparkline": [126, 127, 125, 122, 123, 124, 126]
      },
      {
        "monitor_id": "dataset_stall",
        "count": 1,
        "past_threshold_count": 1,
        "sparkline": [1, 1, 1, 2, 2, 1, 1]
      },
      {
        "monitor_id": "feed_ingestion_error",
        "count": 6,
        "past_threshold_count": 1,
        "sparkline": [1, 2, 1, 1, 6, 1, 6]
      },
      {
        "monitor_id": "dataset_future_decline",
        "count": 7,
        "past_threshold_count": 2,
        "sparkline": [9, 11, 8, 10, 8, 9, 7]
      },
      {
        "monitor_id": "dataset_orphaned_children",
        "count": 582352,
        "past_threshold_count": 0,
        "sparkline": []
      }
    ],
    "publishers_with_issues_delta": 2,
    "open_incidents_delta": null,
    "past_threshold_delta": 2
  },
  "meta": {
    "snapshot_date": "2026-09-01",
    "generated_at": "2026-09-01T00:00:27Z",
    "page": 1,
    "page_size": 1,
    "total": 1
  }
}
```

Field notes:

- `publishers_monitored`, `datasets` and `feeds` come from the latest `feed_ingestion` row, which also
  supplies `meta.generated_at`; `meta.snapshot_date` is that timestamp's day. One dataset is one
  publisher, so `publishers_monitored` and `datasets` always agree.
- `publishers_with_issues` is the number of distinct datasets with at least one `ERROR` row in
  `opportunity_ingestion` dated **today** — failures in today's run, not a running total. Before the
  day's pipeline has run it is legitimately `0`.
- `monitors` carries one entry per monitor, evaluated at the latest day in `opportunity_ingestion` with
  that monitor's default thresholds. `count` therefore equals the `meta.total` of the monitor's own
  incidents endpoint called without arguments, and `sparkline` is the last seven `open_count` values
  from its trend endpoint, oldest first, so `sparkline[^1] == count`. It is shorter than seven entries
  only when less history exists, and is never padded.
  - `dataset_orphaned_children` is the exception to all of this. Its `count` is the **total number of
    orphaned children across the estate** — a count of broken items, not of datasets — so it does
    *not* equal its incidents endpoint's `meta.total`, which counts the datasets responsible. The
    headline is the size of the defect, because a single dataset routinely accounts for hundreds of
    thousands of orphans. It reads `opportunities`, which holds no history, so it has no trend
    endpoint, its `sparkline` is always **empty**, and its `past_threshold_count` is always `0` (that
    threshold applies to datasets, not to this total). Its day-on-day change is unknowable rather than
    zero, so it contributes nothing to the two `*_delta` figures below instead of dragging them
    towards zero.
  - That day is the *ingestion* table's latest day and can differ from `meta.snapshot_date`, which dates
    the coverage figures from `feed_ingestion`.
- `publishers_with_issues_delta` and `past_threshold_delta` are day-on-day changes in the monitors'
  `count` and `past_threshold_count`, summed across `monitors` — the latest day minus the previous one.
  Positive means the estate got worse. Both are `null` when no monitor has a previous day to compare
  against; a monitor that individually lacks one is skipped rather than counted as zero.
- `open_incidents`, `past_threshold` and `open_incidents_delta` are always `null`. Incidents are derived
  per request rather than tracked, so there is no cross-monitor total to report; read the per-monitor
  figures in `monitors` instead.

## Monitors

### `GET /admin/single-feed-stall-incidents`

Feeds that were publishing recently but have gone quiet, ordered longest-running first.

A feed raises an incident when it published at least once within `lookback_days` **and** has since been
silent for `stall_days` or more consecutive days. A day counts as published when the feed's
`opportunity_ingestion` rows for that day report at least one `updated` item.

Two rules are worth knowing:

- **Days with no ingestion run extend a silence rather than break it.** The absence of a run is not
  evidence that the feed published, so a pipeline gap looks like silence.
- **Datasets whose feeds have *all* gone quiet are excluded.** That is a dataset-wide outage, reported
  by [`dataset-stall-incidents`](#get-admindataset-stall-incidents), not a set of independent
  single-feed stalls. A feed that has never published also
  counts as "not publishing" for this check, so a dead dataset containing one never-seen feed cannot
  leak through as single-feed stalls.

| Parameter | Default | Meaning |
|---|---|---|
| `page` | `1` | One-based page number |
| `page_size` | `500` | Rows per page, capped at 1000 |
| `lookback_days` | `120` | How recently a feed must have published to count as live rather than retired |
| `stall_days` | `5` | Consecutive silent days that open an incident |
| `past_threshold_days` | `7` | Consecutive silent days that set `past_threshold`; never treated as looser than `stall_days` |
| `as_of` | latest ingestion day | Evaluate as at this date (`yyyy-MM-dd`) instead of the snapshot date |

The `trend` column always covers the trailing ten days, independently of `lookback_days`.

```json
{
  "monitor_id": "single_feed_stall",
  "publisher_id": "pub_actihire",
  "publisher_name": "Actihire",
  "feed_id": "actihire-bookteq-com-api-open-active-facility-uses",
  "feed_name": "facility-uses",
  "feed_type": "FacilityUse",
  "feed_url": "https://actihire.bookteq.com/api/open-active/facility-uses",
  "first_detected": "2026-08-20",
  "days_open": 12,
  "consecutive_days": 12,
  "past_threshold": true,
  "status": "open",
  "last_contacted": null,
  "trend": [0, 16, 0, 0, 0, 0, 0, 0, 0, 0],
  "detail": { "last_modified": "2026-08-20" },
  "quality_score": null
}
```

Field notes:

- `publisher_id` is a slug derived from `publisher_name` (`pub_<slug>`), not a stored identifier.
- `feed_name` is the last path segment of the feed URL.
- `first_detected` is the day the feed went quiet — its last publishing day — which is also
  `detail.last_modified`.
- `past_threshold` is `true` once `days_open` reaches `past_threshold_days`, which defaults to **7**.
  Every incident is open for at least `stall_days` (5), so the flag separates incidents in their first
  week of silence from those that have gone past it.
- `days_open` and `consecutive_days` always agree under the current model: an incident opens the day
  the feed goes quiet and closes when it publishes again. They would diverge only once incidents are
  tracked and resolved independently of the raw signal.
- `trend` is the feed's daily `updated` count from `opportunity_ingestion` over the trailing **ten
  days**, oldest first, ending on `snapshot_date`. It is always ten entries whatever the age of the
  incident, so entry *i* is the same day for every incident in the response and the column lines up as
  a sparkline. It is not filtered by whether the incident was open — the pre-stall activity is the
  point, so `[0, 16, 0, 0, 0, 0, 0, 0, 0, 0]` reads as "published 16 items nine days ago, nothing
  since".
  - `0` — the feed was polled that day and published nothing.
  - `null` — no ingestion row for that day at all, so nothing is known. Not the same as zero.
  - Multiple ingestion runs on one day are summed.
- `status` is always `open` and `last_contacted` always `null`. Outreach states such as
  `awaiting_reply` need an incident-tracking store, which does not exist yet.
- `quality_score` comes from `feed_quality.score` and is `null` for feeds that have not been assessed
  (most of them).

### `GET /admin/single-feed-stall-trend`

Open stall counts for each of the last `trend_days` days, oldest first. Each day is evaluated
independently against the same rules as the incidents endpoint, so a point shows what that endpoint
would have reported on that day — the final point always agrees with it. `past_threshold_count` is
always a subset of `open_count`.

Accepts `page`, `page_size`, `lookback_days`, `stall_days`, `past_threshold_days`, `as_of` as above,
plus:

| Parameter | Default | Meaning |
|---|---|---|
| `trend_days` | `30` | Days of history to return |

```json
{
  "data": [
    { "date": "2026-08-30", "open_count": 123, "past_threshold_count": 118 },
    { "date": "2026-08-31", "open_count": 124, "past_threshold_count": 117 },
    { "date": "2026-09-01", "open_count": 126, "past_threshold_count": 119 }
  ],
  "meta": { "snapshot_date": "2026-09-01", "generated_at": "2026-09-02T11:09:01Z", "page": 1, "page_size": 500, "total": 30 }
}
```

### `GET /admin/dataset-stall-incidents`

Datasets in which **every** feed has stopped publishing, ordered longest-running first. Nothing new or
updated is reaching consumers from that publisher at all, so all of their downstream data is frozen —
as opposed to one feed of an otherwise healthy dataset going quiet, which is the single-feed monitor's
job.

A dataset raises an incident when it published at least once within `lookback_days` **and** every one
of its feeds has since been silent for `stall_days` or more consecutive days. A dataset counts as
publishing on any day one of its feeds reported at least one `updated` item, so it goes quiet only when
its last remaining feed does, and it has been silent for as long as its most recently active feed has.

The rules worth knowing:

- **Days with no ingestion run extend a silence rather than break it**, exactly as for single feeds: the
  absence of a run is not evidence that anything published.
- **A dataset that never published inside the lookback window is not an incident.** There is nothing to
  say it ever worked. Nor is one silent for longer than `lookback_days` — that is retired, not stalled.
- **A feed that never published does not stop the dataset being reported.** It says nothing about when
  the dataset was last live, but it is still one of the feeds the incident accounts for and appears in
  `detail.feeds` with a `null` `last_published`.
- **This monitor and `single-feed-stall-incidents` partition the same signal.** That one excludes
  datasets whose feeds have all gone quiet, precisely so they are reported here once instead of as a
  handful of unrelated feed stalls. At the default thresholds no feed appears in both.

| Parameter | Default | Meaning |
|---|---|---|
| `page` | `1` | One-based page number |
| `page_size` | `500` | Rows per page, capped at 1000 |
| `lookback_days` | `120` | How recently the dataset must have published to count as live rather than retired |
| `stall_days` | `5` | Consecutive days with no feed publishing that open an incident |
| `past_threshold_days` | `7` | Consecutive silent days that set `past_threshold`; never treated as looser than `stall_days` |
| `as_of` | latest ingestion day | Evaluate as at this date (`yyyy-MM-dd`) instead of the snapshot date |

The defaults match the single-feed monitor's deliberately: the two only partition the silence cleanly
while they agree on what silence is. The `trend` column always covers the trailing ten days,
independently of `lookback_days`.

```json
{
  "monitor_id": "dataset_stall",
  "publisher_id": "pub_shirley-high-school",
  "publisher_name": "Shirley High School",
  "dataset_url": "https://shirleyhighschool.bookteq.com/api/open-active",
  "dataset_name": "Shirley High School Facilities",
  "feed_count": 2,
  "first_detected": "2026-09-01",
  "days_open": 9,
  "consecutive_days": 9,
  "past_threshold": true,
  "status": "open",
  "last_contacted": null,
  "trend": [404, null, 0, 0, 0, 0, 0, 0, 0, 0],
  "detail": {
    "last_modified": "2026-09-01",
    "feeds": [
      {
        "feed_id": "shirleyhighschool-bookteq-com-api-open-active-slots",
        "feed_name": "slots",
        "last_published": "2026-09-01",
        "consecutive_days": 9
      },
      {
        "feed_id": "shirleyhighschool-bookteq-com-api-open-active-facility-uses",
        "feed_name": "facility-uses",
        "last_published": null,
        "consecutive_days": null
      }
    ]
  }
}
```

Field notes:

- The incident's identity is `dataset_url`, matching `feeds.dataset_url`. `dataset_name` is the stored
  `feed_quality.dataset_name`, falling back to the host of the URL, so it is never empty.
- `first_detected` is the day the dataset went quiet — the last day *any* of its feeds published — which
  is also `detail.last_modified` and the `last_published` of the first entry in `detail.feeds`.
- `days_open` and `consecutive_days` always agree, as on the single-feed monitor: the incident opens the
  day the dataset goes dark and closes as soon as any feed publishes again.
- `feed_count` is the feeds seen for the dataset in the ingestion history, all of them silent.
  `detail.feeds` lists them, most recently active first, so the first entry is the feed that went quiet
  last and dates the incident, and the rest show whether the dataset stopped all at once or wound down
  feed by feed. A feed's own `consecutive_days` is therefore never smaller than the incident's, and is
  `null` — with `last_published` — for a feed never seen to publish.
- `trend` is the dataset's daily `updated` total from `opportunity_ingestion` over the trailing **ten
  days**, oldest first, ending on `snapshot_date` — every feed's counts added up. Always ten entries, so
  entry *i* is the same day for every incident in the response.
  - `0` — at least one feed was polled that day and the dataset published nothing.
  - `null` — no feed of the dataset had an ingestion row that day, so nothing is known. Not the same as
    zero.
- `status` is always `open` and `last_contacted` always `null`, as on every monitor.
- There is no `feed_id`, `feed_name`, `feed_type`, `feed_url` or `quality_score`: the incident is about
  a dataset, its feeds are in `detail.feeds`, and `feed_quality.score` is per-feed with no dataset-level
  equivalent. As on `dataset-orphaned-children-incidents`, these are absent rather than always-null.

### `GET /admin/dataset-stall-trend`

Open dataset-wide stall counts for each of the last `trend_days` days, oldest first. Each day is
evaluated independently against the same rules as the incidents endpoint, so a point shows what that
endpoint would have reported on that day — the final point always agrees with it.
`past_threshold_count` is always a subset of `open_count`. Counts are of **datasets**, so a publisher
with six dark feeds is one, not six.

Accepts `page`, `page_size`, `lookback_days`, `stall_days`, `past_threshold_days`, `as_of` as above,
plus:

| Parameter | Default | Meaning |
|---|---|---|
| `trend_days` | `30` | Days of history to return |

```json
{
  "data": [
    { "date": "2026-09-08", "open_count": 1, "past_threshold_count": 1 },
    { "date": "2026-09-09", "open_count": 1, "past_threshold_count": 1 },
    { "date": "2026-09-10", "open_count": 1, "past_threshold_count": 1 }
  ],
  "meta": { "snapshot_date": "2026-09-10", "generated_at": "2026-09-10T14:54:51Z", "page": 1, "page_size": 500, "total": 30 }
}
```

### `GET /admin/feed-ingestion-error-incidents`

Feeds whose ingestion is failing now but was completing recently, ordered longest-failing first.

A feed raises an incident when its ingestion **failed on the snapshot day** and it **completed at least
once in the `success_lookback_days` days before it**. A day counts as failed when the feed's
`opportunity_ingestion` rows for that day report `ERROR` and none of them report `COMPLETE`.

The rules worth knowing:

- **Success wins within a day.** A feed polled twice, once failing and once completing, counts as
  completed: it did deliver. `WARNING` is also a successful ingestion, not a failure.
- **Days with no ingestion run are not evidence of failure.** A pipeline gap cannot open an incident —
  that silence is the stall monitor's business — but it does count towards `days_open`, which measures
  days since the last *completed* ingestion.
- **Feeds that have not completed within `success_lookback_days` are left out** as permanently broken
  rather than newly failing. This is the bulk of the exclusion: on 2026-09-09, 32 feeds failed and only
  8 of them had completed inside the window.
- **Authorisation failures are left out.** A failure whose `error_code` is `401` or `403` is a
  credentials problem rather than a broken feed and gets its own monitor. There is no way to turn this
  off; the codes are fixed in `FeedIngestionErrorDetector.AuthErrorCodes`.
- **No dataset-wide exclusion**, unlike the stall monitor. A publisher whose whole estate fails on one
  day is exactly what this monitor should surface, and the shared `error_code` across its feeds is the
  evidence for it.

| Parameter | Default | Meaning |
|---|---|---|
| `page` | `1` | One-based page number |
| `page_size` | `500` | Rows per page, capped at 1000 |
| `success_lookback_days` | `15` | How recently the feed must have completed an ingestion for its current failure to count as a regression |
| `error_days` | `1` | Days of failure that open an incident; at the default a single failing day is enough |
| `past_threshold_days` | `3` | Days of failure that set `past_threshold`; never treated as looser than `error_days` |
| `as_of` | latest ingestion day | Evaluate as at this date (`yyyy-MM-dd`) instead of the snapshot date |

The `trend` column always covers the trailing ten days, independently of `success_lookback_days`.

```json
{
  "monitor_id": "feed_ingestion_error",
  "publisher_id": "pub_find-my-facility",
  "publisher_name": "Find My Facility",
  "feed_id": "api-findmyfacility-com-v1-openactive-sessionSeries",
  "feed_name": "sessionSeries",
  "feed_type": "SessionSeries",
  "feed_url": "https://api.findmyfacility.com/v1/openactive/sessionSeries",
  "first_detected": "2026-09-02",
  "days_open": 8,
  "consecutive_days": 8,
  "past_threshold": true,
  "status": "open",
  "last_contacted": null,
  "trend": [0, 0, 0, 1, 1, 1, 1, 1, 1, 1],
  "detail": {
    "error_code": "500",
    "error_message": "HTTP 500 fetching https://api.findmyfacility.com/v1/openactive/sessionSeries?afterTimestamp=1786030803&afterId=247e3083-abaf-4275-9227-13d2e88eb2af",
    "last_completed": "2026-09-01"
  },
  "quality_score": null
}
```

The identifier, publisher and feed fields carry the same meaning as on a stall incident; the rest:

- `first_detected` is the day after `detail.last_completed` — the first day the feed can be shown to
  have been failing.
- `days_open` and `consecutive_days` are both days since the last completed ingestion, so they always
  agree, and both are between `error_days` and `success_lookback_days` by construction.
- `past_threshold` is `true` once `days_open` reaches `past_threshold_days`, which defaults to **3**.
- `detail.error_code` is the failure's code on the snapshot day: an HTTP status (`500`, `404`) or a
  pipeline code (`BATCH_FAILED`, `CONNECTION_ERROR`, `MISSING_ITEMS`, `EMPTY_PAGE`).
  `detail.error_message` is that failure's message. Both are `null` for a failure recorded before the
  columns existed, and both always come from the same run.
- `trend` is one flag per day over the trailing **ten days**, oldest first, ending on `snapshot_date`.
  Always ten entries, so entry *i* is the same day for every incident in the response and the column
  lines up as a bar strip.
  - `1` — the feed's ingestion failed that day.
  - `0` — anything else: it completed, it warned, or there was no ingestion run at all. Unlike the stall
    monitor's `trend`, a missing day is not distinguished — the series answers "did this feed fail that
    day", and an absent run did not. In the example above, 2026-09-02 (the day missing from the table)
    reads `0` alongside the two completed days before it.
  - The last entry is always `1`: that is what opened the incident.
  - A `1` can appear before `first_detected` if the feed had an earlier failure spell inside the ten
    days; the column is not filtered to this incident.
- `status` is always `open` and `last_contacted` always `null`, as on every monitor.

### `GET /admin/feed-ingestion-error-trend`

Open ingestion error counts for each of the last `trend_days` days, oldest first. Each day is evaluated
independently against the same rules as the incidents endpoint, so a point shows what that endpoint
would have reported on that day — the final point always agrees with it. `past_threshold_count` is
always a subset of `open_count`.

Accepts `page`, `page_size`, `success_lookback_days`, `error_days`, `past_threshold_days`, `as_of` as
above, plus:

| Parameter | Default | Meaning |
|---|---|---|
| `trend_days` | `30` | Days of history to return |

```json
{
  "data": [
    { "date": "2026-09-07", "open_count": 6, "past_threshold_count": 1 },
    { "date": "2026-09-08", "open_count": 1, "past_threshold_count": 1 },
    { "date": "2026-09-09", "open_count": 6, "past_threshold_count": 1 }
  ],
  "meta": { "snapshot_date": "2026-09-09", "generated_at": "2026-09-09T09:03:48Z", "page": 1, "page_size": 500, "total": 30 }
}
```

Two things to expect in the series:

- **A step down on the first day carrying `error_code`.** The column was added recently, so on earlier
  days the `401`/`403` exclusion has nothing to match and those points count authorisation failures as
  ingestion errors.
- **A zero on days with no ingestion run**, such as 2026-09-02: no rows means no failures can be shown.
  The count recovers the next day, so the series is spiky rather than smooth.

### `GET /admin/dataset-orphaned-children-incidents`

Datasets publishing children whose parent event is missing from the same dataset, ordered worst first.

An OpenActive child names its parent through `has_superEvent`: a `Slot` names its `FacilityUse`, a
`ScheduledSession` names its `SessionSeries`. When that reference points at a `data_id` that is not in
`opportunities` for the same `dataset_url`, the child is an **orphan** — bookable availability hanging
off an event no consumer of the dataset can resolve. A dataset raises an incident when it has at least
`min_orphans` of them.

The rules worth knowing:

- **One incident per dataset, not per feed.** The missing parent may be published by a different feed
  of the same dataset, so the check only means anything at dataset scope — and a publisher fixes it
  once. `detail.by_kind` splits every count between `Slot` and `ScheduledSession`.
- **`missing_parent_count` is the figure to act on, not `orphan_count`.** One absent parent can orphan
  thousands of children. On 2026-09-09 Loughborough University reported 433,014 orphaned Slots arising
  from just **59** missing `FacilityUse` records — fifty-nine things to fix, not four hundred thousand.
- **Only a scalar reference can dangle.** A child that inlines its `superEvent` as a JSON object
  carries its parent with it, so it counts in `child_count` but is never examined. This is most of what
  the check excludes, and it is concentrated in `ScheduledSession`: of the ~1.38M published, about 637k
  inline the parent and are never checked.
- **Nothing is filtered by date.** Every child the table holds is counted, however long ago it was
  added: `opportunities` is current state, so anything in it is something a consumer can see today.
  Ageing either side out would report stable datasets as broken — a `FacilityUse` is ingested once and
  then sits unchanged while the publisher churns slots against it — and would quietly duplicate the
  stall monitor.
- **A parent published by somebody else still counts as missing.** The check is scoped to one
  `dataset_url`, because a consumer of this dataset cannot resolve anything outside it.
- **Known gap:** there is no `min_share` knob, so a dataset with three children all orphaned sits in
  the same list as one with 200,000. Sort or filter on `orphan_share` client-side for now.

| Parameter | Default | Meaning |
|---|---|---|
| `page` | `1` | One-based page number |
| `page_size` | `500` | Rows per page, capped at 1000 |
| `min_orphans` | `1` | Orphaned children that open an incident, counted across both kinds |
| `past_threshold_orphans` | `100` | Orphaned children that set `past_threshold`; never treated as looser than `min_orphans` |

There is **no date parameter at all** — no `as_of`, no lookback. `opportunities` is a current-state
mirror with no per-day snapshots, so a past date cannot be answered and accepting one would return
today's figures under yesterday's label; and since the whole table is current, there is nothing a
window would usefully exclude.

```json
{
  "monitor_id": "dataset_orphaned_children",
  "publisher_id": "pub_loughborough-university",
  "publisher_name": "Loughborough University",
  "dataset_url": "https://loughboroughuniversity-openactive.legendonlineservices.co.uk/OpenActive",
  "dataset_name": "Loughborough University Sessions and Facilities",
  "child_count": 478627,
  "checked_count": 472876,
  "orphan_count": 433014,
  "orphan_share": 0.9047003198733042,
  "missing_parent_count": 59,
  "past_threshold": true,
  "status": "open",
  "last_contacted": null,
  "detail": {
    "by_kind": [
      { "kind": "Slot", "child_count": 472876, "checked_count": 472876, "orphan_count": 433014, "missing_parent_count": 59 },
      { "kind": "ScheduledSession", "child_count": 5751, "checked_count": 0, "orphan_count": 0, "missing_parent_count": 0 }
    ],
    "missing_parents": [
      { "missing_id": "https://loughboroughuniversity-openactive.legendonlineservices.co.uk/api/facility-uses/664-1", "child_count": 10857 },
      { "missing_id": "https://loughboroughuniversity-openactive.legendonlineservices.co.uk/api/facility-uses/686-1", "child_count": 10857 }
    ]
  }
}
```

Field notes:

- `dataset_url` is the incident's identity; `dataset_name` is the display name from `feed_quality`,
  falling back to the URL's host so it is never empty. `publisher_id` is a slug derived from
  `publisher_name` (`pub_<slug>`), the same value the feed monitors use for the same publisher.
- `child_count` counts **every** child of the two kinds the dataset publishes, whatever its age;
  `checked_count` counts only those examined, meaning those that name their parent with a scalar
  reference. The gap between them is children that inline their `superEvent`. So
  `orphan_count <= checked_count <= child_count` always.
- `orphan_share` is `orphan_count / child_count`, so its numerator covers only the children that could
  be checked while its denominator covers all of them. Divide `orphan_count` by `checked_count`
  yourself for the ratio over exactly what was examined; the two diverge for a dataset whose children
  mostly inline their parent, which is common for `ScheduledSession`.
- `past_threshold` is `true` once `orphan_count` reaches `past_threshold_orphans`, which defaults to
  **100**. On 2026-09-09 that split 21 open incidents into 16 escalated and 5 not.
- `detail.by_kind` entries sum to the incident's own counts and are ordered worst kind first. A kind
  the dataset does not publish is absent rather than present with zeros.
- `detail.missing_parents` is a sample of at most **five** missing ids, most children first, merged
  across both kinds so it is the dataset's worst offenders rather than one kind's. Paste one into the
  publisher's feed to show them what is missing. `missing_parent_count` is the full count the sample is
  drawn from.
- `status` is always `open` and `last_contacted` always `null`, as on every monitor.

**Fields this monitor does not have**, and why — they are *absent*, not present and null:

- `feed_id`, `feed_name`, `feed_type`, `feed_url` — the entity is a dataset, not a feed.
- `first_detected`, `days_open`, `consecutive_days`, `trend` — `opportunities` carries no history, so
  none of them can be computed. These would be **added** if a snapshot source appeared; carrying them
  as always-null fields now would mean narrowing them later, which breaks any consumer that had handled
  the null.
- `quality_score` — `feed_quality.score` is per-feed and there is no dataset-level equivalent.

### `GET /admin/dataset-future-decline-incidents`

Datasets whose forward supply is draining, ordered by the largest loss first. The signal is
`total_future_opportunities` — how much a publisher still has on offer — so an incident here means
consumers are running out of things to book, whether or not anything looks broken. It is the gap the
other four monitors leave between them: these feeds are ingesting successfully every day and simply
have less to give each time.

A feed raises an incident when it completed at least three runs inside the last `window_days` days,
carried at least `min_future_opportunities` at the first of them, **and** then either fell at *every*
observation in the window, however gently, **or** lost at least `drop_percent` of its supply between two
consecutive observations. The two rules are independent and either is enough.

It is then **reported** only if it also clears a qualifying gate over the longer `qualify_window_days`
window: either it has lost at least `qualify_drop_percent` of its supply across that window, **or** its
`updated − actual_deletes` across the detection window is negative. Detection is deliberately sensitive
and finds plenty of wobble; the gate is what separates a slide worth an operator's morning from noise.
On 2026-09-15 it took the list from 11 datasets to 7.

The rules worth knowing:

- **Only `COMPLETE` ingestion runs are read.** A feed that failed or was not polled has no observation
  that day at all. That is what keeps a publisher's outage — already reported by the stall and
  ingestion-error monitors — from being turned into a second, duplicate incident with a bogus fall to
  zero.
- **Comparisons are between consecutive observations, not consecutive calendar days.** A missing day
  neither breaks a run of falls nor invents a drop across the gap it leaves.
- **The gate's two clauses catch different things.** A steep slide qualifies on the drop however
  healthily the feed publishes. A shallow one qualifies only if the feed is removing more than it adds,
  which is what tells erosion apart from a publisher whose catalogue is simply smaller this week. Only
  the second clause reads `updated` and `actual_deletes`; neither *raises* an incident, and the rules
  that do read supply alone.
- **The drop clause looks back further than detection does.** A slide that has been running for a
  fortnight reads as trivial through a five-day slot — `qualify_drop_percent` is measured over
  `qualify_window_days`, so the ten-day figure is the one that decides. The delta, by contrast, is
  summed over the five-day detection window only: deletions older than that do not resurrect a feed.
- **`qualify_window_days` is never treated as shorter than `window_days`**, so the days it judges are
  always a superset of the days the decline was found in.
- **`Slot` feeds are excluded entirely.** A slot count says how far ahead a publisher has opened
  bookings, not how much it has to offer, so it falls every time that rolling window shortens — a
  decline that means nothing. Session, session-series and facility-use feeds carry the real signal. The
  list lives on `DatasetSupplyController.IgnoredKinds` and is shared with the `/admin/summary` tile so
  the two cannot disagree.
- **The monotonic rule exists because a percentage threshold cannot see erosion.** A feed shedding two
  percent a day loses a tenth of its supply a week and never trips a single-step threshold; one that
  halves overnight and then holds never trips a cumulative one. `detail.reason` says which fired, and is
  `both` where the dataset's feeds between them did each.
- **The dataset is the incident and `detail.feeds` names the feeds responsible.** Its totals cover only
  those feeds, never the dataset's healthy ones, so `drop` and `drop_percent` always describe the same
  thing. A dataset's healthy feed is simply not in the list.
- **`past_threshold` follows the percentage, not the raw loss**, so a small publisher losing most of
  what it had escalates alongside a large one losing a quarter.
- **A dataset can appear here and on a stall monitor at once.** They answer different questions: a feed
  that is polled daily, publishes nothing new and watches its listings expire is both silent and
  draining. That is not double-reporting, it is two true statements, and the supply figure is the one
  that says how urgent it is.

| Parameter | Default | Meaning |
|---|---|---|
| `page` | `1` | One-based page number |
| `page_size` | `500` | Rows per page, capped at 1000 |
| `window_days` | `5` | Trailing days the decline is measured over |
| `drop_percent` | `10` | Percentage lost between two consecutive runs that raises an incident on its own |
| `qualify_window_days` | `10` | Longer window the decline must also show up over; never treated as shorter than `window_days` |
| `qualify_drop_percent` | `10` | Percentage that must have been lost across `qualify_window_days`, unless the feed's delta is negative |
| `past_threshold_drop_percent` | `25` | Net percentage lost across the window that sets `past_threshold`; never treated as looser than `drop_percent` |
| `min_future_opportunities` | `50` | Forward supply a feed must have had at the start of the window to be worth reporting |
| `as_of` | latest ingestion day | Evaluate as at this date (`yyyy-MM-dd`) instead of the snapshot date |

A feed needs three observations inside the window, or the whole window where that is shorter, and never
fewer than two — so `window_days=1` reports nothing rather than everything. The `trend` column always
covers the trailing ten days, independently of `window_days`.

```json
{
  "monitor_id": "dataset_future_decline",
  "publisher_id": "pub_better-better-admin",
  "publisher_name": "Better (better-admin)",
  "dataset_url": "https://better-admin.org.uk/api/openactive/better",
  "dataset_name": "Better Sessions and Facilities",
  "feed_count": 1,
  "first_detected": "2026-09-11",
  "days_open": 4,
  "consecutive_days": 4,
  "past_threshold": false,
  "status": "open",
  "last_contacted": null,
  "trend": [206069, 202140, 198974, 196018, 193009, 190167, 187137, 183814, 180114, 177186],
  "detail": {
    "reason": "monotonic_decline",
    "window_days": 5,
    "start_total": 190167,
    "current_total": 177186,
    "drop": 12981,
    "drop_percent": 6.83,
    "qualify_window_days": 10,
    "qualify_start_total": 206069,
    "qualify_drop_percent": 14.02,
    "feeds": [
      {
        "feed_id": "better-admin-org-uk-api-openactive-better-scheduled-sessions",
        "feed_name": "scheduled-sessions",
        "reason": "monotonic_decline",
        "start_future": 190167,
        "current_future": 177186,
        "drop": 12981,
        "drop_percent": 6.83,
        "qualify_start_future": 206069,
        "qualify_drop_percent": 14.02,
        "consecutive_declining_days": 4,
        "largest_daily_drop_percent": 2.01,
        "updated_in_window": 332,
        "deletes_in_window": 16131,
        "delta_in_window": -15799
      }
    ]
  }
}
```

Field notes:

- The incident's identity is `dataset_url`, matching `feeds.dataset_url`. `dataset_name` is the stored
  `feed_quality.dataset_name`, falling back to the host of the URL, so it is never empty.
- `feed_count` is the number of **declining** feeds, not the dataset's feed count. It always equals
  `detail.feeds.length`.
- `first_detected` is the day the decline began: the start of the unbroken run of falls that ends the
  window, or — when the window does not end in a fall — the day the steepest drop fell from. It is
  always inside the window, so `days_open` never exceeds `window_days - 1`, and `consecutive_days`
  always equals it.
- `detail.start_total` and `detail.current_total` are the contributing feeds' supply at the first and
  last observation in the window; `drop` is the difference and `drop_percent` that difference over
  `start_total`, to two decimal places. A dataset's own feeds may each have fallen over different pairs
  of days, which is why the per-feed figures are given too.
- `detail.feeds[].largest_daily_drop_percent` is the steepest single step down in the window — the
  figure the `sharp_drop` rule tests. For a monotonic decline it is simply the worst of many small falls
  and may be well under `drop_percent`.
- `detail.feeds[].updated_in_window` and `deletes_in_window` are **context and never raise anything**.
  Read together they say what kind of decline it is: the sample above shows 15,368 deletions against
  309 updates, a feed removing far more than it adds. A fall with no deletions behind it is supply
  quietly expiring because nothing new is being scheduled.
- `trend` is the contributing feeds' daily `total_future_opportunities` over the trailing **ten days**,
  oldest first, ending on `snapshot_date`. Always ten entries, so entry *i* is the same day for every
  incident in the response, and it covers the healthy days before the decline began because the supply
  the dataset used to carry is the point of the column.
  - `null` — no contributing feed completed a run that day, so nothing is known. Not the same as zero.
- `status` is always `open` and `last_contacted` always `null`, as on every monitor.
- There is no `feed_id`, `feed_name`, `feed_type`, `feed_url` or `quality_score`: the incident is about
  a dataset, its feeds are in `detail.feeds`, and `feed_quality.score` is per-feed with no dataset-level
  equivalent. As on the two other dataset-scoped monitors, these are absent rather than always-null.

### `GET /admin/dataset-future-decline-trend`

Counts of datasets losing forward supply on each of the last `trend_days` days, oldest first. Each day
is evaluated independently against the same rules as the incidents endpoint, so a point shows what that
endpoint would have reported on that day — the final point always agrees with it.
`past_threshold_count` is always a subset of `open_count`. Counts are of **datasets**, so a publisher
with six draining feeds is one, not six.

Accepts `page`, `page_size`, `window_days`, `drop_percent`, `qualify_window_days`,
`qualify_drop_percent`, `past_threshold_drop_percent`, `min_future_opportunities`, `as_of` as above,
applies the same qualifying gate and excludes the same kinds, plus:

| Parameter | Default | Meaning |
|---|---|---|
| `trend_days` | `30` | Days of history to return |

```json
{
  "data": [
    { "date": "2026-09-12", "open_count": 18, "past_threshold_count": 4 },
    { "date": "2026-09-13", "open_count": 15, "past_threshold_count": 3 },
    { "date": "2026-09-14", "open_count": 10, "past_threshold_count": 0 }
  ],
  "meta": { "snapshot_date": "2026-09-14", "generated_at": "2026-09-14T15:22:37Z", "page": 1, "page_size": 500, "total": 30 }
}
```

Expect this series to move about far more than the stall ones. A stall persists until a feed publishes
again; a decline is a statement about a five-day window, so a dataset leaves the list as soon as one
good day pushes the fall out of it.

The **spine every monitor's incidents share**, and all the dashboard should rely on across them, is
`monitor_id`, `publisher_id`, `publisher_name`, `past_threshold`, `status` and `last_contacted`.
Everything else, including which entity the incident is about, is monitor-specific.

## Source data

The feed-health monitors read `opportunity_ingestion` (daily ingestion result per feed), joined to
`feeds` for descriptive fields and `feed_quality` for the score. The two stall monitors read exactly
the same per-feed publishing history — one feed at a time, one dataset at a time — which is what lets
them partition the silence between them rather than each having its own idea of it. The orphaned-children monitor reads
`opportunities` instead — see below. Multiple ingestion runs on the same day are
collapsed into one day — summed for the stall monitors' `updated` counts, collapsed with success
winning for the error monitors' status, and taken from the day's **last completed run** for the
future-decline monitor, because `total_future_opportunities` is a level rather than a counter and must
not be added up. That monitor reads only `status = 'COMPLETE'` rows, so a failed or missing run leaves
a gap in its history rather than a figure.

`error_code` and `warning_message` were added to the table recently and are populated only for the most
recent days; older `ERROR` rows carry neither.

A feed id can appear against two `dataset_id`s: a publisher that moves its dataset to a new hostname
keeps its feed ids, so the feed has rows under the old name before the move and the new name after it
(ChelmsfordCitySports moved from `leisurecloud.net` to `gs-signature.cloud` on 2026-08-23). All of it is
the same feed's history and is kept; the feed is attributed to the dataset of its **most recent**
ingestion, so it is reported under the name the dataset has now, and the old name disappears from the
monitors once every feed has moved.

`opportunities` is a different shape entirely: one row per opportunity item, **current state only**,
with no `ingestion_date` and so no history of any kind. Consequences:

- It cannot date itself, which is why the orphaned-children monitor takes `meta.snapshot_date` from
  `opportunity_ingestion` like every other monitor: `opportunities` is a mirror refreshed by that same
  pipeline, so the ingestion day is its provenance. Its own `last_updated` is publisher-supplied and
  can sit in the future when a publisher's clock is wrong.
- There is no trend endpoint and no date parameter of any kind for that monitor, and its incidents
  carry no `days_open`-style fields. Every row in the table is counted, however old.
- `dataset_url` is the join key to `feeds` (for `publisher_name`) and `feed_quality` (for
  `dataset_name`). On 2026-09-09, 159 of the 160 dataset URLs in `opportunities` matched a `feeds` row;
  the one that does not would report with empty descriptive fields.
- It is large — a single orphan query scans roughly 2.6 GB — which the daily cache absorbs but which
  makes `/admin/summary` noticeably slower than it was.

**`opportunity_ingestion` currently holds only ~20 days of history** (from 2026-08-20), with 2026-09-02 missing and
2026-08-20 duplicated. Consequences worth remembering when reading the numbers:

- The 120-day stall lookback is aspirational — it can only see as far back as the table goes.
- Stall trend points read zero for the first `stall_days` (and `past_threshold_count` for the first
  `past_threshold_days`) after the earliest day of data: no feed can yet be *shown* to have been silent
  that long.
- Most open stall incidents are currently past threshold, because the bulk of them date back to the
  first day of data. Expect that proportion to fall as history accumulates. The same applies to the
  dataset-wide stalls, which are drawn from the same history.
- A dataset counts as dark only once *every* feed has been silent for `stall_days`, so
  `dataset-stall-incidents` is a short list — a single dataset on 2026-09-10 — while
  `single-feed-stall-incidents` runs to dozens. That ratio is the monitor working, not a gap in it.
- The ingestion error monitor's 15-day success lookback is inside what the table holds, so it is the one
  window the data can currently exercise in full. So is the future-decline monitor's five-day window,
  which is the other; its 30-day trend, though, can only have points for the days the table covers.
- The duplicated day and the missing one are both absorbed by the future-decline monitor without
  special handling: same-day runs collapse to one figure, and a missing day is simply one fewer
  observation in the window rather than a fall.

## Tests

```bash
dotnet test MonitorApi.Admin.Tests/MonitorApi.Admin.Tests.csproj

# just the pure rules and arithmetic — no BigQuery credentials needed
dotnet test MonitorApi.Admin.Tests/MonitorApi.Admin.Tests.csproj \
  --filter "FullyQualifiedName~SingleFeedStallDetectorTests"
dotnet test MonitorApi.Admin.Tests/MonitorApi.Admin.Tests.csproj \
  --filter "FullyQualifiedName~DatasetStallDetectorTests"
dotnet test MonitorApi.Admin.Tests/MonitorApi.Admin.Tests.csproj \
  --filter "FullyQualifiedName~FeedIngestionErrorDetectorTests"
dotnet test MonitorApi.Admin.Tests/MonitorApi.Admin.Tests.csproj \
  --filter "FullyQualifiedName~MonitorSummariesTests"
dotnet test MonitorApi.Admin.Tests/MonitorApi.Admin.Tests.csproj \
  --filter "FullyQualifiedName~OrphanedChildrenDetectorTests"
dotnet test MonitorApi.Admin.Tests/MonitorApi.Admin.Tests.csproj \
  --filter "FullyQualifiedName~DatasetFutureDeclineDetectorTests"
dotnet test MonitorApi.Admin.Tests/MonitorApi.Admin.Tests.csproj \
  --filter "FullyQualifiedName~AdminSlugTests"
```

The detection rules live in `Services/Admin/SingleFeedStallDetector.cs`,
`Services/Admin/DatasetStallMonitor.cs`, `Services/Admin/FeedIngestionErrorMonitor.cs`,
`Services/Admin/OrphanedChildrenMonitor.cs` and `Services/Admin/DatasetFutureDeclineMonitor.cs`, and the
summary arithmetic in `Services/Admin/MonitorSummaries.cs`, all deliberately free of BigQuery and
ASP.NET types, and pinned
by deterministic unit tests against hand-written inputs. The
endpoint tests then only have to check wiring, the envelope, and invariants that hold whatever the live
data looks like on the day.
