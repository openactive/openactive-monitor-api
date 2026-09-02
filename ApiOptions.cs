using System.ComponentModel.DataAnnotations;

namespace MonitorApi;

public class ApiOptions
{
	public const string SectionName = "Api";

	[Required]
	public string? AccessToken { get; set; }

	/// <summary>
	/// Token for the <c>/admin</c> endpoints. Deliberately not <c>[Required]</c> so an environment that
	/// does not serve the admin dashboard still boots; when it is unset every admin endpoint refuses
	/// all requests (see <c>AdminControllerBase</c>).
	/// </summary>
	public string? AdminToken { get; set; }
}
