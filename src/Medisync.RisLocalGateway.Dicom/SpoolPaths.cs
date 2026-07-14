using System.IO;
using Medisync.RisLocalGateway.Core.Configuration;

namespace Medisync.RisLocalGateway.Dicom;

/// <summary>
/// Giải đường dẫn spool (+ dead-letter) từ Dicom.StorageDirectory — DÙNG CHUNG cho SpoolStore
/// (Service ghi) và ConfigApp (re-drive dead-letter đọc). Rỗng StorageDirectory → fallback
/// %ProgramData%\Medisync\RisLocalGateway\spool.
/// </summary>
public static class SpoolPaths
{
    public const string SpoolFolder = "spool";
    public const string FailedFolder = "failed";
    public const string FailedReasonsFolder = "failed-reasons";

    public static string ResolveRoot(string? storageDirectory) =>
        string.IsNullOrWhiteSpace(storageDirectory)
            ? Path.Combine(ConfigPaths.BaseDirectory, SpoolFolder)
            : Path.Combine(storageDirectory, SpoolFolder);

    public static string FailedDir(string? storageDirectory) =>
        Path.Combine(ResolveRoot(storageDirectory), FailedFolder);

    public static string FailedReasonsDir(string? storageDirectory) =>
        Path.Combine(ResolveRoot(storageDirectory), FailedReasonsFolder);
}
