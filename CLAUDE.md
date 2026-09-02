# CLAUDE.md

## What this is

ASP.NET Core (net10.0) Web API that serves the OpenActive public dashboards by querying a
Google BigQuery analytics dataset. There is no database of its own and no ORM — every endpoint
builds a SQL string and runs it against BigQuery.

Deployed to Azure App Service from `main` (`.github/workflows/CD.yml`); PRs run the integration
test suite (`.github/workflows/CI.yml`).

## Two API surfaces

The project serves two independent consumers. Know which one you are working on before you start.

|                | Analytics platform | Admin dashboard |
|---|---|---|
| Routes | `/summary`, `/opportunities`, … | `/admin/*` |
| Token | `Api:AccessToken` | `Api:AdminToken` |
| Code | `Controllers/ApiController.cs` | `Controllers/Admin/*` |
| Responses | bare arrays / objects | `{ data, meta }` envelope |
| Output cache | 4 hours | 15 minutes |
| OpenAPI document | `/openapi/v1.json`, `/scalar/v1` | `/openapi/admin.json`, `/scalar/admin` |
| Tests | `MonitorApi.Tests` | `MonitorApi.Admin.Tests` |

They share only the BigQuery options and `Tables.cs`. The tokens are not interchangeable in either
direction, and an unset `AdminToken` makes the admin surface refuse everything rather than fall back.
Do not merge the two, and do not refactor `ApiController` while adding admin endpoints.

## Layout

| Path | Purpose |
|---|---|
| `Program.cs` | Minimal host: options binding, snake_case JSON, output cache, the two OpenAPI documents, Scalar |
| `ApiDocuments.cs` | OpenAPI document names and the admin `GroupName` |
| `Controllers/ApiController.cs` | **All** analytics endpoints plus their filter/query helpers |
| `Controllers/Admin/` | Admin dashboard controllers; `AdminControllerBase` holds auth + paging + query plumbing |
| `Models/` | Analytics response DTOs and BigQuery row → DTO mappers |
| `Models/Admin/` | Admin wire contracts (`AdminPage<T>`, incident/trend models) |
| `Services/Admin/` | Pure monitor logic and the SQL/parsing it runs on — no ASP.NET types |
| `Tables.cs` | BigQuery table name constants — never hardcode a table name |
| `ApiOptions.cs` / `BigQueryOptions.cs` | Bound from the `Api` / `BigQuery` config sections |
| `MonitorApi.Tests/` | Analytics integration tests against live BigQuery |
| `MonitorApi.Admin.Tests/` | Admin tests: pure rule tests (no credentials) + endpoint tests |
| `docs/development.md`, `docs/admin-api.md` | Endpoint lists and semantics — keep in sync with code |

## Key conventions

- **One analytics controller.** New analytics endpoints go in `ApiController` inside the
  `#region Endpoints` block; shared helpers go in `#region Utilities`. Admin endpoints go in a
  controller under `Controllers/Admin/` deriving from `AdminControllerBase` — see the
  `add-admin-endpoint` skill.
- **Admin detection logic stays pure.** Rules that decide what a monitor reports live in
  `Services/Admin/`, free of BigQuery and ASP.NET types, so they can be unit tested without
  credentials. Controllers load rows and hydrate output; they do not decide.
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
dotnet build                                                      # build
dotnet run                                                        # http://localhost:5268 (+ /scalar)
dotnet test MonitorApi.Tests/MonitorApi.Tests.csproj              # analytics; needs BigQuery credentials
dotnet test MonitorApi.Admin.Tests/MonitorApi.Admin.Tests.csproj  # admin; also needs Api:AdminToken

# admin detection rules only — pure, no credentials, ~40ms
dotnet test MonitorApi.Admin.Tests/MonitorApi.Admin.Tests.csproj \
  --filter "FullyQualifiedName~SingleFeedStallDetectorTests"
```

## Secrets

`appsettings.Development.json` and `openactive-monitor-*.json` (the GCP service account key) are
gitignored and must stay that way. Never paste token or credential values into commits, test
fixtures, docs, or terminal output. `appsettings.json` holds empty placeholder keys only.

## Data caveat

`opportunity_ingestion` currently holds only ~12 days of history (from 2026-08-20), with one day
missing and one duplicated. Anything reasoning over longer windows — the admin stall monitors' 120-day
lookback in particular — is correct in code but cannot yet be exercised by the data. Never assert on
absolute counts or long histories in a test.
