# Developer Guide

## Prerequisites

Install [.NET](https://dotnet.microsoft.com/en-us/download)

## Development Setup

1. Add your Google Application Credentials JSON file to the project root directory.

2. Create `appsettings.Development.json` by copying `appsettings.json` and put your variables to there.

   `Api:AccessToken` gates the public analytics endpoints and `Api:AdminToken` gates `/admin`. Set both
   — the admin endpoints refuse every request when `AdminToken` is empty, and the admin test suite
   fails fast with a message telling you so.

3. Update the `Credentials` value in `appsettings.Development.json` to point to the credentials file path.

   Alternatively, you can provide the encoded JSON content directly in the `Credentials` setting.

4. Run the application:

```bash
dotnet run
```

## API Documentation

Interactive API reference (Scalar) and the raw OpenAPI documents are available once running. There are
two documents, one per consumer, selectable from the dropdown in the Scalar reference:

```text
http://localhost:5268/scalar                # reference, both documents in a dropdown
http://localhost:5268/scalar/v1             # analytics endpoints
http://localhost:5268/scalar/admin          # /admin endpoints
http://localhost:5268/openapi/v1.json       # analytics OpenAPI document
http://localhost:5268/openapi/admin.json    # admin OpenAPI document
```

`/` redirects to `/scalar`. The docs endpoints are not gated by either token, so the admin document is
publicly readable — it exposes endpoint names and parameters, not the token.

A controller lands in the admin document by carrying `[ApiExplorerSettings(GroupName = "admin")]`,
which `AdminControllerBase` sets once for every admin controller. Untagged controllers go to the
analytics document. Document names live in `ApiDocuments.cs`.

## Authentication

Every public API endpoint requires a valid access token passed as the `token` query parameter (configured under `Api:AccessToken`). A missing or incorrect token returns HTTP 403 with `{ "message": "Please provide a valid token." }`.

The `/admin` endpoints use the same query parameter but check it against `Api:AdminToken`, and return `{ "message": "Please provide a valid admin token." }`. The two tokens are not interchangeable in either direction, and when `AdminToken` is unset the admin surface refuses every request rather than falling back. See [admin-api.md](admin-api.md).

## Available Endpoints

Once the application is running, the following endpoints will be available:

```text
http://localhost:5268/summary?token=
http://localhost:5268/areas?token=
http://localhost:5268/areas?socio=true&token=
http://localhost:5268/socio?token=
http://localhost:5268/socio?district=E09000003&token=
http://localhost:5268/publishers?token=
http://localhost:5268/publishers?district=E09000003&token=
http://localhost:5268/activities?token=
http://localhost:5268/activities?region=E12000007&token=
http://localhost:5268/opportunities?token=
http://localhost:5268/opportunities?publisher=Ashmole%20Trust&token=
http://localhost:5268/opportunities?district=E09000003&token=
http://localhost:5268/opportunities?region=E12000007&country=E92000001&token=
http://localhost:5268/opportunities?activity=Yoga&token=
http://localhost:5268/opportunities?activity=Yoga&activity=Pilates&token=
http://localhost:5268/opportunities?activity=Yoga,Pilates&token=
```

`/opportunities` accepts these optional filters, combined with AND when more than one is supplied:

| Parameter   | Matches column                                   |
|-------------|--------------------------------------------------|
| `publisher` | `publisher`                                      |
| `district`  | `district_code`                                  |
| `region`    | `region_code`                                    |
| `country`   | `country_code`                                   |
| `activity`  | any of the supplied values present in the `activity_or_facility` JSON array |

The `activity` filter accepts either a single value (`?activity=Yoga`) or multiple values — repeated (`?activity=Yoga&activity=Pilates`) or comma-separated (`?activity=Yoga,Pilates`). A row matches if **any** of the supplied activities is present (OR semantics within `activity`). The same multi-value behaviour applies to `/opportunity-records`, `/areas`, and `/publishers`.

`/publishers` returns all distinct publisher names in alphabetical order, accepting the optional `district`, `region`, `country`, and `activity` filters (same column mapping as `/opportunities`, AND-combined across parameters).

`/activities` returns every distinct activity/facility value (flattened from the `activity_or_facility` JSON array) in alphabetical order, accepting the same optional `district`, `region`, and `country` filters.

`/summary` returns aggregate metrics. Every endpoint on this controller is cached for four hours, varying by all query parameters; the `/admin` endpoints are cached for fifteen minutes.

`/areas` returns the location hierarchy (country → regions → districts), keyed by name. Districts with a null region are listed directly on the country under a `districts` key:

```json
{
  "England": {
    "country_code": "E92000001",
    "regions": [
      {
        "London": {
          "region_code": "E12000007",
          "districts": [
            { "district_name": "Barnet", "district_code": "E09000003" }
          ]
        }
      }
    ]
  }
}
```

Every country/region/district node also carries a `socio` field. It is `null` unless `?socio=true` is supplied, in which case it holds the socio-economic context (population, deprivation, Active Lives) for that area's ONS code — see `/socio` below.

`/socio` returns socio-economic context per area, keyed by ONS geography code (`area_code`, which matches `district_code`/`region_code`/`country_code`). It accepts optional `district`, `region`, and `country` filters (matched against `area_code`, OR-combined). `total_population` is available for all areas; deprivation (`imd25_*`) and Active Lives (`als_*`) metrics are England-only and null elsewhere.

## Deployment

GitHub Actions automatically deploys the code from main branch.

Live services can be accessed from:

```
https://openactivemonitorapi-cphbaxemfmgufddc.ukwest-01.azurewebsites.net/summary?token=
```

## Admin API

The admin dashboard is served from a separate `/admin` surface with its own token, controllers and test
project. See [admin-api.md](admin-api.md).

## Tests

There are two suites. Both are integration tests that boot the real app and query live BigQuery, except
the admin detection-rule tests, which are pure and need no credentials.

```
dotnet test MonitorApi.Tests/MonitorApi.Tests.csproj              # public analytics endpoints
dotnet test MonitorApi.Admin.Tests/MonitorApi.Admin.Tests.csproj  # /admin endpoints
```

CI writes `appsettings.Development.json` from repository secrets and runs both. The admin suite needs
the `API_ADMIN_TOKEN` secret in addition to the existing ones.