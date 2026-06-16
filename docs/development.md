# Developer Guide

## Prerequisites

Install [.NET](https://dotnet.microsoft.com/en-us/download)

## Development Setup

1. Add your Google Application Credentials JSON file to the project root directory.

2. Create `appsettings.Development.json` by copying `appsettings.json` and put your variables to there.

3. Update the `Credentials` value in `appsettings.Development.json` to point to the credentials file path.

   Alternatively, you can provide the encoded JSON content directly in the `Credentials` setting.

4. Run the application:

```bash
dotnet run
```

## API Documentation

Interactive API reference (Scalar) and the raw OpenAPI document are available once running:

```text
http://localhost:5268/scalar
http://localhost:5268/openapi/v1.json
```

The docs endpoints are not gated by the access token.

## Authentication

Every API endpoint requires a valid access token passed as the `token` query parameter (configured under `Api:AccessToken`). A missing or incorrect token returns HTTP 403 with `{ "message": "Please provide a valid token." }`.

## Available Endpoints

Once the application is running, the following endpoints will be available:

```text
http://localhost:5268/summary?token=
http://localhost:5268/areas?token=
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

`/summary` returns aggregate metrics and is cached for one hour.

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

## Deployment

GitHub Actions automatically deploys the code from main branch.

Live services can be accessed from:

```
https://openactivemonitorapi-cphbaxemfmgufddc.ukwest-01.azurewebsites.net/summary?token=
```

## UnitTests

Run the following command in the project root folder:

```
dotnet test MonitorApi.Tests/MonitorApi.Tests.csproj
```