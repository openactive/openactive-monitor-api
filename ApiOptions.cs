using System.ComponentModel.DataAnnotations;

namespace MonitorApi;

public class ApiOptions
{
	public const string SectionName = "Api";

	[Required]
	public string? AccessToken { get; set; }
}
