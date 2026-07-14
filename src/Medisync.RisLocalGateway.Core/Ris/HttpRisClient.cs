using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Medisync.RisLocalGateway.Core.Configuration;
using Medisync.RisLocalGateway.Core.Ris.Models;
using Medisync.RisLocalGateway.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Medisync.RisLocalGateway.Core.Ris;

/// <summary>
/// HTTP implementation gọi RIS API thật.
///
/// Token management:
/// - Đăng nhập lần đầu khi cần gọi API → cache access_token + expires_at.
/// - Các request sau dùng token cached (KHÔNG login lại mỗi request).
/// - Hết hạn (còn &lt; 30s) → refresh token trước khi call.
/// - API trả 401 → login lại 1 lần rồi retry. Nếu vẫn 401 → trả về failure.
/// - Sau mỗi lần login thành công, đặt lịch refresh CHỦ ĐỘNG trước khi token
///   hết hạn 5 phút (background timer), tránh để request phải chờ login đồng bộ.
///
/// Concurrency: SemaphoreSlim để bảo đảm chỉ 1 thread refresh token tại 1 thời điểm.
/// </summary>
public sealed class HttpRisClient : IRisClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly ConfigStore _configStore;
    private readonly ILogger<HttpRisClient> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string? _cachedToken;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;

    private static readonly TimeSpan TokenRefreshBuffer = TimeSpan.FromSeconds(30);

    // Refresh chủ động trước hạn 5 phút.
    private static readonly TimeSpan ProactiveRefreshLead = TimeSpan.FromMinutes(5);
    // Token đời quá ngắn (&lt; lead) → vẫn đặt lịch tối thiểu để tránh spin.
    private static readonly TimeSpan MinRefreshDelay = TimeSpan.FromSeconds(10);
    // Login thất bại → thử lại sau khoảng này để giữ vòng tự refresh.
    private static readonly TimeSpan LoginRetryDelay = TimeSpan.FromMinutes(1);

    // Timer chạy 1 lần cho mỗi chu kỳ; mỗi lần login thành công sẽ Change() lại.
    private Timer? _refreshTimer;
    // CancellationToken dài hạn cho refresh nền (khác CT của từng request).
    private readonly CancellationTokenSource _backgroundCts = new();

    public HttpRisClient(HttpClient http, ConfigStore configStore, ILogger<HttpRisClient> logger)
    {
        _http = http;
        _configStore = configStore;
        _logger = logger;
    }

    public async Task<RisResponse<List<LgsGetWorkListResponse>>> FetchWorkListAsync(
        LgsGetWorkListRequest request,
        CancellationToken ct = default)
    {
        var cfg = _configStore.Load();
        var url = CombineUrl(cfg.Ris.BaseUrl, GetEndpoint(cfg, e => e.WorkList));

        var raw = await SendWithAuthAsync(HttpMethod.Post, url, request, cfg, ct).ConfigureAwait(false);
        return ParseResult<List<LgsGetWorkListResponse>>(raw, HttpMethod.Post, url);
    }

    public async Task<RisResponse> NotifyMppsInProgressAsync(
        LgsMppsInProgressRequest request,
        CancellationToken ct = default)
    {
        var cfg = _configStore.Load();
        var url = CombineUrl(cfg.Ris.BaseUrl, GetEndpoint(cfg, e => e.MppsInProgress));
        var raw = await SendWithAuthAsync(HttpMethod.Post, url, request, cfg, ct).ConfigureAwait(false);
        return ParseVoid(raw, HttpMethod.Post, url);
    }

    public async Task<RisResponse> NotifyMppsCompletedAsync(
        LgsMppsCompletedRequest request,
        CancellationToken ct = default)
    {
        var cfg = _configStore.Load();
        var url = CombineUrl(cfg.Ris.BaseUrl, GetEndpoint(cfg, e => e.MppsCompleted));
        var raw = await SendWithAuthAsync(HttpMethod.Post, url, request, cfg, ct).ConfigureAwait(false);
        return ParseVoid(raw, HttpMethod.Post, url);
    }

    public async Task<RisResponse> NotifyMppsDiscontinuedAsync(
        LgsMppsDiscontinuedRequest request,
        CancellationToken ct = default)
    {
        var cfg = _configStore.Load();
        var url = CombineUrl(cfg.Ris.BaseUrl, GetEndpoint(cfg, e => e.MppsDiscontinued));
        var raw = await SendWithAuthAsync(HttpMethod.Post, url, request, cfg, ct).ConfigureAwait(false);
        return ParseVoid(raw, HttpMethod.Post, url);
    }

    public async Task<RisResponse> NotifyReceiveStudyInfoAsync(
        LgsReceiveStudyInfoRequest request,
        CancellationToken ct = default)
    {
        var cfg = _configStore.Load();
        var url = CombineUrl(cfg.Ris.BaseUrl, GetEndpoint(cfg, e => e.ReceiveStudyInfo));
        var raw = await SendWithAuthAsync(HttpMethod.Post, url, request, cfg, ct).ConfigureAwait(false);
        return ParseVoid(raw, HttpMethod.Post, url);
    }

    /// <summary>
    /// Token Bearer hiện hành (dùng cho STOW tới PACS). Dùng lại cơ chế cache/refresh
    /// như khi gọi RIS — cùng một danh tính gateway (ITG-LGS) mang company/facility.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        var cfg = _configStore.Load();
        return await GetTokenAsync(cfg, ct).ConfigureAwait(false);
    }

    // ============== HTTP core (1 nơi duy nhất gửi + 401-retry) ==============

    /// <summary>
    /// Kết quả HTTP thô sau 1 lần gửi có Bearer. <see cref="Error"/> != null nghĩa là lỗi tầng
    /// transport (không có HTTP response: mất token / exception mạng) — khi đó <see cref="Code"/> = 0.
    /// </summary>
    private readonly record struct RawResult(int Code, string Body, string? Error, long ElapsedMs);

    /// <summary>
    /// Gửi 1 request có Bearer, đọc code + body. KHÔNG parse nghiệp vụ — parse để <see cref="ParseResult"/>
    /// / <see cref="ParseVoid"/> tuỳ endpoint có/không payload. Gộp về đây để bỏ 2 bản CallOnce trùng.
    /// </summary>
    private async Task<RawResult> SendOnceAsync<TRequest>(
        HttpMethod method, string url, TRequest body, GatewayConfig cfg, CancellationToken ct)
    {
        var token = await GetTokenAsync(cfg, ct).ConfigureAwait(false);
        if (token is null)
        {
            _logger.LogError("RIS call {Method} {Url} aborted: không lấy được access token", method.Method, url);
            return new RawResult(0, string.Empty, "Không lấy được access token (login thất bại?)", 0);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogDebug("RIS call → {Method} {Url}", method.Method, url);

        try
        {
            using var req = new HttpRequestMessage(method, url)
            {
                Content = JsonContent.Create(body),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            sw.Stop();
            var code = (int)resp.StatusCode;
            var responseBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (code == 401)
            {
                _logger.LogWarning("RIS call ← {Method} {Url} HTTP 401 ({Ms}ms) — sẽ refresh token + retry",
                    method.Method, url, sw.ElapsedMilliseconds);
            }
            else if (code != 200 && code != 400)
            {
                // 200 (OK) + 400 (lỗi nghiệp vụ có message) do Parse* log; còn lại log lỗi kèm body ở đây.
                _logger.LogError("RIS call ← {Method} {Url} HTTP {Code} {Reason} ({Ms}ms) — body: {Body}",
                    method.Method, url, code, resp.ReasonPhrase, sw.ElapsedMilliseconds,
                    TextUtil.Truncate(responseBody, 500));
            }

            return new RawResult(code, responseBody, null, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "RIS call ✗ {Method} {Url} EXCEPTION ({Ms}ms) — {Type}: {Message}",
                method.Method, url, sw.ElapsedMilliseconds, ex.GetType().Name, ex.Message);
            return new RawResult(0, string.Empty, ex.Message, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>Gửi có Bearer; nếu 401 thì force refresh token + gửi lại đúng 1 lần.</summary>
    private async Task<RawResult> SendWithAuthAsync<TRequest>(
        HttpMethod method, string url, TRequest body, GatewayConfig cfg, CancellationToken ct)
    {
        var raw = await SendOnceAsync(method, url, body, cfg, ct).ConfigureAwait(false);
        if (raw.Code != (int)HttpStatusCode.Unauthorized) return raw;

        _logger.LogWarning("Got 401 from {Url}, forcing token refresh and retrying once", url);
        await ForceRefreshTokenAsync(cfg, ct).ConfigureAwait(false);

        raw = await SendOnceAsync(method, url, body, cfg, ct).ConfigureAwait(false);
        if (raw.Code == (int)HttpStatusCode.Unauthorized)
        {
            _logger.LogError("Still 401 after token refresh — credentials có thể sai hoặc bị thu hồi");
        }
        return raw;
    }

    /// <summary>Parse cho endpoint CÓ payload: 200 → deserialize RisBaseResponse&lt;TResult&gt;.result.</summary>
    private RisResponse<TResult> ParseResult<TResult>(RawResult raw, HttpMethod method, string url)
    {
        if (raw.Error is not null) return RisResponse<TResult>.Failure(raw.Code, raw.Error);

        var code = raw.Code;
        if (code == 200)
        {
            var envelope = JsonUtil.TryDeserialize<RisBaseResponse<TResult>>(raw.Body);
            if (envelope is null)
            {
                _logger.LogError("RIS call ← {Method} {Url} HTTP 200 ({Ms}ms) nhưng response không phải JSON chuẩn",
                    method.Method, url, raw.ElapsedMs);
                return RisResponse<TResult>.Failure(code, "Response 200 nhưng không phải JSON chuẩn (RisBaseResponse)");
            }
            if (envelope.Result is null)
            {
                _logger.LogWarning("RIS call ← {Method} {Url} HTTP 200 ({Ms}ms) — result null: {Message}",
                    method.Method, url, raw.ElapsedMs, envelope.Message);
                return RisResponse<TResult>.Failure(code, $"Response 200 nhưng field 'result' null: {envelope.Message}");
            }
            _logger.LogInformation("RIS call ← {Method} {Url} HTTP 200 ({Ms}ms) OK", method.Method, url, raw.ElapsedMs);
            return RisResponse<TResult>.Success(envelope.Result, code);
        }

        if (code == 400)
        {
            var msg = ExtractErrorMessage(raw.Body);
            _logger.LogWarning("RIS call ← {Method} {Url} HTTP 400 ({Ms}ms) — {Message}", method.Method, url, raw.ElapsedMs, msg);
            return RisResponse<TResult>.Failure(code, msg);
        }
        if (code == (int)HttpStatusCode.Unauthorized)
            return RisResponse<TResult>.Failure(code, "HTTP 401 Unauthorized");

        return RisResponse<TResult>.Failure(code, $"HTTP {code}");
    }

    /// <summary>Parse cho endpoint KHÔNG payload (3 MPPS trả result=null): chỉ xét status.</summary>
    private RisResponse ParseVoid(RawResult raw, HttpMethod method, string url)
    {
        if (raw.Error is not null) return RisResponse.Failure(raw.Code, raw.Error);

        var code = raw.Code;
        if (code == 200)
        {
            _logger.LogInformation("RIS call ← {Method} {Url} HTTP 200 ({Ms}ms) OK", method.Method, url, raw.ElapsedMs);
            return RisResponse.Success(code);
        }
        if (code == 400)
        {
            var msg = ExtractErrorMessage(raw.Body);
            _logger.LogWarning("RIS call ← {Method} {Url} HTTP 400 ({Ms}ms) — {Message}", method.Method, url, raw.ElapsedMs, msg);
            return RisResponse.Failure(code, msg);
        }
        if (code == (int)HttpStatusCode.Unauthorized)
            return RisResponse.Failure(code, "HTTP 401 Unauthorized");

        return RisResponse.Failure(code, $"HTTP {code}");
    }

    private static string ExtractErrorMessage(string body)
    {
        var err = JsonUtil.TryDeserialize<RisErrorResponse>(body);
        return err?.Message ?? "HTTP 400 (không parse được message)";
    }

    // ============== Token management ==============

    /// <summary>
    /// Lấy token: cache nếu còn hạn, refresh nếu hết hạn.
    /// </summary>
    private async Task<string?> GetTokenAsync(GatewayConfig cfg, CancellationToken ct)
    {
        if (_cachedToken is not null && _tokenExpiresAt > DateTimeOffset.UtcNow.Add(TokenRefreshBuffer))
        {
            var remaining = (int)(_tokenExpiresAt - DateTimeOffset.UtcNow).TotalSeconds;
            _logger.LogDebug("Token cache hit — còn {Remaining}s đến khi hết hạn", remaining);
            return _cachedToken;
        }
        _logger.LogInformation("Token cache miss/expired — login lấy token mới");
        await RefreshTokenIfNeededAsync(cfg, ct).ConfigureAwait(false);
        return _cachedToken;
    }

    private async Task RefreshTokenIfNeededAsync(GatewayConfig cfg, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-check sau khi acquire lock — có thể thread khác đã refresh xong
            if (_cachedToken is not null && _tokenExpiresAt > DateTimeOffset.UtcNow.Add(TokenRefreshBuffer))
            {
                return;
            }
            await DoLoginAsync(cfg, ct).ConfigureAwait(false);
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task ForceRefreshTokenAsync(GatewayConfig cfg, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Force: invalidate cache, login lại bất kể status
            _cachedToken = null;
            _tokenExpiresAt = DateTimeOffset.MinValue;
            await DoLoginAsync(cfg, ct).ConfigureAwait(false);
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task DoLoginAsync(GatewayConfig cfg, CancellationToken ct)
    {
        try
        {
            await DoLoginCoreAsync(cfg, ct).ConfigureAwait(false);
        }
        finally
        {
            // Dù login thành công hay thất bại đều đặt lịch refresh kế tiếp:
            // - thành công → refresh trước hạn 5 phút,
            // - thất bại  → thử lại sau LoginRetryDelay.
            ScheduleNextRefresh();
        }
    }

    private async Task DoLoginCoreAsync(GatewayConfig cfg, CancellationToken ct)
    {
        try
        {
            var password = SecretProtector.Unprotect(cfg.Ris.ProtectedPassword);

            // DÙNG CHUNG hàm đăng nhập với nút "Kiểm tra tài khoản" (ConfigApp) — RisAuth.SignInAsync.
            // Khác biệt duy nhất ở đây: thành công thì CACHE token cho các call sau dùng.
            var result = await RisAuth.SignInAsync(
                _http, cfg.Ris.BaseUrl, GetEndpoint(cfg, e => e.SignIn), cfg.Ris.Username, password, ct)
                .ConfigureAwait(false);

            if (!result.IsSuccess || result.Data is null)
            {
                _logger.LogError("Login failed: HTTP {Code} — {Message}", result.HttpStatusCode, result.ErrorMessage);
                _cachedToken = null;
                _tokenExpiresAt = DateTimeOffset.MinValue;
                return;
            }

            _cachedToken = result.Data.AccessToken;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(result.Data.ExpiresIn > 0 ? result.Data.ExpiresIn : 3600);
            _logger.LogInformation("Login OK — token cached, expires at {Expiry}", _tokenExpiresAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login exception");
            _cachedToken = null;
            _tokenExpiresAt = DateTimeOffset.MinValue;
        }
    }

    /// <summary>
    /// Đặt lịch refresh token kế tiếp dựa trên trạng thái hiện tại:
    /// - Còn token hợp lệ → refresh trước khi hết hạn <see cref="ProactiveRefreshLead"/> (5 phút).
    /// - Không có token (login lỗi) → thử lại sau <see cref="LoginRetryDelay"/>.
    /// Gọi bên trong _tokenLock (từ DoLoginAsync) nên thao tác timer được serialize.
    /// </summary>
    private void ScheduleNextRefresh()
    {
        if (_backgroundCts.IsCancellationRequested)
        {
            return;
        }

        TimeSpan delay;
        if (_cachedToken is not null && _tokenExpiresAt > DateTimeOffset.UtcNow)
        {
            delay = _tokenExpiresAt - ProactiveRefreshLead - DateTimeOffset.UtcNow;
            if (delay < MinRefreshDelay)
            {
                delay = MinRefreshDelay;
            }
        }
        else
        {
            delay = LoginRetryDelay;
        }

        try
        {
            _refreshTimer ??= new Timer(_ => _ = ProactiveRefreshAsync());
            _refreshTimer.Change(delay, Timeout.InfiniteTimeSpan);
            _logger.LogInformation("Đặt lịch refresh token sau {DelaySec:n0}s (lúc {When:u})",
                delay.TotalSeconds, DateTimeOffset.UtcNow.Add(delay));
        }
        catch (ObjectDisposedException)
        {
            // Đang shutdown — bỏ qua.
        }
    }

    /// <summary>
    /// Callback của timer: refresh token nền trước khi hết hạn. Bản thân
    /// DoLoginAsync sẽ tự đặt lịch chu kỳ kế tiếp.
    /// </summary>
    private async Task ProactiveRefreshAsync()
    {
        if (_backgroundCts.IsCancellationRequested)
        {
            return;
        }

        try
        {
            var cfg = _configStore.Load();
            _logger.LogInformation("Proactive refresh — token sắp hết hạn (trước 5 phút), login lấy token mới");
            await ForceRefreshTokenAsync(cfg, _backgroundCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            // DoLoginAsync đã tự bắt lỗi + đặt lịch retry; đây chỉ là lưới an toàn
            // cho lỗi ngoài login (vd Load config) để không mất vòng tự refresh.
            _logger.LogError(ex, "Proactive token refresh lỗi — đặt lịch thử lại");
            ScheduleNextRefresh();
        }
    }

    // ============== Utilities ==============

    private static string GetEndpoint(GatewayConfig cfg, Func<RisEndpoints, string> selector)
    {
        var endpoints = cfg.Ris.Endpoints ?? RisEndpoints.CreateDefault();
        var path = selector(endpoints);
        return string.IsNullOrWhiteSpace(path) ? selector(RisEndpoints.CreateDefault()) : path;
    }

    // Ghép URL dùng helper chung (UrlHelper) — không tự normalize riêng nữa.
    private static string CombineUrl(string baseUrl, string path) => UrlHelper.Combine(baseUrl, path);

    public void Dispose()
    {
        _backgroundCts.Cancel();
        _refreshTimer?.Dispose();
        _backgroundCts.Dispose();
        _tokenLock.Dispose();
    }
}
