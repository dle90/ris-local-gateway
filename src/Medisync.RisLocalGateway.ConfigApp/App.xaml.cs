using System.IO;
using System.Windows;
using Medisync.RisLocalGateway.Core.Configuration;
using Serilog;

namespace Medisync.RisLocalGateway.ConfigApp;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
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
