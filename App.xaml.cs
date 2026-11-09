using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyCinema.Core.Interfaces;
using Person_Movie_Player.Services;
using Person_Movie_Player.ViewModels;

namespace Person_Movie_Player;

public partial class App : Application
{
    private ServiceProvider? _services;
    private ILogger<App>? _logger;
    private SingleInstanceApplication? _singleInstance;
    private bool _activationRequested;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        try
        {
            _singleInstance = new SingleInstanceApplication();
            if (!_singleInstance.IsPrimaryInstance)
            {
                if (!await _singleInstance.ActivatePrimaryAsync())
                    MessageBox.Show("MyCinema is already running. Open its existing window from the taskbar.", "MyCinema",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
            _singleInstance.ListenForActivation(() => Dispatcher.BeginInvoke(ActivateMainWindow));
            _services = new ServiceCollection().AddMyCinema().BuildServiceProvider(
                new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
            _logger = _services.GetRequiredService<ILogger<App>>();
            _logger.LogInformation("MyCinema starting. Version {Version}; 64-bit process: {Is64Bit}",
                typeof(App).Assembly.GetName().Version, Environment.Is64BitProcess);
            await _services.GetRequiredService<IDatabaseService>().InitializeAsync();
            await _services.GetRequiredService<ISettingsService>().LoadAsync();
            await _services.GetRequiredService<SettingsViewModel>().InitializeAsync();
            await _services.GetRequiredService<LibraryStore>().RefreshAsync();
            await _services.GetRequiredService<EpisodeSidebarViewModel>().InitializeAsync();
            MainWindow = _services.GetRequiredService<MainWindow>();
            MainWindow.Show();
            if (_activationRequested) ActivateMainWindow();
        }
        catch (Exception error)
        {
            _logger?.LogCritical(error, "Application startup failed");
            MessageBox.Show($"MyCinema could not start.\n\n{error.Message}", "MyCinema",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        _logger?.LogInformation("MyCinema shutting down. Exit code: {ExitCode}", e.ApplicationExitCode);
        try { _services?.Dispose(); }
        finally { _singleInstance?.Dispose(); }
        base.OnExit(e);
    }

    private void ActivateMainWindow()
    {
        if (Dispatcher.HasShutdownStarted) return;
        _activationRequested = true;
        if (MainWindow is not { } window) return;
        _activationRequested = false;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        if (!window.IsVisible) window.Show();
        window.Activate();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogCritical(e.Exception, "Unhandled user interface error");
        e.Handled = true;
        MessageBox.Show("MyCinema encountered an unexpected error and needs to close.\n\n" +
            "Details are saved under %LOCALAPPDATA%\\MyCinema\\Logs.", "MyCinema",
            MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(1);
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        _logger?.LogCritical(e.ExceptionObject as Exception, "Unhandled application error; terminating: {Terminating}",
            e.IsTerminating);
}
