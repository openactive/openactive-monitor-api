using MonitorApi;
using Scalar.AspNetCore;
using System.Text.Json;

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

builder.Services.AddOpenApi();

var app = builder.Build();

app.MapOpenApi();
app.MapScalarApiReference();

app.UseOutputCache();

app.MapControllers();

app.Run();

public partial class Program { }
