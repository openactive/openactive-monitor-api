# OpenActive Monitor API

ASP.NET Core Web API that powers OpenActive public dashboards by querying the OpenActive analytics dataset in Google BigQuery.

## What This API Provides

- `GET /summary`: High-level aggregate metrics for the dashboard
- `GET /areas`: Location hierarchy (country -> regions -> districts)
- `GET /publishers`: Distinct publisher names (optional location filters)
- `GET /activities`: Distinct activities/facilities (optional location filters)
- `GET /nhs-trusts`: Distinct NHS Trusts (optional publisher/location/activity/organization filters)
- `GET /opportunities`: Active opportunities (optional publisher/location/activity filters)
- `GET /feed-quality`: Feed quality rows from `feed_quality` with selected quality metrics (optional publisher/location/activity/organization/nhs_trust filters)

All data is read from configured BigQuery tables in the configured project + dataset.

## Admin API

A separate `/admin` surface serves the admin dashboard, authenticated with its own token
(`Api:AdminToken`) and returning a paginated `{ data, meta }` envelope:

- `GET /admin/summary`: dashboard landing figures — monitored estate, current issues, per-monitor counts
- `GET /admin/single-feed-stall-incidents`: feeds that were publishing recently but have gone quiet
- `GET /admin/single-feed-stall-trend`: daily open/past-threshold stall counts
- `GET /admin/dataset-stall-incidents`: datasets whose feeds have all stopped publishing, freezing everything downstream
- `GET /admin/dataset-stall-trend`: daily open/past-threshold dataset-wide stall counts
- `GET /admin/feed-ingestion-error-incidents`: feeds whose ingestion is failing now but was completing recently
- `GET /admin/feed-ingestion-error-trend`: daily open/past-threshold ingestion error counts
- `GET /admin/dataset-orphaned-children-incidents`: datasets publishing Slots or ScheduledSessions whose parent event is missing from the same dataset
- `GET /admin/dataset-future-decline-incidents`: datasets whose future opportunity supply is draining while their feeds keep ingesting successfully
- `GET /admin/dataset-future-decline-trend`: daily open/past-threshold future-supply decline counts
- `GET /admin/feed-quality`: every assessed feed's quality row from `feed_quality`, plus an estate-wide quality summary (optional dataset_url/publisher filters)
- `GET /admin/feed-custom-properties`: schema drift — every feed publishing custom (non-vocabulary) properties from `custom_properties`, plus an estate-wide summary (optional dataset_url/publisher filters)
- `GET /admin/active-places-site-mappings`: every Active Places site the OpenActive data reaches, paired with the venue that reaches it (optional site_id/local_authority_code/publisher/match_method filters)
- `GET /admin/active-places-coverage`: the Active Places coverage report — headline coverage, breakdowns by area, ownership and publisher, and the unmatched OpenActive venues
- `GET /admin/publisher-notifications`: steward work list of publishers (BigQuery-backed; not cached)
- `GET /admin/publisher-notifications/summary`: status/alert counts for that list
- `GET /admin/ecosystem-growth-trend`: monthly datasets crawled (as the catalogues list them in `feed_ingestion`), added, removed, without future items, stopped publishing and not ingested by the pipeline, each with the list of datasets behind it
- `GET /admin/activity-coverage`: future opportunities per local authority district per OpenActive activity-list concept, for the coverage map; published labels are matched to the vocabulary with a tunable similarity threshold (optional activity/district/region/country/min_similarity)
- `GET /admin/activity-coverage/summary`: national activity headline figures — future activity opportunities, % with an activity label, % linked to the activity list, activity-list concepts on offer (optional min_similarity)
- `GET /admin/activity-coverage/top-activities`: activity-list concepts ranked nationally by future opportunities; top 10 by default (optional min_similarity)
- `GET /admin/catalog-requests`: dataset addition requests (community pull requests on `openactive/data-catalogs`) over the last year, with steward first-contact and completion times, plus a summary (optional status filter)
- `GET /admin/tooling-issues`: community issues on the OpenActive tooling repositories over the last year, with assignee and close time (optional repo/status filters)
- `POST /admin/publisher-notifications`: create a row
- `PATCH /admin/publisher-notifications/{id}`: update a row (writable fields; UI chooses which)
- `DELETE /admin/publisher-notifications/{id}`: delete a row
- `GET /admin/metric-snapshots`: the month-by-month metric series the KPI report is built from (optional source/field/period filters; not cached)
- `POST /admin/metric-snapshots`: capture a batch of measurements; a measurement may be captured once

See [docs/admin-api.md](docs/admin-api.md). The analytics token and the admin token are not
interchangeable in either direction.

See live docs at:

- https://openactivemonitorapi-cphbaxemfmgufddc.ukwest-01.azurewebsites.net/scalar/

The reference carries two documents, selectable from the dropdown: **Analytics API** (`/scalar/v1`) and
**Admin API** (`/scalar/admin`).

## Authentication

All public analytics endpoints require a token via query string:

```text
?token=<your-access-token>
```

If the token is missing or invalid, the API returns:

- `403 Forbidden`
- Body: `{ "message": "Please provide a valid token." }`

The `/admin` endpoints use `Api:AdminToken` in the same query parameter and answer
`{ "message": "Please provide a valid admin token." }` instead.

## Development

For detailed setup and deployment notes, see [docs/development.md](docs/development.md).
For the admin dashboard endpoints, see [docs/admin-api.md](docs/admin-api.md).
