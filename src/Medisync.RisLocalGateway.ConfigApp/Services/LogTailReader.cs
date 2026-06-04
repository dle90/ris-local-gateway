using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Medisync.RisLocalGateway.ConfigApp.Services;

/// <summary>
/// Tail Serilog rolling log file. Theo dõi file mới nhất trong log directory,
/// đọc các dòng append từ lần đọc trước.
/// </summary>
public sealed class LogTailReader
{
    private readonly string _logDirectory;
    private string? _currentFile;
    private long _lastPosition;

    public LogTailReader(string logDirectory)
    {
        _logDirectory = logDirectory;
    }

    /// <summary>
    /// Đọc dòng mới từ log file. Lần đầu trả về tail 200 dòng cuối.
    /// </summary>
    public IReadOnlyList<string> ReadNew(int initialTailLines = 200)
    {
        if (!Directory.Exists(_logDirectory)) return Array.Empty<string>();

        var latest = Directory.GetFiles(_logDirectory, "gateway-*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (latest is null) return Array.Empty<string>();

        // File mới (rolling sang ngày mới hoặc lần đọc đầu)
        if (!string.Equals(latest, _currentFile, StringComparison.OrdinalIgnoreCase))
        {
            _currentFile = latest;
            return InitialTail(latest, initialTailLines);
        }

        return ReadAppendedLines(latest);
    }

    public string? CurrentFilePath => _currentFile;

    public void Reset()
    {
        _currentFile = null;
        _lastPosition = 0;
    }

    private IReadOnlyList<string> InitialTail(string file, int lines)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            var all = new List<string>();
            string? line;
            while ((line = sr.ReadLine()) != null) all.Add(line);
            _lastPosition = fs.Position;
            return all.Count <= lines ? all : all.Skip(all.Count - lines).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private IReadOnlyList<string> ReadAppendedLines(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _lastPosition)
            {
                // File bị truncate (hiếm) — reset
                _lastPosition = 0;
            }
            fs.Seek(_lastPosition, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            var lines = new List<string>();
            string? line;
            while ((line = sr.ReadLine()) != null) lines.Add(line);
            _lastPosition = fs.Position;
            return lines;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
