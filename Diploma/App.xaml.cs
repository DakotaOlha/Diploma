using System.IO;
using System.Windows;
using Diploma.Core.Interfaces;
using Diploma.Core.Services;
using Diploma.Data.Database;
using Diploma.ViewModels;
using Diploma.Views;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Diploma;

public partial class App : Application
{
    private IHost        _host     = null!;
    private TaskbarIcon? _trayIcon;

    private int _shutdownGuard;
    
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
                services.AddSingleton<ISettingsService>(_ => new AppSettingsService(connStr));
                services.AddSingleton<IInputMonitorService, InputMonitorService>();
                services.AddSingleton<IAudioCaptureService,  AudioCaptureService>();
                services.AddSingleton<IScreenCaptureService, ScreenCaptureService>();
                services.AddSingleton<IGlobalHotkeyService,  GlobalHotkeyService>();
                services.AddSingleton<ModeProfileService>();
                services.AddSingleton<DiskSpaceService>();
                services.AddSingleton<MediaMergeService>();
                services.AddSingleton<ExportService>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<SessionsViewModel>();
                services.AddSingleton<SessionsView>();
                services.AddSingleton<PlayerViewModel>();
                services.AddSingleton<PlayerView>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<SettingsView>();
                services.AddSingleton<SettingsWindow>();
                services.AddSingleton<OverlayWindow>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();

        var settingsService = _host.Services.GetRequiredService<ISettingsService>();
        await settingsService.LoadAsync();
        settingsService.ApplyToProfileService(
            _host.Services.GetRequiredService<ModeProfileService>());

        _trayIcon = BuildTrayIcon();

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
        // Main window opens on demand (Sessions / Settings buttons in overlay)

        var overlay = _host.Services.GetRequiredService<OverlayWindow>();
        overlay.Show();

        var hotkeyService = _host.Services.GetRequiredService<IGlobalHotkeyService>();
        hotkeyService.Start();

        base.OnStartup(e);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (Interlocked.Exchange(ref _shutdownGuard, 1) == 1)
        {
            base.OnExit(e);
            return;
        }

        try
        {
            var hotkeyService = _host.Services.GetRequiredService<IGlobalHotkeyService>();
            hotkeyService.Stop();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to stop hotkey service");
        }

        try
        {
            var vm = _host.Services.GetRequiredService<MainViewModel>();
            if (vm.IsRecording)
            {
                Log.Information("OnExit: recording still active — stopping pipeline…");

                await vm.StopRecordingCommand.ExecuteAsync(null);

                Log.Information("OnExit: recording pipeline finished.");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "OnExit: error while stopping recording");
        }

        try
        {
            var player = _host.Services.GetRequiredService<PlayerViewModel>();
            player.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "OnExit: error while disposing PlayerViewModel");
        }

        try
        {
            if (_host.Services.GetRequiredService<ILogService>() is IAsyncDisposable logService)
                await logService.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "OnExit: error while disposing LogService");
        }

        await _host.StopAsync();

        _trayIcon?.Dispose();

        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private TaskbarIcon BuildTrayIcon()
    {
        var openItem = new System.Windows.Controls.MenuItem
        {
            Header     = "Відкрити",
            FontWeight = FontWeights.SemiBold,
        };
        openItem.Click += (_, _) => ShowMainWindow();

        var startStopItem = new System.Windows.Controls.MenuItem
        {
            Header = "Старт / Стоп запису",
        };
        startStopItem.Click += (_, _) =>
        {
            var vm = _host.Services.GetRequiredService<MainViewModel>();
            if (vm.IsRecording)
                _ = vm.StopRecordingCommand.ExecuteAsync(null);
            else
                _ = vm.StartRecordingCommand.ExecuteAsync(null);
        };

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Вийти" };
        exitItem.Click += (_, _) => RequestShutdown();

        var menu = new System.Windows.Controls.ContextMenu();
        menu.Items.Add(openItem);
        menu.Items.Add(startStopItem);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(exitItem);

        var icon = new TaskbarIcon
        {
            IconSource  = new System.Windows.Media.Imaging.BitmapImage(
                              new Uri("pack://application:,,,/Assets/tray.ico")),
            ToolTipText = "AlgoReplay",
            ContextMenu = menu,
        };
        icon.TrayMouseDoubleClick += (_, _) => ShowMainWindow();

        return icon;
    }

    private async void RequestShutdown()
    {
        var vm = _host.Services.GetRequiredService<MainViewModel>();

        if (vm.IsRecording)
        {
            ShowMainWindow();

            var result = MessageBox.Show(
                MainWindow,
                "Запис ще триває. Зупинити запис і вийти з програми?",
                "AlgoReplay",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (result != MessageBoxResult.Yes)
                return;

            _trayIcon!.ContextMenu.IsEnabled = false;
            try
            {
                await vm.StopRecordingCommand.ExecuteAsync(null);
            }
            finally
            {
                _trayIcon.ContextMenu.IsEnabled = true;
            }
        }

        Shutdown();
    }
    
    private void ShowMainWindow()
    {
        var win = _host.Services.GetRequiredService<MainWindow>();
        win.Show();
        if (win.WindowState == WindowState.Minimized)
            win.WindowState = WindowState.Normal;
        win.Activate();
    }

    public OverlayWindow   GetOverlay()        => _host.Services.GetRequiredService<OverlayWindow>();
    public SettingsWindow  GetSettingsWindow() => _host.Services.GetRequiredService<SettingsWindow>();
}