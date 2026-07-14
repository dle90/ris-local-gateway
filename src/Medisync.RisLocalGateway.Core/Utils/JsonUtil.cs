using System.Text.Json;

namespace Medisync.RisLocalGateway.Core.Utils;

/// <summary>
/// Tiện ích JSON dùng chung — gom một chỗ để HttpRisClient / RisAuth không mỗi nơi tự viết
/// một bản TryDeserialize riêng (dễ lệch options / xử lý lỗi).
/// </summary>
public static class JsonUtil
{
    /// <summary>Deserialize an toàn: body rỗng hoặc parse lỗi → trả null (không ném).</summary>
    public static T? TryDeserialize<T>(string body) where T : class
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonSerializer.Deserialize<T>(body); }
        catch { return null; }
    }
}
