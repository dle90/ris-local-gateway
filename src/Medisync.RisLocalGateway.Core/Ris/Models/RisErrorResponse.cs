using System.Text.Json;
using System.Text.Json.Serialization;

namespace Medisync.RisLocalGateway.Core.Ris.Models;

/// <summary>
/// Response error chuẩn của his-core (BaseResponse trong base_response.go).
/// Khi controller trả về error qua HandleSystemError, body luôn có format này
/// với HTTP status thường là 400 (BadRequest) hoặc 401 (Unauthorized).
/// </summary>
public sealed class RisErrorResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; set; }
}
