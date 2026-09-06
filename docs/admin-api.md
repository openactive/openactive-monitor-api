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
  by its own monitor, not a set of independent single-feed stalls. A feed that has never published also
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

## Source data

Both monitors read `opportunity_ingestion` (daily ingestion result per feed), joined to `feeds` for
descriptive fields and `feed_quality` for the score. Multiple ingestion runs on the same day are
collapsed into one day.

**The table currently holds only ~12 days of history** (from 2026-08-20). Consequences worth
remembering when reading the numbers:

- The 120-day lookback is aspirational — it can only see as far back as the table goes.
- Trend points read zero for the first `stall_days` (and `past_threshold_count` for the first
  `past_threshold_days`) after the earliest day of data: no feed can yet be *shown* to have been silent
  that long. With the current 12-day table, `past_threshold_count` is zero before 2026-08-27.
- Most open incidents are currently past threshold — 119 of 126 — because the bulk of them date back
  to the first day of data. Expect that proportion to fall as history accumulates.

## Tests

```bash
dotnet test MonitorApi.Admin.Tests/MonitorApi.Admin.Tests.csproj

# just the pure rules and arithmetic — no BigQuery credentials needed
dotnet test MonitorApi.Admin.Tests/MonitorApi.Admin.Tests.csproj \
  --filter "FullyQualifiedName~SingleFeedStallDetectorTests"
dotnet test MonitorApi.Admin.Tests/MonitorApi.Admin.Tests.csproj \
  --filter "FullyQualifiedName~MonitorSummariesTests"
```

The detection rules live in `Services/Admin/SingleFeedStallDetector.cs` and the summary arithmetic in
`Services/Admin/MonitorSummaries.cs`, both deliberately free of BigQuery and ASP.NET types, and pinned
by deterministic unit tests against hand-written inputs. The
endpoint tests then only have to check wiring, the envelope, and invariants that hold whatever the live
data looks like on the day.
