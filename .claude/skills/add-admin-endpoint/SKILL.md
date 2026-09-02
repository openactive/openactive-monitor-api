---
name: add-admin-endpoint
description: Add an endpoint to the /admin dashboard API — controller action on AdminControllerBase, pure monitor logic in Services/Admin, the { data, meta } envelope, and tests in MonitorApi.Admin.Tests. Use for any admin dashboard route; use add-endpoint instead for the public analytics API.
---

# Adding an admin endpoint

The admin surface is deliberately separate from the public analytics API: different token, different
controllers, different response envelope, different test project. **Do not touch `ApiController` or
`MonitorApi.Tests` while working here**, and do not reuse the public `AccessToken`.

## Where things go

| Concern | Location |
|---|---|
| Route + parameter binding + hydration | `Controllers/Admin/<Area>Controller.cs` |
| Auth, paging, `Fq`, `Query`, `QuerySingle` | `AdminControllerBase` (inherited — don't re-implement) |
| Wire contract (what the dashboard sees) | `Models/Admin/` |
| Rules that decide what gets reported | `Services/Admin/` — no BigQuery, no ASP.NET types |
| SQL text + row parsing | `Services/Admin/*Query.cs` |
| Tests | `MonitorApi.Admin.Tests/<Area>/` |
| Document names | `ApiDocuments.cs` |

Group related endpoints on one controller (`FeedStallsController` carries both single-feed stall
routes). With 30+ endpoints planned, one controller per monitor family, not per route.

## Steps

1. **Controller.** Derive from `AdminControllerBase`, which supplies the `admin` route prefix, the
   `AdminToken` check and the fifteen-minute output cache:

   ```csharp
   public class FeedStallsController(IOptions<BigQueryOptions> bigQueryOptions, IOptions<ApiOptions> apiOptions)
   	: AdminControllerBase(bigQueryOptions, apiOptions)
   {
   	[HttpGet("single-feed-stall-incidents")]
   	[ProducesResponseType(typeof(AdminPage<StallIncident>), StatusCodes.Status200OK)]
   	public async Task<ActionResult<AdminPage<StallIncident>>> SingleFeedStallIncidents(
   		int page = 1,
   		int page_size = DefaultPageSize,
   		[FromQuery] DateOnly? as_of = null)
   	{
   		// load rows -> apply pure rules -> hydrate -> paginate
   		return Ok(Paginate(rows, page, page_size, snapshotDate));
   	}
   }
   ```

   Routes are kebab-case (`single-feed-stall-incidents`) like the rest of the API; query parameters are
   snake_case (`page_size`, `as_of`) to match the JSON.

2. **Envelope.** Every admin endpoint returns `AdminPage<T>` via the inherited `Paginate` helper, which
   clamps `page` to ≥1 and `page_size` to 1–1000 and fills `meta`. Never hand-roll the envelope, and
   never return a bare array — the dashboard paginates every endpoint identically.

   `meta.snapshot_date` is the day the data describes (usually `MAX(DATE(...))` from the source table),
   **not** today. Resolve it from the data and let `as_of` override it.

3. **Pure rules.** Anything that decides *what* to report goes in `Services/Admin/` as a static class
   over plain records — see `SingleFeedStallDetector`. It must not reference BigQuery or ASP.NET.
   This is the point of the split: the rules get deterministic unit tests with no credentials, and the
   endpoint tests are left checking wiring and invariants.

4. **SQL.** Put query text and row parsing in a `Services/Admin/*Query.cs` static class taking already
   fully-qualified table names, so sibling monitors can reuse it. Run it with the inherited
   `Query(sql, parameters)` / `QuerySingle(...)`. All user input goes through `BigQueryParameter`;
   only `Fq(Tables.X)` is interpolated.

   Aggregate in SQL and decide in C#. Prefer collapsing to one row per entity (`ARRAY_AGG`) over
   pulling one row per entity per day.

5. **Tests** in `MonitorApi.Admin.Tests`:
   - **Rule tests** — no fixture, no credentials, hand-written inputs, exact expected values. Every
     rule and edge case belongs here.
   - **Endpoint tests** — `IClassFixture<AdminApiFixture>`, `_fixture.WithAdminToken(route + query)`.
     Live data, so assert invariants only: envelope shape, ordering, paging disjointness, clamping,
     monotonicity when a threshold moves, and cross-endpoint agreement. Never assert absolute counts.
   - **Auth** — add the new route to `AdminRoutes()` in `AdminAuthTests`, which covers missing token,
     wrong token, and the public token being rejected.

6. **Docs.** Add the route, its parameters and a sample payload to `docs/admin-api.md`, and the bullet
   in `README.md`. The XML doc comments are the published reference — write them for the dashboard
   developer, and state defaults and always-applied conditions.

   The admin surface has its own OpenAPI document (`/openapi/admin.json`, rendered at `/scalar/admin`).
   Endpoints land in it because `AdminControllerBase` carries
   `[ApiExplorerSettings(GroupName = ApiDocuments.AdminGroupName)]`, so a controller that derives from
   the base is documented automatically — don't re-tag it, and don't add admin routes to the analytics
   document.

## Gotchas

- **Every public method on a controller is a route unless told otherwise.** A public helper with no
  route attribute inherits the controller's own template and collides with anything else there —
  `AmbiguousMatchException` on every request to that path. Mark non-endpoint public methods
  `[NonAction]`, or make them `private`. This is why the `IActionFilter` methods on the base carry
  `[NonAction]`; `MonitorApi.Tests/RoutingTests.cs` pins it.
- `AdminControllerBase` fails closed: if `Api:AdminToken` is unset, every admin endpoint 403s. That is
  intentional — never add a fallback to `AccessToken`.
- `Api:AdminToken` is deliberately not `[Required]` on `ApiOptions`, so an environment without it still
  boots. Don't "fix" that with `[Required]`; it would break startup and CI.
- Columns that are `NULL` are **absent** from the row dictionary — use `row.GetValueOrDefault(...)` and
  the `BigQueryValueParser` helpers, not `row[...]`.
- Responses are cached for fifteen minutes varying by all query parameters; a manual re-check of a
  changed response can return a cached body.
- `opportunity_ingestion` holds only ~12 days of history, with gaps and same-day duplicate runs.
  Collapse duplicates in SQL, treat a missing day as "no evidence of publishing" rather than a
  publish, and never write a test that assumes a long history.
