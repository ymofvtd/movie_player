using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Windows.Threading;
using MyCinema.Core.Interfaces;
using MyCinema.Infrastructure.Database;
using MyCinema.Infrastructure.Logging;
using MyCinema.Infrastructure.Repositories;
using MyCinema.Infrastructure.Services;
using Person_Movie_Player.ViewModels;

namespace Person_Movie_Player.Services;

public static class ServiceRegistration
{
    public static IServiceCollection AddMyCinema(this IServiceCollection services)
    {
        services.AddSingleton<IApplicationPaths, ApplicationPaths>();
        services.AddSingleton<ILoggerProvider>(provider =>
            new FileLoggerProvider(provider.GetRequiredService<IApplicationPaths>().LogsDirectory));
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Information));
        services.AddSingleton<SqliteDatabase>();
        services.AddSingleton<IDatabaseService>(provider => provider.GetRequiredService<SqliteDatabase>());
        services.AddSingleton<ILibraryRepository, LibraryRepository>();
        services.AddSingleton<ILibraryOrganizationService, LibraryOrganizationService>();
        services.AddSingleton<PosterService>();
        services.AddTransient<OrganizationEditorViewModel>();
        services.AddSingleton<IScanRepository, ScanRepository>();
        services.AddSingleton<ILibraryScannerService, LibraryScannerService>();
        services.AddSingleton<IProgressRepository, ProgressRepository>();
        services.AddSingleton<ISettingsRepository, SettingsRepository>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<PlaybackService>();
        services.AddSingleton<IPlaybackService>(provider => provider.GetRequiredService<PlaybackService>());
        services.AddSingleton<WatchHistoryService>();
        services.AddSingleton<IFileDialogService, FileDialogService>();
        services.AddSingleton<IUiDispatcher>(_ => new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        services.AddSingleton<PlayerViewModel>();
        services.AddSingleton<EpisodeSidebarViewModel>();
        services.AddSingleton<LibraryStore>();
        services.AddSingleton<MediaDetailsViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        return services;
    }
}
