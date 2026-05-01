using System.IO;
using System.Windows;
using Diploma.Core.Interfaces;
using Diploma.Core.Services;
using Diploma.Data.Database;
using Diploma.Helpers;
using Diploma.ViewModels;
using Diploma.Views;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Diploma;

public partial class App : Application
{
    private IHost _host = null!;
    private TaskbarIcon? _trayIcon;

    protected override async void OnStartup(StartupEventArgs e)
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.File("logs/algoreplay.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((_, services) =>
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var dbPath  = Path.Combine(appData, "AlgoReplay", "data.db");
                var connStr = $"Data Source={dbPath}";

                var dbInit = new DatabaseInitializer(connStr);
                dbInit.Initialize();

                services.AddSingleton<ILogService>(_ => new LogService(connStr));
                services.AddSingleton<IInputMonitorService, InputMonitorService>();
                services.AddSingleton<IAudioCaptureService, AudioCaptureService>();
                services.AddSingleton<IScreenCaptureService, ScreenCaptureService>();
                services.AddSingleton<IGlobalHotkeyService, GlobalHotkeyService>();
                services.AddSingleton<ModeProfileService>();
                services.AddSingleton<DiskSpaceService>();
                services.AddSingleton<MediaMergeService>();
                services.AddSingleton<ExportService>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<SessionsViewModel>();
                services.AddSingleton<SessionsView>();
                services.AddSingleton<PlayerViewModel>();
                services.AddSingleton<PlayerView>();
                services.AddSingleton<OverlayWindow>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();

        _trayIcon = (TaskbarIcon)FindResource("TrayIcon");

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var splash = new SplashWindow();
        splash.Show();
        try
        {
            await Task.Run(HardwareEncoderDetector.Detect);
            splash.SetStatus("Ready.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HardwareEncoderDetector warm-up failed; will retry on first record");
            splash.SetStatus("Encoder probe failed — software fallback will be used.");
            await Task.Delay(1_500);
        }
        finally
        {
            splash.Close();
        }

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();

        var hotkeyService = _host.Services.GetRequiredService<IGlobalHotkeyService>();
        hotkeyService.Start();

        base.OnStartup(e);
    }

    private void TrayIcon_DoubleClick(object sender, RoutedEventArgs e) => ShowMainWindow();

    private void TrayOpen_Click(object sender, RoutedEventArgs e) => ShowMainWindow();

    private void TrayStartStop_Click(object sender, RoutedEventArgs e)
    {
        var vm = _host.Services.GetRequiredService<MainViewModel>();
        if (vm.IsRecording)
            _ = vm.StopRecordingCommand.ExecuteAsync(null);
        else
            _ = vm.StartRecordingCommand.ExecuteAsync(null);
    }

    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        _trayIcon?.Dispose();
        Shutdown();
    }

    private void ShowMainWindow()
    {
        var win = _host.Services.GetRequiredService<MainWindow>();
        win.Show();
        if (win.WindowState == System.Windows.WindowState.Minimized)
            win.WindowState = System.Windows.WindowState.Normal;
        win.Activate();
    }

    public OverlayWindow GetOverlay() =>
        _host.Services.GetRequiredService<OverlayWindow>();

    protected override async void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();

        var player = _host.Services.GetRequiredService<PlayerViewModel>();
        player.Dispose();

        await _host.StopAsync();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}