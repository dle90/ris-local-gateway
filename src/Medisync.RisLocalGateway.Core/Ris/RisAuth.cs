using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Medisync.RisLocalGateway.Core.Ris.Models;
using Medisync.RisLocalGateway.Core.Utils;

namespace Medisync.RisLocalGateway.Core.Ris;

/// <summary>
/// Gọi endpoint đăng nhập RIS (POST sign-in) — DÙNG CHUNG cho login thật (HttpRisClient) và nút
/// "Kiểm tra tài khoản" (ConfigApp.HealthChecker). Stateless: build SignInRequest, POST, parse
/// SignInResponse / RisErrorResponse rồi trả RisResponse&lt;SignInResponse&gt;. KHÔNG cache token,
/// KHÔNG side-effect — caller tự quyết định dùng kết quả thế nào (gateway cache token; ConfigApp
/// chỉ báo hợp lệ/không).
/// </summary>
public static class RisAuth
{
    public static async Task<RisResponse<SignInResponse>> SignInAsync(
        HttpClient http,
        string baseUrl,
        string signInPath,
        string username,
        string password,
        CancellationToken ct = default)
    {
        var url = UrlHelper.Combine(baseUrl, signInPath);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new SignInRequest { Username = username, Password = password }),
        };

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        var code = (int)resp.StatusCode;
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (code == 200)
        {
            // Auth endpoint trả raw SignInResponse, KHÔNG wrap qua RisBaseResponse.
            var signIn = JsonUtil.TryDeserialize<SignInResponse>(body);
            if (signIn is null)
                return RisResponse<SignInResponse>.Failure(code, "HTTP 200 nhưng response không phải JSON chuẩn (SignInResponse)");
            if (string.IsNullOrEmpty(signIn.AccessToken))
                return RisResponse<SignInResponse>.Failure(code, "HTTP 200 nhưng response thiếu access_token");
            return RisResponse<SignInResponse>.Success(signIn, code);
        }

        var err = JsonUtil.TryDeserialize<RisErrorResponse>(body);
        return RisResponse<SignInResponse>.Failure(code, err?.Message ?? $"HTTP {code} {resp.ReasonPhrase}");
    }
}
