using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Medisync.RisLocalGateway.Core.Configuration;

namespace Medisync.RisLocalGateway.ConfigApp.ViewModels;

public partial class AboutViewModel : ObservableObject
{
    [ObservableProperty] private string _productName = "RIS Local Gateway";
    [ObservableProperty] private string _version = ReadVersion();
    [ObservableProperty] private string _company = "Medisync";

    [ObservableProperty] private string _configPath = ConfigPaths.ConfigFile;
    [ObservableProperty] private string _logsPath = ConfigPaths.LogDirectory;
    [ObservableProperty] private string _osVersion = RuntimeInformation.OSDescription;
    [ObservableProperty] private string _dotnetVersion = RuntimeInformation.FrameworkDescription;

    [ObservableProperty] private string _exportMessage = string.Empty;
    [ObservableProperty] private Brush _exportMessageColor = Brushes.Black;

    [RelayCommand]
    private void OpenConfigFolder()
    {
        if (!Directory.Exists(ConfigPaths.BaseDirectory))
        {
            Directory.CreateDirectory(ConfigPaths.BaseDirectory);
        }
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = ConfigPaths.BaseDirectory,
            UseShellExecute = true,
        });
    }

    [RelayCommand]
    private void ExportDiagnosticBundle()
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var bundlePath = Path.Combine(desktop, $"gateway-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

            using (var zip = ZipFile.Open(bundlePath, ZipArchiveMode.Create))
            {
                // Config (mật khẩu đã encrypted nên OK để export)
                if (File.Exists(ConfigPaths.ConfigFile))
                {
                    zip.CreateEntryFromFile(ConfigPaths.ConfigFile, "config.json");
                }
                // 7 file log gần nhất
                if (Directory.Exists(ConfigPaths.LogDirectory))
                {
                    var logs = Directory.GetFiles(ConfigPaths.LogDirectory, "gateway-*.log");
                    Array.Sort(logs, (a, b) => File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)));
                    for (int i = 0; i < Math.Min(7, logs.Length); i++)
                    {
                        zip.CreateEntryFromFile(logs[i], $"logs/{Path.GetFileName(logs[i])}");
                    }
                }
                // System info
                var sysInfo = $"Product: {ProductName} {Version}\n" +
                              $"OS: {OsVersion}\n" +
                              $".NET: {DotnetVersion}\n" +
                              $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n";
                var entry = zip.CreateEntry("system-info.txt");
                using var es = entry.Open();
                using var sw = new StreamWriter(es);
                sw.Write(sysInfo);
            }

            ExportMessage = $"Đã xuất: {bundlePath}";
            ExportMessageColor = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));

            // Mở Explorer chọn file vừa tạo
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{bundlePath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ExportMessage = "Lỗi: " + ex.Message;
            ExportMessageColor = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
        }
    }

    private static string ReadVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetName().Version?.ToString() ?? "0.1.0";
    }
}
