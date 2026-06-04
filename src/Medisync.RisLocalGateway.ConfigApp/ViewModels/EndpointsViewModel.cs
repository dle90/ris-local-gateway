using System;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Medisync.RisLocalGateway.Core.Configuration;

namespace Medisync.RisLocalGateway.ConfigApp.ViewModels;

public partial class EndpointsViewModel : ObservableObject
{
    private readonly ConfigStore _store;

    public EndpointsViewModel(ConfigStore store)
    {
        _store = store;
    }

    [ObservableProperty] private string _signIn = string.Empty;
    [ObservableProperty] private string _healthCheck = string.Empty;
    [ObservableProperty] private string _workList = string.Empty;
    [ObservableProperty] private string _mppsInProgress = string.Empty;
    [ObservableProperty] private string _mppsCompleted = string.Empty;
    [ObservableProperty] private string _mppsDiscontinued = string.Empty;
    [ObservableProperty] private string _instanceMetadata = string.Empty;

    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private Brush _statusColor = Brushes.Black;
    [ObservableProperty] private bool _shouldClose;

    public void Load()
    {
        try
        {
            var cfg = _store.Load();
            var ep = cfg.Ris.Endpoints ?? RisEndpoints.CreateDefault();
            SignIn = ep.SignIn;
            HealthCheck = ep.HealthCheck;
            WorkList = ep.WorkList;
            MppsInProgress = ep.MppsInProgress;
            MppsCompleted = ep.MppsCompleted;
            MppsDiscontinued = ep.MppsDiscontinued;
            InstanceMetadata = ep.InstanceMetadata;
            SetStatus("Đã tải endpoint.", ok: true);
        }
        catch (Exception ex)
        {
            SetStatus("Không tải được: " + ex.Message, ok: false);
        }
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            var cfg = _store.Load();
            cfg.Ris.Endpoints = new RisEndpoints
            {
                SignIn = SignIn?.Trim() ?? string.Empty,
                HealthCheck = HealthCheck?.Trim() ?? string.Empty,
                WorkList = WorkList?.Trim() ?? string.Empty,
                MppsInProgress = MppsInProgress?.Trim() ?? string.Empty,
                MppsCompleted = MppsCompleted?.Trim() ?? string.Empty,
                MppsDiscontinued = MppsDiscontinued?.Trim() ?? string.Empty,
                InstanceMetadata = InstanceMetadata?.Trim() ?? string.Empty,
            };
            _store.Save(cfg);
            SetStatus("Đã lưu endpoint. Service sẽ tự nạp lại.", ok: true);
            ShouldClose = true;
        }
        catch (Exception ex)
        {
            SetStatus("Lưu thất bại: " + ex.Message, ok: false);
        }
    }

    [RelayCommand]
    private void ResetToDefault()
    {
        var def = RisEndpoints.CreateDefault();
        SignIn = def.SignIn;
        HealthCheck = def.HealthCheck;
        WorkList = def.WorkList;
        MppsInProgress = def.MppsInProgress;
        MppsCompleted = def.MppsCompleted;
        MppsDiscontinued = def.MppsDiscontinued;
        InstanceMetadata = def.InstanceMetadata;
        SetStatus("Đã reset về giá trị mặc định (chưa lưu — bấm Lưu để áp dụng).", ok: true);
    }

    [RelayCommand]
    private void Cancel()
    {
        ShouldClose = true;
    }

    private void SetStatus(string message, bool ok)
    {
        Status = message;
        StatusColor = ok ? new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32))
                         : new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
    }
}
