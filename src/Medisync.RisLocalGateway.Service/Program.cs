using System;
using System.IO;
using System.Net.Http;
using FellowOakDicom;
using Medisync.RisLocalGateway.Core.Configuration;
using Medisync.RisLocalGateway.Core.Ris;
using Medisync.RisLocalGateway.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

ConfigPaths.EnsureDirectories();

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    // fo-dicom tự log "Connection closed" ở mức Information cho MỖI kết nối TCP đóng (kể cả
    // health-probe mỗi 5s từ ConfigApp/DVT) — bỏ qua đúng message này để khỏi spam. Vẫn giữ:
    // mở association ("Association request from …") và lỗi ("Connection closed with error").
    .Filter.ByExcluding(le => le.MessageTemplate.Text == "Connection closed")
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: Path.Combine(ConfigPaths.LogDirectory, "gateway-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

Log.Information("=== Service bootstrap starting === LogDir={LogDir}", ConfigPaths.LogDirectory);

// fo-dicom có DI container riêng. Phải register logging riêng để
// Logger trong DicomService (provider) route qua Serilog của ứng dụng.
new DicomSetupBuilder()
    .RegisterServices(s => s
        .AddFellowOakDicom()
        // Native codecs (fo-dicom.Codecs) cho transcode JPEG 2000 trước khi STOW.
        // Thiếu dòng này thì DicomTranscoder không có codec → ném lỗi lúc transcode.
        .AddTranscoderManager<FellowOakDicom.Imaging.NativeCodec.NativeTranscoderManager>()
        .AddLogging(b =>
        {
            b.ClearProviders();
            b.AddSerilog(Log.Logger, dispose: false);
        }))
    .SkipValidation()
    .Build();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Logging.ClearProviders();
    builder.Logging.AddSerilog(Log.Logger, dispose: true);

    builder.Services.AddSingleton<ConfigStore>();
    builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
    builder.Services.AddSingleton<IRisClient, HttpRisClient>();
    builder.Services.AddSingleton<Medisync.RisLocalGateway.Dicom.IPacsStowClient,
                                  Medisync.RisLocalGateway.Dicom.PacsStowClient>();
    builder.Services.AddSingleton<Medisync.RisLocalGateway.Dicom.SpoolStore>();
    builder.Services.AddSingleton<Medisync.RisLocalGateway.Dicom.DicomServerHost>();
    builder.Services.AddHostedService<GatewayWorker>();
    builder.Services.AddHostedService<SpoolForwarder>();
    builder.Services.AddWindowsService(o => o.ServiceName = "Medisync RIS Local Gateway");

    var host = builder.Build();

    // Set static reference cho Provider (fo-dicom 5.x không cho inject qua ctor)
    Medisync.RisLocalGateway.Dicom.RisGatewayDicomProvider.RisClient =
        host.Services.GetRequiredService<IRisClient>();
    Medisync.RisLocalGateway.Dicom.RisGatewayDicomProvider.Spool =
        host.Services.GetRequiredService<Medisync.RisLocalGateway.Dicom.SpoolStore>();

    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Service terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
