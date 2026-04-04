using System.Windows;
using Diploma.Core.Interfaces;
using Diploma.Core.Services;
using Diploma.ViewModels;
using Diploma.Views;
using Microsoft.Extensions.DependencyInjection;
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
        
        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<IScreenCaptureService, ScreenCaptureService>();
                services.AddSingleton<ILogService, LogService>();

                services.AddSingleton<MainViewModel>();
                
                services.AddSingleton<MainWindow>();
            })
            .Build();
        
        await _host.StartAsync();
        
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
        
        base.OnStartup(e);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await _host.StopAsync();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}