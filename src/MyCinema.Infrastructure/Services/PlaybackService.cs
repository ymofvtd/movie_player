using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using Microsoft.Extensions.Logging;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Playback;
using System.Globalization;

namespace MyCinema.Infrastructure.Services;

/// <summary>Owns a single VLC engine/player. Native callbacks only publish immutable state.</summary>
public sealed class PlaybackService : IPlaybackService
{
    private readonly ILogger<PlaybackService> _logger;
    private readonly string[] _engineOptions;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly object _stateLock = new();
    private PlaybackSnapshot _current = new();
    private LibVLC? _engine;
    private string? _lastNativeError;
    private volatile bool _acceptEvents;
    private volatile bool _disposed;

    public PlaybackService(ILogger<PlaybackService> logger) : this(logger, []) { }

    public PlaybackService(ILogger<PlaybackService> logger, string[] engineOptions)
    {
        _logger = logger;
        _engineOptions = engineOptions;
    }

    // Only the WPF view adapter consumes this handle; Core and the player ViewModel remain engine-independent.
    public MediaPlayer? MediaPlayer { get; private set; }
    public PlaybackSnapshot Current { get { lock (_stateLock) return _current; } }
    public event EventHandler<PlaybackChangedEventArgs>? Changed;

    public Task InitializeAsync() => ExecuteAsync(() =>
    {
        if (MediaPlayer is not null) return;
        LibVLCSharp.Shared.Core.Initialize();
        _engine = new LibVLC(new[] { "--no-video-title-show", "--no-snapshot-preview" }.Concat(_engineOptions).ToArray());
        _engine.Log += OnNativeLog;
        try
        {
            MediaPlayer = new MediaPlayer(_engine) { EnableKeyInput = false, EnableMouseInput = false };
            Subscribe(MediaPlayer);
            Update(state => state with { ErrorMessage = null });
            _logger.LogInformation("LibVLC initialized: {Version}", _engine.Version);
        }
        catch
        {
            _engine.Log -= OnNativeLog;
            _engine.Dispose();
            _engine = null;
            throw;
        }
    }, "The video engine could not start. Check the packaged VLC runtime.");

    public async Task OpenAsync(string filePath, long startPositionMs = 0)
    {
        if (MediaPlayer is null) await InitializeAsync().ConfigureAwait(false);
        await ExecuteAsync(() =>
        {
            if (MediaPlayer is null || _engine is null) return;
            if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathFullyQualified(filePath) || !File.Exists(filePath))
            {
                ReportError("The selected file is unavailable. Reconnect its drive or rescan the library in Settings.",
                    preservePlayback: true, details: filePath);
                return;
            }

            var fullPath = Path.GetFullPath(filePath);
            _acceptEvents = false;
            MediaPlayer.Stop(); // Never called from a native callback. Stop joins the old session's callbacks.
            Volatile.Write(ref _lastNativeError, null);
            Update(state => state with
            {
                State = PlaybackState.Opening, FilePath = fullPath, PositionMs = 0,
                DurationMs = 0, CanSeek = false, ErrorMessage = null, ErrorDetails = null, SessionId = state.SessionId + 1,
                AudioTracks = [], SubtitleTracks = [], AudioTrackId = -1, SubtitleTrackId = -1
            });
            using var media = new Media(_engine, fullPath, FromType.FromPath);
            if (startPositionMs > 0)
                media.AddOption(":start-time=" + (startPositionMs / 1000d).ToString("0.###", CultureInfo.InvariantCulture));
            _acceptEvents = true;
            if (!MediaPlayer.Play(media))
            {
                ReportError("VLC could not open this video. Try another local file.");
                return;
            }
            ApplyAudio();
            _logger.LogInformation("Opened local media {FilePath}", fullPath);
        }, "This video could not be opened. Check that the file is readable.").ConfigureAwait(false);
    }

    public Task PlayAsync() => ExecuteAsync(() =>
    {
        if (MediaPlayer is null || !Current.HasMedia) return;
        if (Current.State is PlaybackState.Stopped or PlaybackState.Ended or PlaybackState.Error)
        {
            using var media = MediaPlayer.Media;
            media?.AddOption(":start-time=0");
        }
        if (Current.State is PlaybackState.Ended or PlaybackState.Error)
        {
            _acceptEvents = false;
            MediaPlayer.Stop();
            Update(state => state with { PositionMs = 0, CanSeek = false, ErrorMessage = null });
        }
        _acceptEvents = true;
        Update(state => state with { State = PlaybackState.Opening, ErrorMessage = null, ErrorDetails = null });
        if (!MediaPlayer.Play()) ReportError("Playback could not start. Try opening the file again.");
        ApplyAudio();
    }, "Playback could not start.");

    public void Pause()
    {
        if (!_disposed && Current.State is PlaybackState.Playing or PlaybackState.Opening) MediaPlayer?.SetPause(true);
    }

    public Task StopAsync() => ExecuteAsync(() =>
    {
        if (MediaPlayer is null) return;
        _acceptEvents = false;
        MediaPlayer.Stop();
        Update(state => state with
        {
            State = state.HasMedia ? PlaybackState.Stopped : PlaybackState.Empty,
            PositionMs = 0, CanSeek = false, ErrorMessage = null
        });
    }, "Playback could not be stopped.");

    public void Seek(long positionMs)
    {
        var state = Current;
        if (_disposed || MediaPlayer is null || !state.CanSeek || state.DurationMs <= 0) return;
        var position = PlaybackMath.ClampPosition(positionMs, state.DurationMs);
        MediaPlayer.Time = position;
        Update(current => current with { PositionMs = position });
    }

    public void SetVolume(int volume)
    {
        if (_disposed) return;
        var value = Math.Clamp(volume, 0, 100);
        if (MediaPlayer is not null) MediaPlayer.Volume = value;
        Update(state => state with { Volume = value });
    }

    public void SetMute(bool muted)
    {
        if (_disposed) return;
        if (MediaPlayer is not null) MediaPlayer.Mute = muted;
        Update(state => state with { IsMuted = muted });
    }

    public Task SetSpeedAsync(double speed) => ExecuteAsync(() =>
    {
        if (!PlaybackOptions.Speeds.Contains(speed)) return;
        if (CanSelectTracks && MediaPlayer!.SetRate((float)speed) != 0)
        {
            ReportError("This file does not support the selected playback speed. Try normal speed (1×).", preservePlayback: true);
            return;
        }
        Update(s => s with { Speed = speed });
    }, "Playback speed could not be changed. Try normal speed (1×).", preservePlayback: true);

    public Task SetAspectRatioAsync(VideoAspectRatio aspectRatio) => ExecuteAsync(() =>
    {
        if (!Enum.IsDefined(aspectRatio)) return;
        ApplyAspectRatio(aspectRatio);
        Update(s => s with { AspectRatio = aspectRatio });
    }, "The picture shape could not be changed. Try Original aspect ratio.", preservePlayback: true);

    public void DismissError() => Update(s => s with { ErrorMessage = null, ErrorDetails = null });

    private void ApplyAspectRatio(VideoAspectRatio aspectRatio)
    {
        if (MediaPlayer is null) return;
        MediaPlayer.Scale = 0; // Fit the drawable; VLC preserves the selected ratio with letterboxing.
        MediaPlayer.AspectRatio = aspectRatio switch
        {
            VideoAspectRatio.Wide16By9 => "16:9",
            VideoAspectRatio.Standard4By3 => "4:3",
            VideoAspectRatio.Cinema235By1 => "235:100",
            _ => null
        };
    }

    public Task SetAudioTrackAsync(int trackId) => ExecuteAsync(() =>
    {
        if (!CanSelectTracks || !Current.AudioTracks.Any(t => t.Id == trackId)) return;
        if (!MediaPlayer!.SetAudioTrack(trackId))
            ReportError("This audio track could not be selected. Try another track.", preservePlayback: true);
        else Update(s => s with { AudioTrackId = trackId, ErrorMessage = null });
        RefreshTracks();
    }, "The audio track could not be changed.", preservePlayback: true);

    public Task SetSubtitleTrackAsync(int trackId) => ExecuteAsync(() =>
    {
        if (!CanSelectTracks || !Current.SubtitleTracks.Any(t => t.Id == trackId)) return;
        if (!MediaPlayer!.SetSpu(trackId))
            ReportError("This subtitle track could not be selected. Try another track.", preservePlayback: true);
        else Update(s => s with { SubtitleTrackId = trackId, ErrorMessage = null });
        RefreshTracks();
    }, "The subtitle track could not be changed.", preservePlayback: true);

    public Task LoadSubtitleAsync(string filePath) => ExecuteAsync(() =>
    {
        if (!CanSelectTracks) return;
        if (!Path.IsPathFullyQualified(filePath) || !File.Exists(filePath))
        {
            ReportError("The subtitle file is unavailable. Choose an existing local subtitle file.", preservePlayback: true);
            return;
        }
        if (!MediaPlayer!.AddSlave(MediaSlaveType.Subtitle, new Uri(Path.GetFullPath(filePath)).AbsoluteUri, true))
            ReportError("VLC could not load this subtitle file. Try an SRT, ASS, SSA, or VTT file.", preservePlayback: true);
        else Update(s => s with { ErrorMessage = null });
        RefreshTracks();
    }, "The subtitle file could not be loaded.", preservePlayback: true);

    private bool CanSelectTracks => MediaPlayer is not null && Current.State is PlaybackState.Playing or PlaybackState.Paused;

    private void RefreshTracks()
    {
        if (!CanSelectTracks) return;
        using var media = MediaPlayer!.Media;
        var metadata = media?.Tracks ?? [];
        PlaybackTrack Describe(TrackDescription track, TrackType type)
        {
            var language = metadata.FirstOrDefault(t => t.Id == track.Id && t.TrackType == type).Language;
            var label = string.IsNullOrWhiteSpace(track.Name) ? $"Track {track.Id}" : track.Name;
            if (!string.IsNullOrWhiteSpace(language) && language is not "und" and not "unknown")
            {
                var culture = CultureInfo.GetCultures(CultureTypes.NeutralCultures).FirstOrDefault(c =>
                    c.TwoLetterISOLanguageName.Equals(language, StringComparison.OrdinalIgnoreCase) ||
                    c.ThreeLetterISOLanguageName.Equals(language, StringComparison.OrdinalIgnoreCase));
                label = $"{culture?.EnglishName ?? language} · {label}";
            }
            return new(track.Id, label, language);
        }
        var audio = MediaPlayer.AudioTrackDescription.Where(t => t.Id >= 0).Select(t => Describe(t, TrackType.Audio)).ToArray();
        var subtitles = new[] { new PlaybackTrack(-1, "Off") }.Concat(MediaPlayer.SpuDescription
            .Where(t => t.Id >= 0).Select(t => Describe(t, TrackType.Text))).ToArray();
        var current = Current;
        if (current.AudioTracks.SequenceEqual(audio) && current.SubtitleTracks.SequenceEqual(subtitles) &&
            current.AudioTrackId == MediaPlayer.AudioTrack && current.SubtitleTrackId == MediaPlayer.Spu) return;
        Update(s => s with
        {
            AudioTracks = Array.AsReadOnly(audio), SubtitleTracks = Array.AsReadOnly(subtitles),
            AudioTrackId = MediaPlayer.AudioTrack, SubtitleTrackId = MediaPlayer.Spu
        });
    }

    private async void OnTracksChanged(object? sender, EventArgs e)
    {
        if (!_acceptEvents || _disposed) return;
        try { await ExecuteAsync(RefreshTracks, "Available tracks could not be read.").ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
    }

    private void ApplyAudio()
    {
        if (MediaPlayer is null) return;
        var state = Current;
        MediaPlayer.Volume = state.Volume;
        MediaPlayer.Mute = state.IsMuted;
    }

    private async Task ExecuteAsync(Action operation, string errorMessage, bool preservePlayback = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await Task.Run(operation).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not ObjectDisposedException)
        {
            _logger.LogError(error, "{Message}", errorMessage);
            ReportError(errorMessage, preservePlayback, error.Message);
        }
        finally { _operations.Release(); }
    }

    private void Update(Func<PlaybackSnapshot, PlaybackSnapshot> update)
    {
        PlaybackSnapshot state;
        lock (_stateLock)
        {
            if (_disposed) return;
            _current = state = update(_current) with { Revision = _current.Revision + 1 };
        }
        Changed?.Invoke(this, new PlaybackChangedEventArgs(state));
    }

    private void ReportError(string message, bool preservePlayback = false, string? details = null)
    {
        _logger.LogWarning("Playback error: {Message}. VLC details: {NativeError}", message, Volatile.Read(ref _lastNativeError));
        Update(state => state with
        {
            State = preservePlayback && state.HasMedia ? state.State : PlaybackState.Error,
            CanSeek = preservePlayback && state.CanSeek, ErrorMessage = message,
            ErrorDetails = details ?? string.Join(Environment.NewLine, new[] { state.FilePath, Volatile.Read(ref _lastNativeError) }
                .Where(value => !string.IsNullOrWhiteSpace(value)))
        });
    }

    private void OnPlaying(object? sender, EventArgs e)
    {
        if (!_acceptEvents) return;
        Update(s => s with { State = PlaybackState.Playing });
        OnOutputReady(sender, e);
    }
    private async void OnOutputReady(object? sender, EventArgs e)
    {
        if (!_acceptEvents || _disposed) return;
        try
        {
            await ExecuteAsync(() =>
            {
                if (!CanSelectTracks) return;
                ApplyAudio();
                ApplyAspectRatio(Current.AspectRatio);
                if (MediaPlayer!.SetRate((float)Current.Speed) != 0)
                {
                    Update(s => s with { Speed = MediaPlayer.Rate });
                    ReportError("This file does not support the selected playback speed. Try normal speed (1×).", preservePlayback: true);
                }
                RefreshTracks();
            }, "Playback options could not be applied.", preservePlayback: true).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }
    }
    private void OnNativeLog(object? sender, LogEventArgs e)
    {
        // Keep native callbacks free of file I/O; report the last engine detail with a playback error.
        if (e.Level == LibVLCSharp.Shared.LogLevel.Error)
            Volatile.Write(ref _lastNativeError, $"{e.Module}: {e.Message}");
    }
    private void OnPaused(object? sender, EventArgs e) { if (_acceptEvents) Update(s => s with { State = PlaybackState.Paused }); }
    private void OnEnded(object? sender, EventArgs e)
    {
        if (!_acceptEvents) return;
        if (Current.DurationMs <= 0)
        {
            ReportError("No playable audio or video was found in this file.");
            return;
        }
        Update(s => s with { State = PlaybackState.Ended, PositionMs = s.DurationMs, CanSeek = false });
    }
    private void OnError(object? sender, EventArgs e)
    {
        if (_acceptEvents) ReportError("VLC could not decode this file. It may be damaged or use an unsupported format.");
    }
    private void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        if (_acceptEvents) Update(s => s with { PositionMs = PlaybackMath.ClampPosition(e.Time, s.DurationMs) });
    }
    private void OnLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        if (_acceptEvents) Update(s => s with { DurationMs = Math.Max(0, e.Length) });
    }
    private void OnSeekableChanged(object? sender, MediaPlayerSeekableChangedEventArgs e)
    {
        if (_acceptEvents) Update(s => s with { CanSeek = e.Seekable != 0 });
    }
    private void Subscribe(MediaPlayer player)
    {
        player.Playing += OnPlaying;
        player.Paused += OnPaused;
        player.EndReached += OnEnded;
        player.EncounteredError += OnError;
        player.TimeChanged += OnTimeChanged;
        player.LengthChanged += OnLengthChanged;
        player.SeekableChanged += OnSeekableChanged;
        player.ESAdded += OnTracksChanged;
        player.ESDeleted += OnTracksChanged;
        player.ESSelected += OnTracksChanged;
        player.Vout += OnOutputReady;
    }
    private void Unsubscribe(MediaPlayer player)
    {
        player.Playing -= OnPlaying;
        player.Paused -= OnPaused;
        player.EndReached -= OnEnded;
        player.EncounteredError -= OnError;
        player.TimeChanged -= OnTimeChanged;
        player.LengthChanged -= OnLengthChanged;
        player.SeekableChanged -= OnSeekableChanged;
        player.ESAdded -= OnTracksChanged;
        player.ESDeleted -= OnTracksChanged;
        player.ESSelected -= OnTracksChanged;
        player.Vout -= OnOutputReady;
    }

    public void Dispose()
    {
        if (_disposed) return;
        // The app awaits StopAsync before window teardown; this also supports non-UI/test consumers.
        _operations.Wait();
        try
        {
            if (_disposed) return;
            _acceptEvents = false;
            _disposed = true;
            if (MediaPlayer is not null)
            {
                Unsubscribe(MediaPlayer);
                MediaPlayer.Stop();
                MediaPlayer.Dispose();
                MediaPlayer = null;
            }
            if (_engine is not null)
            {
                _engine.Log -= OnNativeLog;
                _engine.Dispose();
            }
            _engine = null;
        }
        finally { _operations.Release(); }
    }
}
