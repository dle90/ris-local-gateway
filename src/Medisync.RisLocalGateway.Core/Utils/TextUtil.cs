namespace Medisync.RisLocalGateway.Core.Utils;

/// <summary>
/// Tiện ích chuỗi dùng chung — tránh mỗi nơi tự viết một bản Truncate cho log.
/// </summary>
public static class TextUtil
{
    /// <summary>Cắt chuỗi cho log: dài quá <paramref name="max"/> thì thêm hậu tố "…(truncated)".</summary>
    public static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= max ? s : s.Substring(0, max) + "…(truncated)";
    }
}
