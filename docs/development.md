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
http://localhost:5268/opportunities?token=
http://localhost:5268/opportunities?publisher=Ashmole%20Trust&token=
http://localhost:5268/opportunities?district=E09000003&token=
http://localhost:5268/opportunities?region=E12000007&country=E92000001&token=
http://localhost:5268/opportunities?activity=Yoga&token=
```

`/opportunities` accepts these optional filters, combined with AND when more than one is supplied:

| Parameter   | Matches column                                   |
|-------------|--------------------------------------------------|
| `publisher` | `publisher`                                      |
| `district`  | `district_code`                                  |
| `region`    | `region_code`                                    |
| `country`   | `country_code`                                   |
| `activity`  | value present in the `activity_or_facility` JSON array |

`/summary` returns aggregate metrics and is cached for one hour.

## Deployment

GitHub Actions automatically deploys the code from main branch.

Live services can be accessed from:

```
https://openactivemonitorapi-cphbaxemfmgufddc.ukwest-01.azurewebsites.net/summary?token=
```