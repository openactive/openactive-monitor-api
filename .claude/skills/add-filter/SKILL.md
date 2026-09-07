---
name: add-filter
description: Add or change a query-string filter (publisher, activity, organization, nhs_trust, district/region/country, …) across the OpenActive Monitor API endpoints. Use when asked to make an endpoint filterable by something new or to change how existing filters combine.
---

# Adding a query filter

Filters are shared helpers in `Controllers/ApiController.cs` → `#region Utilities`. A filter is
almost never local to one endpoint: adding one usually means threading a new `string[]?` parameter
through several actions.

## Semantics to preserve

- **Within one parameter: OR.** `?activity=Yoga&activity=Pilates` matches rows with either.
- **Across parameters: AND.** `?activity=Yoga&publisher=X` matches rows with both.
- **Location is one clause.** `district`, `region` and `country` are OR'd *with each other* inside a
  single parenthesised condition (`AddLocationFilters`), then AND'd with everything else.
- **Multi-value syntax is universal.** Repeated (`?a=x&a=y`) and comma-separated (`?a=x,y`) must both
  work; `NormaliseMultiValue` splits, trims, drops blanks and de-duplicates. Always run the raw
  `string[]?` through it.
- **Absent filter = no condition**, not an empty result.

## Writing the helper

```csharp
private static void AddThingFilter(List<string> conditions, List<BigQueryParameter> parameters, string[]? thing, string column = "thing")
{
	var values = NormaliseMultiValue(thing);
	if (values.Count == 0) return;

	conditions.Add($"{column} IN UNNEST(@things)");
	parameters.Add(new BigQueryParameter("things", BigQueryDbType.Array, values) { ArrayElementType = BigQueryDbType.String });
}
```

Variants already in the file, reuse rather than reinvent:

| Situation | Pattern |
|---|---|
| Plain scalar column | `column IN UNNEST(@p)` — `AddPublisherFilter` |
| JSON array column | `EXISTS (SELECT 1 FROM UNNEST(JSON_EXTRACT_ARRAY(col)) AS x WHERE JSON_VALUE(x) IN UNNEST(@p))` — `AddActivityFilter`, `AddOrganizationFilter` |
| Two JSON arrays, either may match | OR the two `EXISTS` clauses — `AddOpportunityActivityFilter` |
| Same concept, different column per table | `string column = "default"` parameter — `AddPublisherFilter`, `AddNhsTrustFilter` |
| Sentinel value | `nhs_trust=all` (case-insensitive) means "any row with a trust code" and short-circuits the other values — `AddNhsTrustFilter` |

The summary table (`active_opportunities_summary`) and the raw table (`opportunities`) name the same
concept differently — `publisher` vs `publisher_name`, `organization_names` (JSON array) vs
`organization_name` (scalar), `activity_or_facility` vs separate `activity`/`facility`. That is why
some filters have a `column:` argument and some have a paired `AddOpportunity*` variant. Check which
table the endpoint queries before wiring a helper into it.

`feed_quality` has no publisher/location columns at all: `FeedQuality` resolves matching publishers
from the summary table first, then restricts `feed_quality` via `dataset_url` through the `feeds`
table. Any new filter on that endpoint goes into that first phase.

## Checklist

1. Add or extend the helper in `#region Utilities`.
2. Add the `[FromQuery] string[]? name = null` parameter to every endpoint that should accept it,
   keeping the existing parameter order (`publisher`, `district`, `region`, `country`, `activity`,
   `organization`, `nhs_trust`).
3. Add a `/// <param name="...">` line to each of those actions — it is published API documentation.
4. Add tests: filtered ⊆ unfiltered, non-existent value → empty, repeated vs comma-separated forms
   agree, and combining with another filter narrows further.
5. Update the filter table and example URLs in `docs/development.md`, and the endpoint bullet in
   `README.md` if the filter list it names has changed.
