---
name: run-tests
description: Run the OpenActive Monitor API test suite or the app locally. Use when asked to run tests, verify a change, debug a failing or hanging test, or start the API. Covers the live-BigQuery credential setup the tests require.
---

# Running tests and the app

## The important caveat

`MonitorApi.Tests` are **integration tests against live BigQuery**, not unit tests. `ApiFixture`
boots the real `Program` in-process (`WebApplicationFactory<Program>`) and every request issues real
BigQuery queries billed to the configured project. There are no mocks and no fixtures. Consequences:

- The suite needs valid credentials or *everything* fails at startup, not with a useful assertion.
- Tests are slow and can be rate/quota limited. A "hang" is usually a slow query, not a deadlock.
- Assertions must hold for any data: sortedness, distinctness, subset relations, empty-on-nonsense
  filters. Never assert an exact count or a specific row.

## Setup

Both files are gitignored; if they are missing the developer must supply them — do not invent values
and do not commit either file.

1. `openactive-monitor-*.json` — GCP service account key, in the repo root.
2. `appsettings.Development.json` — copy of `appsettings.json` with real values:

```json
{
	"BigQuery": {
		"ProjectId": "<project>",
		"DatasetId": "<dataset>",
		"Credentials": "openactive-monitor-xxxxxxxx.json"
	},
	"Api": {
		"AccessToken": "<token>"
	}
}
```

`BigQuery:Credentials` is either a path ending in `.json` or the credential JSON inline
(see `BigQueryOptions.GoogleCredential`). `ApiFixture` rewrites a relative path to an absolute one
by walking up to `MonitorApi.csproj`, so tests work from any working directory. The fixture reads
`Api:AccessToken` from this same file and appends it to every request via `WithToken`.

Options are validated with `ValidateOnStart`, so a missing key fails fast at boot.

## Commands

```bash
dotnet build                                                        # compile check — do this first
dotnet test MonitorApi.Tests/MonitorApi.Tests.csproj                # full suite
dotnet test MonitorApi.Tests/MonitorApi.Tests.csproj \
  --filter "FullyQualifiedName~NhsTrustsEndpointTests"              # one class
dotnet run                                                          # http://localhost:5268
```

Browse `http://localhost:5268/scalar` for interactive docs, or
`http://localhost:5268/openapi/v1.json` for the raw document. Both are ungated; every other route
needs `?token=<Api:AccessToken>`.

## Debugging failures

- **All tests fail immediately** → configuration or credentials, not the code under test. Check
  `appsettings.Development.json` and that the credentials file resolves.
- **403 in a test** → the token was not appended; use `_fixture.WithToken(path)`.
- **A stale or unchanged response when checking manually** → the four-hour output cache. Vary a
  query parameter or restart the app; the cache is in-memory.
- **`KeyNotFoundException` mapping a row** → the column was `NULL`, so it is absent from the row
  dictionary. Use `row.GetValueOrDefault(...)` and the `BigQueryValueParser` helpers.

CI (`.github/workflows/CI.yml`) runs the same suite on PRs to `main`, writing the credentials file
and `appsettings.Development.json` from repository secrets. Do not print token or credential values
into logs or test output.
