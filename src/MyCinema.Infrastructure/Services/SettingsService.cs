using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Models;

namespace MyCinema.Infrastructure.Services;

public sealed class SettingsService(ISettingsRepository repository, ILogger<SettingsService> logger) : ISettingsService
{
    public const string SettingsKey = "ApplicationSettings";
    private static readonly JsonSerializerOptions JsonOptions = new() { IgnoreReadOnlyProperties = true };

    public async Task<ApplicationSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var json = await repository.GetAsync(SettingsKey, cancellationToken).ConfigureAwait(false);
        if (json is not null)
        {
            try
            {
                var settings = JsonSerializer.Deserialize<ApplicationSettings>(json);
                if (settings?.IsValid == true) return settings;
                logger.LogWarning("Stored application settings are invalid; restoring defaults");
            }
            catch (JsonException error)
            {
                logger.LogWarning(error, "Stored application settings are malformed; restoring defaults");
            }
        }
        var defaults = new ApplicationSettings();
        await SaveAsync(defaults, cancellationToken).ConfigureAwait(false);
        return defaults;
    }

    public Task SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsValid) throw new ArgumentException("The application settings contain unsupported values.", nameof(settings));
        return repository.SetAsync(SettingsKey, JsonSerializer.Serialize(settings, JsonOptions), cancellationToken);
    }
}
