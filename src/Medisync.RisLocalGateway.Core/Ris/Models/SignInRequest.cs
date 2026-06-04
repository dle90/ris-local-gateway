using System.Text.Json.Serialization;

namespace Medisync.RisLocalGateway.Core.Ris.Models;

/// <summary>
/// Request body cho POST /v1/integration/auth/token.
/// Match IntegrationLoginRequest trong his-core.
/// </summary>
public sealed class SignInRequest
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;
}
