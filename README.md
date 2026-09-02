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

- `GET /admin/single-feed-stall-incidents`: feeds that were publishing recently but have gone quiet
- `GET /admin/single-feed-stall-trend`: daily open/past-threshold stall counts

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
