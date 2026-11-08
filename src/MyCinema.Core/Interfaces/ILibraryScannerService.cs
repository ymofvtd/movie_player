using MyCinema.Core.Scanning;

namespace MyCinema.Core.Interfaces;

public interface ILibraryScannerService
{
    Task ConfigureDefaultRootAsync(CancellationToken cancellationToken = default);
    Task<ScanResult> ScanAsync(IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IScanRepository
{
    // One transaction per root. Cancellation/failure cannot expose a partial hierarchy.
    Task CommitAsync(long rootId, RootScan scan, CancellationToken cancellationToken = default);
}
