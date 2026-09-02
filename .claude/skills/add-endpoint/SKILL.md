---
name: add-endpoint
description: Add or modify an endpoint on the OpenActive Monitor API — controller action, BigQuery query, response model, XML docs and integration tests. Use when asked to add a new route, change what an endpoint returns, or expose a new BigQuery table/column.
---

# Adding an endpoint

Everything lives in `Controllers/ApiController.cs`. There is no service layer, no repository and
no separate controller per route — resist adding one unless asked.

## Steps

1. **Table constant.** If the endpoint reads a table not already in `Tables.cs`, add a `const string`
   there. Reference it only as `Fq(Tables.Whatever)`, which expands to
   `` `project.dataset.table` ``.

2. **Action.** Add the method inside `#region Endpoints`, next to related endpoints. Shape:

   ```csharp
   /// <summary>
   /// Endpoint Title
   /// </summary>
   /// <remarks>
   /// What it returns, filter combination semantics, caching, any always-applied conditions.
   /// </remarks>
   /// <param name="publisher">One or more publisher names. A row matches if any of the supplied values is present.</param>
   [HttpGet("my-endpoint")]
   [ProducesResponseType(typeof(IEnumerable<MyRecord>), StatusCodes.Status200OK)]
   public async Task<ActionResult<IEnumerable<MyRecord>>> MyEndpoint(
       [FromQuery] string[]? publisher = null,
       [FromQuery] string[]? district = null,
       [FromQuery] string[]? region = null,
       [FromQuery] string[]? country = null)
   {
       var conditions = new List<string>();
       var parameters = new List<BigQueryParameter>();

       AddLocationFilters(conditions, parameters, district, region, country);
       AddPublisherFilter(conditions, parameters, publisher);

       var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

       var rows = (IAsyncEnumerable<Dictionary<string, object>>)await Execute(
           $"""
           SELECT DISTINCT something
           FROM {Fq(Tables.ActiveOpportunitiesSummary)}
           {where}
           ORDER BY something ASC
           """,
           parameters
       );

       return Ok(await rows.Select(MyRecord.FromBigQueryRow).ToListAsync());
   }
   ```

   The XML docs are the public API reference (Scalar / `/openapi/v1.json`), so write them for a
   consumer: state what an omitted filter means, and that multi-values are OR'd while different
   parameters are AND'd.

3. **Query rules.**
   - User input goes in `BigQueryParameter` only — `@name`, or `IN UNNEST(@name)` for arrays.
     Never string-interpolate a filter value.
   - Prefer the existing `Add*Filter` helpers over hand-written predicates; add a new helper in
     `#region Utilities` rather than inlining a clause you will need twice.
   - `Execute` returns `object` (really `IAsyncEnumerable<Dictionary<string, object>>`); cast it,
     as the existing code does.
   - Sorting that BigQuery can do should be done in SQL. Only sort in C# when the values were
     post-processed (see `NhsTrusts`, which sorts case-insensitively in memory).
   - When a filter cannot resolve to any row, return the empty result early instead of running the
     main query (see `FeedQuality`).

4. **Model.** Add a class in `Models/` when the response has a fixed shape. Properties are
   PascalCase — global snake_case serialization handles the wire format; use `[JsonPropertyName]`
   only for names snake_case gets wrong (e.g. `nhstrust_name`). Give it a
   `static FromBigQueryRow(Dictionary<string, object> row)` mapper, and use `BigQueryValueParser`
   (`AsLong`, `AsDouble`, `ParseJson`) for nullable numerics and JSON columns — BigQuery cell types
   are not reliably what you expect. Cells that are `null` are *absent* from the dictionary, so use
   `row.GetValueOrDefault(...)`, not `row[...]`, for nullable columns.

5. **Paginate** list endpoints over raw rows: mirror `OpportunityRecords` — clamp `limit`, fetch
   `limit + 1` to derive `HasMore`, return `PaginatedResponse<T>`. No total count is computed.

6. **Tests.** Add `MonitorApi.Tests/<Name>EndpointTests.cs` following the existing pattern:
   `IClassFixture<ApiFixture>`, a private `Get(string query)` helper using
   `_fixture.WithToken("/my-endpoint" + query)`. Tests run against live data, so assert on
   invariants, not values: sortedness, distinctness, filtered ⊆ unfiltered, non-existent filter
   returns empty, 403 without a token.

7. **Docs.** Add the route to the endpoint list in `README.md` and to `docs/development.md`
   (example URLs plus the filter table if the semantics are new).

## Reminders

- The whole controller is under `[OutputCache(PolicyName = "FourHours")]`, keyed by all query
  parameters — a manual re-check of a changed response can return a cached body.
- The `token` query parameter participates in cache keys and is checked in `OnActionExecuting`;
  new actions inherit both automatically.
