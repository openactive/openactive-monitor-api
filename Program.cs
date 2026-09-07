using MonitorApi;
using Scalar.AspNetCore;
using System.Text.Json;

// Time of day, UTC, at which every cached admin response is discarded — shortly after the overnight
// ingestion pipeline has landed the new day's numbers.
var adminCacheRefreshTime = new TimeOnly(7, 0);

var builder = WebApplication.CreateBuilder(args);

builder.Services
	.AddOptions<BigQueryOptions>()
	.BindConfiguration(BigQueryOptions.SectionName)
	.ValidateDataAnnotations()
	.ValidateOnStart();

builder.Services
	.AddOptions<ApiOptions>()
	.BindConfiguration(ApiOptions.SectionName)
	.ValidateDataAnnotations()
	.ValidateOnStart();

builder.Services.AddOutputCache(options =>
{
	options.SizeLimit = 100 * 1024 * 1024;       
    options.MaximumBodySize = 12 * 1024 * 1024;  
	options.AddPolicy("FourHours", policy => policy
		.Expire(TimeSpan.FromHours(4))
		.SetVaryByQuery("*"));
	// The admin monitors describe one ingestion day, which only changes once the overnight pipeline has
	// landed, so their responses are held until the next morning refresh rather than for a fixed span.
	options.AddPolicy(DailyRefreshCachePolicy.PolicyName, new DailyRefreshCachePolicy(adminCacheRefreshTime));
});

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    });

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});

// Two OpenAPI documents, one per consumer: the analytics platform should not have to scroll past the
// admin dashboard's endpoints, and vice versa. Admin controllers are tagged via ApiExplorerSettings on
// AdminControllerBase; everything else is untagged and belongs to the analytics document.
builder.Services.AddOpenApi(ApiDocuments.Analytics, options =>
{
	options.ShouldInclude = api => api.GroupName != ApiDocuments.AdminGroupName;
	options.AddDocumentTransformer((document, _, _) =>
	{
		document.Info.Title = "OpenActive Monitor API";
		document.Info.Description =
			"Aggregated OpenActive data for the public dashboards. Every endpoint requires a valid " +
			"access token supplied as the `token` query parameter.";
		return Task.CompletedTask;
	});
});

builder.Services.AddOpenApi(ApiDocuments.Admin, options =>
{
	options.ShouldInclude = api => api.GroupName == ApiDocuments.AdminGroupName;
	options.AddDocumentTransformer((document, _, _) =>
	{
		document.Info.Title = "OpenActive Monitor Admin API";
		document.Info.Description =
			"Feed health monitors for the admin dashboard. Every endpoint requires the **admin** token " +
			"supplied as the `token` query parameter — the public access token is not accepted. " +
			"Responses share a `{ data, meta }` envelope and are paginated with `page` / `page_size`.";
		return Task.CompletedTask;
	});
});

var app = builder.Build();

app.MapOpenApi();

// Both documents in one reference, selectable from the dropdown.
app.MapScalarApiReference(options => options
	.AddDocument(ApiDocuments.Analytics, "Analytics API")
	.AddDocument(ApiDocuments.Admin, "Admin API"));

// The root would otherwise 404: ApiController's route template is "/" but every action on it has its
// own path. Send it to the API reference, which is what a bare host name is usually asking for.
app.MapGet("/", () => Results.Redirect("/scalar")).ExcludeFromDescription();

app.UseOutputCache();

app.MapControllers();

app.Run();

public partial class Program { }
