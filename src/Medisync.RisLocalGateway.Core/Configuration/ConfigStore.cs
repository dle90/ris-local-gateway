using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Medisync.RisLocalGateway.Core.Configuration;

/// <summary>
/// Nguồn cấu hình DUY NHẤT của tiến trình. Nạp config.json vào MEMORY một lần rồi các lần đọc sau
/// (<see cref="Load"/>) đều trả bản in-memory — KHÔNG đọc ổ đĩa trên hot path (mỗi C-FIND / MPPS /
/// forward STOW).
///
/// Invalidate bản in-memory:
///   • <see cref="Save"/> / <see cref="SaveAsync"/> — cùng tiến trình sửa (ConfigApp): ghi đĩa xong
///     cập nhật cache ngay.
///   • <see cref="Reload"/> — tiến trình KHÁC sửa file (ConfigApp ghi, Service đọc): FileSystemWatcher
///     của GatewayWorker gọi Reload() để đọc lại đĩa + thay cache.
///
/// Thread-safe: gán tham chiếu <c>_cache</c> là atomic (đọc lock-free); ghi/đọc-đĩa serialize qua
/// <c>FileLock</c>.
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    // Bản in-memory — nguồn đọc chính. null = chưa nạp lần nào.
    private volatile GatewayConfig? _cache;

    /// <summary>
    /// Trả cấu hình từ MEMORY. Lần đầu (cache trống) nạp từ đĩa đúng một lần rồi giữ lại; các lần
    /// sau KHÔNG chạm đĩa. Muốn ép đọc lại file dùng <see cref="Reload"/>.
    /// LƯU Ý: trả về instance cache DÙNG CHUNG — chỉ ĐỌC, không mutate. Luồng chỉnh sửa
    /// (ConfigApp) dùng <see cref="LoadCopy"/> để lấy bản copy sửa tự do rồi <see cref="Save"/>.
    /// </summary>
    public GatewayConfig Load()
    {
        var cached = _cache;
        if (cached is not null) return cached;

        FileLock.Wait();
        try
        {
            // Double-check: thread khác có thể đã nạp trong lúc chờ lock.
            return _cache ??= ReadFromDisk() ?? GatewayConfig.CreateDefault();
        }
        finally
        {
            FileLock.Release();
        }
    }

    /// <summary>
    /// Bản DEEP COPY của cấu hình để CHỈNH SỬA (ConfigApp): mutate thoải mái không đụng cache;
    /// cache chỉ đổi khi <see cref="Save"/> thành công (Save lỗi → memory vẫn khớp đĩa).
    /// </summary>
    public GatewayConfig LoadCopy()
    {
        var json = JsonSerializer.Serialize(Load(), JsonOptions);
        return JsonSerializer.Deserialize<GatewayConfig>(json, JsonOptions) ?? GatewayConfig.CreateDefault();
    }

    /// <summary>
    /// Ép đọc lại config.json từ đĩa và thay bản in-memory (invalidate). Dùng khi file bị sửa bởi
    /// tiến trình khác (ConfigApp) — GatewayWorker phát hiện qua FileSystemWatcher rồi gọi hàm này.
    /// Đọc đĩa LỖI (file đang bị writer giữ / JSON hỏng dở) → GIỮ NGUYÊN bản in-memory hiện có,
    /// KHÔNG thay bằng default — tránh gateway chạy config trắng chỉ vì một lần đọc trượt.
    /// </summary>
    public GatewayConfig Reload()
    {
        FileLock.Wait();
        try
        {
            var fresh = ReadFromDisk();
            if (fresh is not null)
            {
                _cache = fresh;
            }
            return _cache ??= GatewayConfig.CreateDefault();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public void Save(GatewayConfig config)
    {
        FileLock.Wait();
        try
        {
            WriteToDisk(config);
            _cache = config; // đồng bộ cache ngay trong cùng tiến trình
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task SaveAsync(GatewayConfig config, CancellationToken ct = default)
    {
        await FileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteToDiskAsync(config, ct).ConfigureAwait(false);
            _cache = config; // đồng bộ cache ngay trong cùng tiến trình
        }
        finally
        {
            FileLock.Release();
        }
    }

    // ============== Disk I/O (gọi bên trong FileLock) ==============

    /// <summary>Đọc config từ đĩa. File chưa có → tạo default. Đọc/parse LỖI → null (caller quyết định giữ cache cũ hay dùng default).</summary>
    private static GatewayConfig? ReadFromDisk()
    {
        ConfigPaths.EnsureDirectories();
        if (!File.Exists(ConfigPaths.ConfigFile))
        {
            var fresh = GatewayConfig.CreateDefault();
            try { WriteToDisk(fresh); } catch { /* không ghi được default cũng vẫn chạy với default */ }
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(ConfigPaths.ConfigFile);
            return JsonSerializer.Deserialize<GatewayConfig>(json, JsonOptions);
        }
        catch
        {
            return null; // file đang bị lock bởi writer / JSON hỏng — caller giữ bản cũ
        }
    }

    private static void WriteToDisk(GatewayConfig config)
    {
        ConfigPaths.EnsureDirectories();
        var json = JsonSerializer.Serialize(config, JsonOptions);
        var tmp = ConfigPaths.ConfigFile + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, ConfigPaths.ConfigFile, overwrite: true);
    }

    private static async Task WriteToDiskAsync(GatewayConfig config, CancellationToken ct)
    {
        ConfigPaths.EnsureDirectories();
        var json = JsonSerializer.Serialize(config, JsonOptions);
        var tmp = ConfigPaths.ConfigFile + ".tmp";
        await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
        File.Move(tmp, ConfigPaths.ConfigFile, overwrite: true);
    }
}
