using MonitorApi;
using Scalar.AspNetCore;

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

builder.Services.AddMemoryCache();

builder.Services.AddControllers();

builder.Services.AddOpenApi();

var app = builder.Build();

app.MapOpenApi();
app.MapScalarApiReference();

app.MapControllers();

app.Run();
