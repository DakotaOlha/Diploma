using System.Configuration;
using System.Data;
using System.Windows;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Diploma;

public partial class App : Application
{

    private IHost _host;
    
    protected override async void OnStartup(StartupEventArgs e)
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.File("logs/algoreplay.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();
        
        
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await _host.StopAsync();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}