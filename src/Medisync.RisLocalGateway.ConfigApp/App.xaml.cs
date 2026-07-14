using System.IO;
using System.Windows;
using FellowOakDicom;
using Medisync.RisLocalGateway.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Medisync.RisLocalGateway.ConfigApp;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // fo-dicom native codec — để tính năng re-drive dead-letter transcode (JPEG-LS...) trước
        // khi STOW giống hệt Service. Thiếu bước này thì DicomTranscoder không có codec → forward
        // bản gốc uncompressed (vẫn chạy nhưng nặng hơn).
        new DicomSetupBuilder()
            .RegisterServices(s => s
                .AddFellowOakDicom()
                .AddTranscoderManager<FellowOakDicom.Imaging.NativeCodec.NativeTranscoderManager>())
            .SkipValidation()
            .Build();

        // Logger riêng cho ConfigApp (ghi configapp-*.log cùng thư mục với log của service).
        ConfigPaths.EnsureDirectories();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                path: Path.Combine(ConfigPaths.LogDirectory, "configapp-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
