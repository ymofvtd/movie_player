using MyCinema.Core.Models;

namespace MyCinema.Core.Interfaces;

public interface ISettingsService
{
    Task<ApplicationSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken = default);
}
