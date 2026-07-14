using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Medisync.RisLocalGateway.ConfigApp.Services;
using Medisync.RisLocalGateway.Core.Configuration;

namespace Medisync.RisLocalGateway.ConfigApp.ViewModels;

public partial class LogViewerViewModel : ObservableObject
{
    private const int MaxBufferedLines = 2000;

    private readonly LogTailReader _tailReader;

    public LogViewerViewModel()
    {
        _tailReader = new LogTailReader(ConfigPaths.LogDirectory);
    }

    public ObservableCollection<string> Lines { get; } = new();

    [ObservableProperty] private string _logDirectoryPath = ConfigPaths.LogDirectory;
    [ObservableProperty] private string _currentFileText = "—";
    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private bool _showInfo = true;
    [ObservableProperty] private bool _showWarning = true;
    [ObservableProperty] private bool _showError = true;
    [ObservableProperty] private bool _autoScroll = true;

    // Nguồn log: true = Service (gateway-*.log), false = ConfigApp (configapp-*.log —
    // chứa log của tính năng đẩy lại dead-letter chạy trong tiến trình ConfigApp).
    [ObservableProperty] private bool _showServiceLog = true;

    partial void OnShowServiceLogChanged(bool value)
    {
        Lines.Clear();
        _tailReader.SetPattern(value ? "gateway-*.log" : "configapp-*.log");
        PollNewLines();
    }

    public event EventHandler? LinesAppended;

    public void PollNewLines()
    {
        var newLines = _tailReader.ReadNew();
        if (newLines.Count == 0) return;

        CurrentFileText = _tailReader.CurrentFilePath ?? "—";

        foreach (var line in newLines)
        {
            if (!PassesFilter(line)) continue;
            Lines.Add(line);
        }

        // Trim buffer
        while (Lines.Count > MaxBufferedLines) Lines.RemoveAt(0);

        LinesAppended?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Clear() => Lines.Clear();

    [RelayCommand]
    private void OpenLogFolder()
    {
        if (!Directory.Exists(ConfigPaths.LogDirectory))
        {
            Directory.CreateDirectory(ConfigPaths.LogDirectory);
        }
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = ConfigPaths.LogDirectory,
            UseShellExecute = true,
        });
    }

    [RelayCommand]
    private void Reload()
    {
        Lines.Clear();
        _tailReader.Reset();
        PollNewLines();
    }

    private bool PassesFilter(string line)
    {
        // Level filter (Serilog [INF] [WRN] [ERR] [DBG])
        bool isInfo = line.Contains("[INF]");
        bool isWarn = line.Contains("[WRN]");
        bool isError = line.Contains("[ERR]") || line.Contains("[FTL]");

        if (isError && !ShowError) return false;
        if (isWarn && !ShowWarning && !isError) return false;
        if (isInfo && !ShowInfo && !isWarn && !isError) return false;

        // Text filter
        if (!string.IsNullOrWhiteSpace(FilterText) &&
            line.IndexOf(FilterText, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        return true;
    }
}
