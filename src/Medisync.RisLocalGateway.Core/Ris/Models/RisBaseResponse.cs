using System.Text.Json.Serialization;

namespace Medisync.RisLocalGateway.Core.Ris.Models;

/// <summary>
/// JSON envelope của his-core (BaseResponse trong base_response.go).
/// Mọi controller (trừ auth/token trả raw SignInResponse) đều wrap data
/// trong format này: {code, message, result}.
/// </summary>
public sealed class RisBaseResponse<T>
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("result")]
    public T? Result { get; set; }
}
