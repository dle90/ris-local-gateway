using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Medisync.RisLocalGateway.Core.Configuration;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public GatewayConfig Load()
    {
        ConfigPaths.EnsureDirectories();
        if (!File.Exists(ConfigPaths.ConfigFile))
        {
            var fresh = GatewayConfig.CreateDefault();
            Save(fresh);
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(ConfigPaths.ConfigFile);
            var cfg = JsonSerializer.Deserialize<GatewayConfig>(json, JsonOptions);
            return cfg ?? GatewayConfig.CreateDefault();
        }
        catch
        {
            return GatewayConfig.CreateDefault();
        }
    }

    public void Save(GatewayConfig config)
    {
        ConfigPaths.EnsureDirectories();
        FileLock.Wait();
        try
        {
            var json = JsonSerializer.Serialize(config, JsonOptions);
            var tmp = ConfigPaths.ConfigFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, ConfigPaths.ConfigFile, overwrite: true);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task SaveAsync(GatewayConfig config, CancellationToken ct = default)
    {
        ConfigPaths.EnsureDirectories();
        await FileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.Serialize(config, JsonOptions);
            var tmp = ConfigPaths.ConfigFile + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            File.Move(tmp, ConfigPaths.ConfigFile, overwrite: true);
        }
        finally
        {
            FileLock.Release();
        }
    }
}
