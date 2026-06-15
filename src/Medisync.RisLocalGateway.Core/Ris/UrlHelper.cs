using System;

namespace Medisync.RisLocalGateway.Core.Ris;

/// <summary>
/// Chuẩn hoá + ghép URL — DÙNG CHUNG cho HttpRisClient (gateway) và HealthChecker (ConfigApp),
/// tránh mỗi nơi tự viết một bản normalize/combine riêng (dễ lệch nhau).
/// </summary>
public static class UrlHelper
{
    /// <summary>Thêm scheme http:// nếu thiếu (cho phép user nhập "localhost:8100").</summary>
    public static string NormalizeBaseUrl(string url)
    {
        url = (url ?? string.Empty).Trim();
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }
        return "http://" + url;
    }

    /// <summary>Full URL = NormalizeBaseUrl(baseUrl) + path (đảm bảo đúng 1 dấu "/").</summary>
    public static string Combine(string baseUrl, string path)
    {
        var b = NormalizeBaseUrl(baseUrl).TrimEnd('/');
        var p = path ?? string.Empty;
        if (!p.StartsWith('/')) p = "/" + p;
        return b + p;
    }
}
