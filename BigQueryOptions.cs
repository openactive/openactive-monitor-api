using Google.Apis.Auth.OAuth2;
using System.ComponentModel.DataAnnotations;

namespace MonitorApi;

public class BigQueryOptions
{
	public const string SectionName = "BigQuery";

	[Required]
	public string? ProjectId { get; set; }

	[Required]
	public string? Credentials { get; set; }

	internal GoogleCredential GoogleCredential => GoogleCredential.FromJson(Credentials);
}
