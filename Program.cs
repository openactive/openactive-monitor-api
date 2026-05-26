using MonitorApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services
	.AddOptions<BigQueryOptions>()
	.BindConfiguration(BigQueryOptions.SectionName)
	.ValidateDataAnnotations()
	.ValidateOnStart();

builder.Services.AddControllers();

var app = builder.Build();

app.MapControllers();

app.Run();
