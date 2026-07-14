using System;
using System.IO;

namespace Medisync.RisLocalGateway.Core.Configuration;

public static class ConfigPaths
{
    private const string CompanyFolder = "Medisync";
    private const string ProductFolder = "RisLocalGateway";

    public static string BaseDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            CompanyFolder,
            ProductFolder);

    public static string ConfigFile => Path.Combine(BaseDirectory, "config.json");

    public static string LogDirectory => Path.Combine(BaseDirectory, "logs");

    /// <summary>Thư mục chứa DB thống kê study local (SQLite) — cố định, không theo StorageDirectory.</summary>
    public static string StatsDirectory => Path.Combine(BaseDirectory, "stats");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(BaseDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
