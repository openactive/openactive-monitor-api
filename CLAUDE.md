# CLAUDE.md

## What this is

ASP.NET Core (net10.0) Web API that serves the OpenActive public dashboards by querying a
Google BigQuery analytics dataset. There is no database of its own and no ORM — every endpoint
builds a SQL string and runs it against BigQuery.

Deployed to Azure App Service from `main` (`.github/workflows/CD.yml`); PRs run the integration
test suite (`.github/workflows/CI.yml`).

## Layout

| Path | Purpose |
|---|---|
| `Program.cs` | Minimal host: options binding, snake_case JSON, output cache, OpenAPI/Scalar |
| `Controllers/ApiController.cs` | **All** endpoints plus the shared filter/query helpers |
| `Models/` | Response DTOs and BigQuery row → DTO mappers |
| `Tables.cs` | BigQuery table name constants — never hardcode a table name |
| `ApiOptions.cs` / `BigQueryOptions.cs` | Bound from the `Api` / `BigQuery` config sections |
| `MonitorApi.Tests/` | xUnit integration tests that boot the real app and hit live BigQuery |
| `docs/development.md` | Setup, endpoint list, filter semantics — keep in sync with code |

## Key conventions

- **One controller.** New endpoints go in `ApiController` inside the `#region Endpoints`
  block; shared helpers go in `#region Utilities`.
- **Auth** is a single query-string token (`?token=`) checked in `ApiController.OnActionExecuting`
  against `Api:AccessToken`. Failure is `403` with `{ "message": "Please provide a valid token." }`.
  There is no ASP.NET auth middleware.
- **Never interpolate user input into SQL.** Use `BigQueryParameter` (`@name` / `IN UNNEST(@name)`).
  Table names are interpolated only via `Fq(Tables.X)`.
- **Filter semantics:** values within one parameter are OR'd, different parameters are AND'd.
  `district`/`region`/`country` are the exception — they OR against each other as one location clause.
  Every filter accepts repeated (`?a=x&a=y`) or comma-separated (`?a=x,y`) values via
  `NormaliseMultiValue`.
- **JSON output is snake_case** (configured globally in `Program.cs`). Deviations use
  `[JsonPropertyName]` on the model.
- **Output caching:** the controller carries `[OutputCache(PolicyName = "FourHours")]`, varying by
  all query parameters. Responses are stale for up to four hours — expect this when testing manually.
- **Style:** tabs for indentation, file-scoped namespaces, XML doc comments on every endpoint
  (they are the published Scalar/OpenAPI docs, so write them for API consumers).

## Commands

```bash
dotnet build                                          # build
dotnet run                                            # http://localhost:5268 (+ /scalar)
dotnet test MonitorApi.Tests/MonitorApi.Tests.csproj  # needs live BigQuery credentials
```

## Secrets

`appsettings.Development.json` and `openactive-monitor-*.json` (the GCP service account key) are
gitignored and must stay that way. Never paste token or credential values into commits, test
fixtures, docs, or terminal output. `appsettings.json` holds empty placeholder keys only.
