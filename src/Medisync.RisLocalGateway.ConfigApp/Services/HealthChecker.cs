using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Medisync.RisLocalGateway.Core.Ris.Models;

namespace Medisync.RisLocalGateway.ConfigApp.Services;

public sealed class HealthChecker
{
    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    public async Task<(bool listening, string message)> ProbeDicomListenerAsync(int port, CancellationToken ct = default)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync("127.0.0.1", port);
            var timeout = Task.Delay(TimeSpan.FromSeconds(2), ct);
            var finished = await Task.WhenAny(connect, timeout).ConfigureAwait(false);
            if (finished == timeout)
            {
                return (false, "Timeout sau 2s — port không LISTEN");
            }
            await connect.ConfigureAwait(false);
            return (true, $"Port {port} đang LISTEN");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool ok, long latencyMs, string message)> ProbeRisAsync(string baseUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return (false, 0, "Base URL trống");
        }

        // Auto-prepend scheme nếu user nhập "localhost:8100" hoặc "192.168.1.1:8100"
        var normalized = NormalizeUrl(baseUrl);

        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, normalized);
            using var resp = await SharedHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            sw.Stop();
            var code = (int)resp.StatusCode;
            // 2xx, 3xx, 4xx đều = host reachable. 5xx = server lỗi nhưng vẫn alive.
            return (true, sw.ElapsedMilliseconds, $"HTTP {code} ({sw.ElapsedMilliseconds}ms)");
        }
        catch (TaskCanceledException)
        {
            sw.Stop();
            return (false, sw.ElapsedMilliseconds, "Timeout");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    /// <summary>
    /// Kiểm tra kết nối tới PACS bằng cách GET chính STOW URL (.../wado/studies) — đây là
    /// QIDO đọc (public, không cần token), đi qua dicomweb-proxy → Orthanc nên kiểm được
    /// cả chuỗi. Thêm limit=1 cho nhẹ.
    /// </summary>
    public async Task<(bool ok, long latencyMs, string message)> ProbePacsAsync(string stowUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(stowUrl))
        {
            return (false, 0, "STOW URL trống");
        }

        var probe = NormalizeUrl(stowUrl);
        probe += probe.Contains('?') ? "&limit=1" : "?limit=1";

        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, probe);
            using var resp = await SharedHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            sw.Stop();
            var code = (int)resp.StatusCode;
            if (code == 200)
                return (true, sw.ElapsedMilliseconds, $"PACS OK — đọc được qua proxy (HTTP 200, {sw.ElapsedMilliseconds}ms)");
            if (code is 502 or 503 or 504)
                return (false, sw.ElapsedMilliseconds, $"Proxy lên nhưng PACS không phản hồi (HTTP {code})");
            if (code is 401 or 403)
                return (false, sw.ElapsedMilliseconds, $"Proxy chặn đọc (HTTP {code}) — chính sách đọc có thể đang yêu cầu token");
            return (false, sw.ElapsedMilliseconds, $"HTTP {code} {resp.ReasonPhrase} ({sw.ElapsedMilliseconds}ms)");
        }
        catch (TaskCanceledException)
        {
            sw.Stop();
            return (false, sw.ElapsedMilliseconds, "Timeout — không kết nối được tới proxy/PACS");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, sw.ElapsedMilliseconds, "Không kết nối được: " + ex.Message);
        }
    }

    public async Task<(bool ok, string message)> ProbeSignInAsync(
        string baseUrl,
        string signInPath,
        string username,
        string password,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return (false, "Base URL trống");
        if (string.IsNullOrWhiteSpace(signInPath)) return (false, "SignIn path trống — cấu hình endpoint trước");
        if (string.IsNullOrWhiteSpace(username)) return (false, "Tài khoản trống");
        if (string.IsNullOrWhiteSpace(password)) return (false, "Mật khẩu trống");

        var fullUrl = CombineUrl(NormalizeUrl(baseUrl), signInPath);

        try
        {
            var payload = JsonSerializer.Serialize(new { username, password });
            using var req = new HttpRequestMessage(HttpMethod.Post, fullUrl)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };

            using var resp = await SharedHttp.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            var code = (int)resp.StatusCode;
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (code == 200)
            {
                var signIn = TryDeserialize<SignInResponse>(body);
                if (signIn is null)
                {
                    return (false, "HTTP 200 nhưng response không phải JSON chuẩn");
                }
                if (string.IsNullOrEmpty(signIn.AccessToken))
                {
                    return (false, "HTTP 200 nhưng response thiếu access_token");
                }
                return (true, "Tài khoản hợp lệ");
            }

            if (code == 400)
            {
                var error = TryDeserialize<RisErrorResponse>(body);
                if (error?.Message is { Length: > 0 } msg)
                {
                    return (false, msg);
                }
                return (false, "HTTP 400 (không parse được message từ response)");
            }

            return (false, $"HTTP {code} {resp.ReasonPhrase}");
        }
        catch (TaskCanceledException)
        {
            return (false, "Timeout");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static T? TryDeserialize<T>(string body) where T : class
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(body);
        }
        catch
        {
            return null;
        }
    }

    private static string CombineUrl(string baseUrl, string path)
    {
        var b = baseUrl.TrimEnd('/');
        var p = path.StartsWith("/") ? path : "/" + path;
        return b + p;
    }

    private static string NormalizeUrl(string url)
    {
        url = url.Trim();
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }
        return "http://" + url;
    }
}
