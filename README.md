# OpenActive Monitor API

ASP.NET Core Web API that powers OpenActive public dashboards by querying the OpenActive analytics dataset in Google BigQuery.

## What This API Provides

- `GET /summary`: High-level aggregate metrics for the dashboard
- `GET /areas`: Location hierarchy (country -> regions -> districts)
- `GET /publishers`: Distinct publisher names (optional location filters)
- `GET /activities`: Distinct activities/facilities (optional location filters)
- `GET /opportunities`: Active opportunities (optional publisher/location/activity filters)
- `GET /feed-quality`: Feed quality rows from `feed_quality` with selected quality metrics

All data is read from configured BigQuery tables in the configured project + dataset.

See live docs at:

- https://openactivemonitorapi-cphbaxemfmgufddc.ukwest-01.azurewebsites.net/scalar/

## Authentication

All controller endpoints require a token via query string:

```text
?token=<your-access-token>
```

If the token is missing or invalid, the API returns:

- `403 Forbidden`
- Body: `{ "message": "Please provide a valid token." }`

## Development

For detailed setup and deployment notes, see [docs/development.md](docs/development.md).
