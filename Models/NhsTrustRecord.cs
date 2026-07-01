using System.Text.Json.Serialization;

namespace MonitorApi.Models;

public class NhsTrustRecord
{
	[JsonPropertyName("nhstrust_name")]
	public string NhsTrustName { get; set; } = "";

	[JsonPropertyName("nhstrust_code")]
	public string? NhsTrustCode { get; set; } = "";
}
