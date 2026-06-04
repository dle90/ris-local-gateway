using System.Text.Json.Serialization;

namespace Medisync.RisLocalGateway.Core.Ris.Models;

/// <summary>
/// Response khi đăng nhập thành công (HTTP 200) tới endpoint
/// POST /v1/integration/auth/token của his-core.
/// </summary>
public sealed class SignInResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("expires_in")]
    public long ExpiresIn { get; set; }
}
