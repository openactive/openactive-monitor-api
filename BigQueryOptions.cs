using Google.Apis.Auth.OAuth2;
using System;
using System.ComponentModel.DataAnnotations;
using System.IO;

namespace MonitorApi;

public class BigQueryOptions
{
	public const string SectionName = "BigQuery";

	[Required]
	public string? ProjectId { get; set; }

	[Required]
	public string? DatasetId { get; set; }

	[Required]
	public string? Credentials { get; set; }

	internal GoogleCredential GoogleCredential
	{
		get
		{
			if (string.IsNullOrWhiteSpace(Credentials))
			{
				throw new InvalidOperationException($"{nameof(Credentials)} must be provided.");
			}

			if (Credentials.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
			{
				var json = File.ReadAllText(Credentials);
				return GoogleCredential.FromJson(json);
			}

			return GoogleCredential.FromJson(Credentials);
		}
	}
}
