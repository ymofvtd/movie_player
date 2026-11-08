using Microsoft.Extensions.Logging;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Models;
using MyCinema.Core.Playback;

namespace MyCinema.Infrastructure.Services;

/// <summary>Serializes progress writes independently of native callbacks and the WPF lifetime.</summary>
public sealed class WatchHistoryService : IDisposable
{
    private readonly IPlaybackService _playback;
    private readonly ILibraryRepository _library;
    private readonly IProgressRepository _progress;
    private readonly ISettingsService _settings;
    private readonly ILogger<WatchHistoryService> _logger;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private Task _pending = Task.CompletedTask;
    private PlaybackSnapshot? _latest;
    private long _revision = -1;
    private long? _suppressedSession;
    private DateTimeOffset _lastSave;
    private bool _disposed;

    public WatchHistoryService(IPlaybackService playback, ILibraryRepository library, IProgressRepository progress,
        ISettingsService settings, ILogger<WatchHistoryService> logger, TimeProvider? clock = null)
    {
        _playback = playback;
        _library = library;
        _progress = progress;
        _settings = settings;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        playback.Changed += OnPlaybackChanged;
    }

    public event EventHandler? Saved;
    public string? Warning { get; private set; }
    public async Task<ApplicationSettings> LoadSettingsAsync()
    {
        try { return await _settings.LoadAsync().ConfigureAwait(false); }
        catch (Exception error) { ReportFailure(error); return new(); }
    }

    public async Task<PlaybackProgress?> GetProgressAsync(string path)
    {
        await FlushAsync().ConfigureAwait(false);
        try
        {
            var file = await _library.FindFileAsync(path).ConfigureAwait(false);
            return file is null ? null : await _progress.GetAsync(file.Id).ConfigureAwait(false);
        }
        catch (Exception error) { ReportFailure(error); return null; }
    }

    private void OnPlaybackChanged(object? sender, PlaybackChangedEventArgs args)
    {
        lock (_gate)
        {
            var snapshot = args.Snapshot;
            if (_disposed || snapshot.Revision <= _revision) return;
            _revision = snapshot.Revision;
            if (snapshot.SessionId == _suppressedSession) return;
            var previous = _latest;
            if (previous?.SessionId != snapshot.SessionId)
            {
                if (previous is not null) EnqueueSave(previous);
                _lastSave = _clock.GetUtcNow();
            }
            // Stop resets VLC's clock. Persist the last position before that reset.
            if (snapshot.State == PlaybackState.Stopped && previous?.SessionId == snapshot.SessionId)
                snapshot = snapshot with { PositionMs = previous.PositionMs };
            _latest = snapshot;
            if (snapshot.State is PlaybackState.Paused or PlaybackState.Stopped or PlaybackState.Ended or PlaybackState.Error)
            {
                if (previous?.State != snapshot.State || previous.PositionMs != snapshot.PositionMs)
                    EnqueueSave(snapshot);
            }
            else if (snapshot.State == PlaybackState.Playing && _clock.GetUtcNow() - _lastSave >= TimeSpan.FromSeconds(10))
                EnqueueSave(snapshot);
        }
    }

    private void EnqueueSave(PlaybackSnapshot snapshot)
    {
        if (snapshot.FilePath is null || snapshot.DurationMs <= 0 || snapshot.State == PlaybackState.Opening) return;
        _lastSave = _clock.GetUtcNow();
        Enqueue(async () =>
        {
            // Unscanned files remain playable without inserting unrelated library records.
            var file = await _library.FindFileAsync(snapshot.FilePath).ConfigureAwait(false);
            if (file is null) return;
            var existing = await _progress.GetAsync(file.Id).ConfigureAwait(false);
            var now = _clock.GetUtcNow();
            await _progress.SaveAsync(new PlaybackProgress
            {
                MediaFileId = file.Id, PositionMs = PlaybackMath.ClampPosition(snapshot.PositionMs, snapshot.DurationMs),
                DurationMs = snapshot.DurationMs, IsCompleted = snapshot.State == PlaybackState.Ended || existing?.IsCompleted == true,
                LastPlayedUtc = now, UpdatedUtc = now
            }).ConfigureAwait(false);
            Warning = null;
            Saved?.Invoke(this, EventArgs.Empty);
        });
    }

    // Caller holds _gate. Continuations always run on workers, never in a VLC callback.
    private Task Enqueue(Func<Task> operation) => _pending = _pending.ContinueWith(async _ =>
    {
        try { await operation().ConfigureAwait(false); }
        catch (Exception error) { ReportFailure(error); }
    }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();

    public Task FlushAsync()
    {
        lock (_gate)
        {
            if (_latest is { } snapshot) EnqueueSave(snapshot);
            return _pending;
        }
    }

    public Task ResetAsync(long fileId) => EditAsync(fileId, null);
    public Task SetWatchedAsync(long fileId, bool watched) => EditAsync(fileId, watched);

    private async Task EditAsync(long fileId, bool? watched)
    {
        try
        {
            var file = (await _library.GetAllFilesAsync().ConfigureAwait(false)).FirstOrDefault(f => f.Id == fileId);
            Task pending;
            lock (_gate)
            {
                if (_latest is { } snapshot && string.Equals(snapshot.FilePath, file?.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    EnqueueSave(snapshot);
                    // Manual edits win over callbacks from the old paused session.
                    _suppressedSession = snapshot.SessionId;
                    _latest = null;
                }
                pending = Enqueue(async () =>
                {
                    if (watched is null) await _progress.ResetAsync(fileId).ConfigureAwait(false);
                    else
                    {
                        var existing = await _progress.GetAsync(fileId).ConfigureAwait(false);
                        var duration = existing?.DurationMs ?? file?.DurationMs ?? 0;
                        var now = _clock.GetUtcNow();
                        await _progress.SaveAsync(new PlaybackProgress
                        {
                            MediaFileId = fileId, DurationMs = duration,
                            PositionMs = watched.Value ? duration : existing?.IsCompleted == false ? existing.PositionMs : 0,
                            IsCompleted = watched.Value, LastPlayedUtc = existing?.LastPlayedUtc ?? now, UpdatedUtc = now
                        }).ConfigureAwait(false);
                    }
                    Warning = null;
                    Saved?.Invoke(this, EventArgs.Empty);
                });
            }
            await pending.ConfigureAwait(false);
        }
        catch (Exception error) { ReportFailure(error); }
    }

    private void ReportFailure(Exception error)
    {
        _logger.LogError(error, "Could not read or save watch progress");
        Warning = "Watch progress could not be saved or read. Check the library drive and try again.";
        Saved?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; }
        _playback.Changed -= OnPlaybackChanged;
        // MainWindow flushes before stopping; queued work has no dependency on the UI dispatcher.
        FlushAsync().GetAwaiter().GetResult();
    }
}
