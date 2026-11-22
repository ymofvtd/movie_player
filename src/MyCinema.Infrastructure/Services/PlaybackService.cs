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
    private readonly PlaybackPreferencesState? _playbackPreferences;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly object _stateLock = new();
    private PlaybackSnapshot _current = new();
    private LibVLC? _engine;
    private string? _lastNativeError;
    private volatile bool _acceptEvents;
    private volatile bool _disposed;
    private long _reloadPositionMs = -1;
    private PlaybackSnapshot? _reloadTracks;
    private volatile bool _reloadAwaitingMetadata;
    private readonly List<string> _externalSubtitles = [];
    private readonly Dictionary<PlaybackTrack, SubtitleTimeline> _secondaryFiles = [];
    private readonly Dictionary<string, SubtitleTimeline> _subtitleFileCache = new(StringComparer.OrdinalIgnoreCase);
    private (uint Codec, SubtitleTimeline Timeline)[]? _embeddedSubtitles;
    private (HashSet<int> PreviousIds, SubtitleTimeline Timeline)? _pendingPrimarySubtitle;
    private PlaybackPreferences _inputPreferences = new();
    private MatroskaTrackFlags[] _audioFlags = [];
    private MatroskaTrackFlags[] _subtitleFlags = [];
    private bool _preferAudioForInput;
    private bool _preferEnglishForInput;

    public PlaybackService(ILogger<PlaybackService> logger, PlaybackPreferencesState? playbackPreferences = null)
        : this(logger, [], playbackPreferences) { }

    public PlaybackService(ILogger<PlaybackService> logger, string[] engineOptions, PlaybackPreferencesState? playbackPreferences = null)
    {
        _logger = logger;
        _engineOptions = engineOptions;
        _playbackPreferences = playbackPreferences;
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
            Interlocked.Exchange(ref _reloadPositionMs, -1);
            _reloadTracks = null;
            _reloadAwaitingMetadata = false;
            _externalSubtitles.Clear();
            _secondaryFiles.Clear();
            _subtitleFileCache.Clear();
            _embeddedSubtitles = null;
            _pendingPrimarySubtitle = null;
            Volatile.Write(ref _lastNativeError, null);
            Update(state => state with
            {
                State = PlaybackState.Opening, FilePath = fullPath, PositionMs = 0,
                DurationMs = 0, CanSeek = false, ErrorMessage = null, ErrorDetails = null, SessionId = state.SessionId + 1,
                AudioTracks = [], SubtitleTracks = [], SecondarySubtitleTracks = [], AudioTrackId = -1,
                SubtitleTrackId = -1, SecondarySubtitleTrackId = -1, SubtitleOverlay = null, VideoFrameDurationMs = 0
            });
            using var media = new Media(_engine, fullPath, FromType.FromPath);
            ConfigureTrackPreferences(media, fullPath);
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
            media?.AddOption(":no-start-paused");
            Interlocked.Exchange(ref _reloadPositionMs, -1);
            _reloadTracks = null;
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

    public Task ReloadAsync() => ExecuteAsync(() =>
    {
        var saved = Current;
        if (MediaPlayer is null || _engine is null || !saved.HasMedia || saved.State == PlaybackState.Opening) return;
        if (!File.Exists(saved.FilePath))
        {
            ReportError("The video is unavailable. Reconnect its drive, then reload again.", preservePlayback: true,
                details: saved.FilePath);
            return;
        }

        _acceptEvents = false;
        // Stop tears down VLC's decoder and video output before a fresh media input is created.
        // Do not publish Stopped or a zero clock: this is still the same watch-history session.
        MediaPlayer.Stop();
        var position = PlaybackMath.ClampPosition(saved.PositionMs, saved.DurationMs);
        Interlocked.Exchange(ref _reloadPositionMs, position);
        _reloadTracks = saved;
        _reloadAwaitingMetadata = true;
        _preferAudioForInput = _preferEnglishForInput = false;
        Volatile.Write(ref _lastNativeError, null);
        Update(s => s with { State = PlaybackState.Opening, PositionMs = position, CanSeek = false,
            ErrorMessage = null, ErrorDetails = null });
        using var media = new Media(_engine, saved.FilePath!, FromType.FromPath);
        media.AddOption(":start-time=" + (position / 1000d).ToString("0.###", CultureInfo.InvariantCulture));
        if (saved.State == PlaybackState.Paused) media.AddOption(":start-paused");
        foreach (var path in _externalSubtitles.Where(File.Exists))
            media.AddSlave(MediaSlaveType.Subtitle, 4, new Uri(path).AbsoluteUri);
        _acceptEvents = true;
        if (!MediaPlayer.Play(media))
        {
            ReportError("The player could not reload this video. Your playback position has been kept; try Reload again.");
            return;
        }
        ApplyAudio();
        _logger.LogInformation("Reloaded local media {FilePath} at {PositionMs} ms", saved.FilePath, position);
    }, "The player could not reload. Your playback position has been kept; try Reload again.");

    public void Pause()
    {
        if (!_disposed && Current.State is PlaybackState.Playing or PlaybackState.Opening) MediaPlayer?.SetPause(true);
    }

    public Task StopAsync() => ExecuteAsync(() =>
    {
        if (MediaPlayer is null) return;
        _acceptEvents = false;
        MediaPlayer.Stop();
        Interlocked.Exchange(ref _reloadPositionMs, -1);
        _reloadTracks = null;
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
        Interlocked.Exchange(ref _reloadPositionMs, -1);
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
        _reloadTracks = null;
        _preferAudioForInput = false;
        if (!MediaPlayer!.SetAudioTrack(trackId))
            ReportError("This audio track could not be selected. Try another track.", preservePlayback: true);
        else Update(s => s with { AudioTrackId = trackId, ErrorMessage = null });
        RefreshTracks();
    }, "The audio track could not be changed.", preservePlayback: true);

    public Task SetSubtitleTrackAsync(int trackId) => ExecuteAsync(() =>
    {
        var state = Current;
        if (!CanSelectTracks || !state.SubtitleTracks.Any(t => t.Id == trackId)) return;
        _reloadTracks = null;
        _preferEnglishForInput = false;
        _pendingPrimarySubtitle = null;
        if (state.SubtitleOverlay is { } overlay)
        {
            var primary = trackId == -1 ? null : FindSubtitleTimeline(trackId);
            if (trackId != -1 && (trackId == state.SecondarySubtitleTrackId || primary is null))
            {
                ReportDualSubtitleError(trackId == state.SecondarySubtitleTrackId);
                return;
            }
            Update(s => s with { SubtitleTrackId = trackId, SubtitleOverlay = overlay with { Primary = primary }, ErrorMessage = null });
            return;
        }
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
        var fullPath = Path.GetFullPath(filePath);
        var pending = Current.SubtitleOverlay is null
            ? ((HashSet<int> PreviousIds, SubtitleTimeline Timeline)?)null
            : (Current.SubtitleTracks.Select(track => track.Id).ToHashSet(), ReadSubtitleFile(fullPath));
        if (!MediaPlayer!.AddSlave(MediaSlaveType.Subtitle, new Uri(fullPath).AbsoluteUri, true))
            ReportError("VLC could not load this subtitle file. Try an SRT, ASS, SSA, or VTT file.", preservePlayback: true);
        else
        {
            _preferEnglishForInput = false;
            _pendingPrimarySubtitle = pending;
            if (!_externalSubtitles.Contains(fullPath, StringComparer.OrdinalIgnoreCase)) _externalSubtitles.Add(fullPath);
            Update(s => s with { ErrorMessage = null });
        }
        RefreshTracks();
    }, "The subtitle file could not be loaded.", preservePlayback: true);

    public Task SetSecondarySubtitleTrackAsync(int trackId) => ExecuteAsync(() =>
    {
        var state = Current;
        if (!CanSelectTracks || !state.SecondarySubtitleTracks.Any(track => track.Id == trackId)) return;
        if (trackId == -1)
        {
            if (MediaPlayer!.Spu != state.SubtitleTrackId && !MediaPlayer.SetSpu(state.SubtitleTrackId))
            {
                ReportError("The primary subtitle track could not be restored.", preservePlayback: true);
                return;
            }
            _pendingPrimarySubtitle = null;
            Update(s => s with { SecondarySubtitleTrackId = -1, SubtitleOverlay = null, ErrorMessage = null });
            return;
        }

        var secondary = _secondaryFiles.FirstOrDefault(file => file.Key.Id == trackId).Value ?? FindSubtitleTimeline(trackId);
        if (TryEnableSecondarySubtitle(trackId, secondary)) RefreshTracks();
    }, "The secondary subtitle track could not be selected.", preservePlayback: true);

    public Task LoadSecondarySubtitleAsync(string filePath) => ExecuteAsync(() =>
    {
        if (!CanSelectTracks) return;
        var path = Path.GetFullPath(filePath);
        var timeline = ReadSubtitleFile(path);
        var track = _secondaryFiles.FirstOrDefault(file => ReferenceEquals(file.Value, timeline)).Key;
        track ??= new PlaybackTrack(-2 - _secondaryFiles.Count, Path.GetFileName(path));
        if (!TryEnableSecondarySubtitle(track.Id, timeline)) return;
        _secondaryFiles.TryAdd(track, timeline);
        RefreshTracks();
    }, "The secondary subtitle file could not be loaded.", preservePlayback: true);

    private bool TryEnableSecondarySubtitle(int trackId, SubtitleTimeline? secondary)
    {
        var state = Current;
        var sameTrack = trackId == state.SubtitleTrackId;
        var primary = sameTrack || state.SubtitleTrackId == -1 ? null : state.SubtitleOverlay is { } overlay
            ? overlay.Primary : FindSubtitleTimeline(state.SubtitleTrackId);
        if (sameTrack || secondary is null || state.SubtitleTrackId != -1 && primary is null)
        {
            ReportDualSubtitleError(sameTrack);
            return false;
        }
        if (MediaPlayer!.Spu != -1 && !MediaPlayer.SetSpu(-1))
        {
            ReportError("Dual subtitles could not be enabled.", preservePlayback: true);
            return false;
        }
        _reloadTracks = null;
        Update(s => s with { SecondarySubtitleTrackId = trackId, SubtitleOverlay = new(primary, secondary), ErrorMessage = null });
        return true;
    }

    private void ReportDualSubtitleError(bool sameTrack) => ReportError(sameTrack
        ? "Choose different primary and secondary subtitle tracks."
        : "Dual subtitles need text tracks from an MKV or WebM video, or SRT, ASS, SSA, or VTT files.", preservePlayback: true);

    private SubtitleTimeline ReadSubtitleFile(string path)
    {
        if (!_subtitleFileCache.TryGetValue(path, out var timeline))
            _subtitleFileCache[path] = timeline = SubtitleFiles.Read(path);
        return timeline;
    }

    private SubtitleTimeline? FindSubtitleTimeline(int trackId)
    {
        using var media = MediaPlayer!.Media;
        if (media is null) return null;
        var tracks = media.Tracks.Where(track => track.TrackType == TrackType.Text).ToArray();
        var track = tracks.FirstOrDefault(track => track.Id == trackId);
        if (track.Codec is not (MatroskaSubtitles.Subt or MatroskaSubtitles.Ssa or MatroskaSubtitles.Vtt)) return null;

        var path = Current.FilePath!;
        _embeddedSubtitles ??= Path.GetExtension(path).ToLowerInvariant() is ".mkv" or ".webm"
            ? MatroskaSubtitles.Read(path) : [];

        // VLC creates the embedded streams in track-number order, followed by external files.
        // Match within a codec so image tracks and generated captions do not affect the order.
        var ids = tracks.Where(other => other.Codec == track.Codec).Select(other => other.Id).Order().ToArray();
        var embedded = _embeddedSubtitles.Where(other => other.Codec == track.Codec).ToArray();
        var index = Array.IndexOf(ids, trackId);
        if (index < embedded.Length)
            return embedded[index].Timeline.IsEmpty ? null : embedded[index].Timeline;

        var files = media.Slaves.Where(slave => slave.Type == MediaSlaveType.Subtitle)
            .Select(slave => Uri.TryCreate(slave.Uri, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null)
            .OfType<string>()
            .Where(file => Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".srt" => track.Codec == MatroskaSubtitles.Subt,
                ".ass" or ".ssa" => track.Codec == MatroskaSubtitles.Ssa,
                ".vtt" => track.Codec == MatroskaSubtitles.Vtt,
                _ => false
            }).ToArray();
        return ids.Length == embedded.Length + files.Length ? ReadSubtitleFile(files[index - embedded.Length]) : null;
    }

    private bool CanSelectTracks => MediaPlayer is not null && Current.State is PlaybackState.Playing or PlaybackState.Paused;

    private void ConfigureTrackPreferences(Media media, string path)
    {
        _inputPreferences = _playbackPreferences?.Current ?? new PlaybackPreferences();
        _preferAudioForInput = _inputPreferences.PreferOriginalAudio || _inputPreferences.FallbackAudioLanguage.Length > 0;
        _preferEnglishForInput = _inputPreferences.PreferEnglishSubtitles;
        _audioFlags = _subtitleFlags = [];
        if (_preferEnglishForInput) media.AddOption(":sub-language=en");
        if (!_inputPreferences.PreferOriginalAudio && _inputPreferences.FallbackAudioLanguage.Length > 0)
            media.AddOption(":audio-language=" + _inputPreferences.FallbackAudioLanguage);
        if ((_preferAudioForInput || _preferEnglishForInput) && Path.GetExtension(path).ToLowerInvariant() is ".mkv" or ".webm")
        {
            try { (_audioFlags, _subtitleFlags) = MatroskaSubtitles.ReadTrackFlags(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _logger.LogDebug(error, "Could not read Matroska track flags for {FilePath}", path);
            }
        }
    }

    private void ApplyTrackPreferences(MediaPlayer player, PlaybackTrack[] audio, PlaybackTrack[] subtitles, bool overlayActive)
    {
        if (_preferAudioForInput && audio.Length >= _audioFlags.Length &&
            PreferredTracks.Audio(audio, _audioFlags, _inputPreferences) is { } original)
        {
            _preferAudioForInput = false;
            if (player.AudioTrack != original.Id) player.SetAudioTrack(original.Id);
        }
        if (_preferEnglishForInput && !overlayActive && subtitles.Length - 1 >= _subtitleFlags.Length &&
            PreferredTracks.EnglishSubtitles(subtitles, _subtitleFlags) is { } english)
        {
            _preferEnglishForInput = false;
            if (player.Spu != english.Id) player.SetSpu(english.Id);
        }
    }

    private void RefreshTracks()
    {
        if (!CanSelectTracks) return;
        using var media = MediaPlayer!.Media;
        var metadata = media?.Tracks ?? [];
        var video = metadata.FirstOrDefault(t => t.TrackType == TrackType.Video && t.Id == MediaPlayer.VideoTrack);
        var frameDuration = video.Data.Video.FrameRateNum > 0 && video.Data.Video.FrameRateDen > 0
            ? 1000d * video.Data.Video.FrameRateDen / video.Data.Video.FrameRateNum : 0;
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
        var secondarySubtitles = subtitles.Where(track => track.Id == -1 || metadata.Any(native =>
            native.Id == track.Id && native.TrackType == TrackType.Text &&
            native.Codec is MatroskaSubtitles.Subt or MatroskaSubtitles.Ssa or MatroskaSubtitles.Vtt))
            .Concat(_secondaryFiles.Keys).ToArray();
        var current = Current;
        var overlay = current.SubtitleOverlay;
        var overlayPrimaryId = current.SubtitleTrackId;
        if (overlay is not null && _pendingPrimarySubtitle is { } pending)
        {
            var added = subtitles.FirstOrDefault(track => !pending.PreviousIds.Contains(track.Id));
            if (added is not null)
            {
                overlayPrimaryId = added.Id;
                overlay = overlay with { Primary = pending.Timeline };
                _pendingPrimarySubtitle = null;
            }
        }

        if (overlay is not null && MediaPlayer.Spu != -1) MediaPlayer.SetSpu(-1);
        if (_reloadTracks is { } saved)
        {
            if (audio.Any(t => t.Id == saved.AudioTrackId) && MediaPlayer.AudioTrack != saved.AudioTrackId)
                MediaPlayer.SetAudioTrack(saved.AudioTrackId);
            var nativeSubtitleId = overlay is null ? saved.SubtitleTrackId : -1;
            if (overlay is null && subtitles.Any(t => t.Id == nativeSubtitleId) && MediaPlayer.Spu != nativeSubtitleId)
                MediaPlayer.SetSpu(nativeSubtitleId);
            if (MediaPlayer.AudioTrack == saved.AudioTrackId && MediaPlayer.Spu == nativeSubtitleId)
                _reloadTracks = null;
        }
        else ApplyTrackPreferences(MediaPlayer, audio, subtitles, overlay is not null);
        var primaryId = overlay is null ? MediaPlayer.Spu : overlayPrimaryId;
        if (current.AudioTracks.SequenceEqual(audio) && current.SubtitleTracks.SequenceEqual(subtitles) &&
            current.SecondarySubtitleTracks.SequenceEqual(secondarySubtitles) &&
            current.AudioTrackId == MediaPlayer.AudioTrack && current.SubtitleTrackId == primaryId &&
            current.SubtitleOverlay == overlay && current.VideoFrameDurationMs == frameDuration) return;
        Update(s => s with
        {
            AudioTracks = Array.AsReadOnly(audio), SubtitleTracks = Array.AsReadOnly(subtitles),
            SecondarySubtitleTracks = Array.AsReadOnly(secondarySubtitles), AudioTrackId = MediaPlayer.AudioTrack,
            SubtitleTrackId = primaryId, SubtitleOverlay = overlay, VideoFrameDurationMs = frameDuration
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
                var resumePosition = Interlocked.Read(ref _reloadPositionMs);
                // A start-paused input has not consumed start-time yet. Seek its decoded picture explicitly.
                if (resumePosition >= 0 && Current.CanSeek) MediaPlayer!.Time = resumePosition;
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
    private void OnPaused(object? sender, EventArgs e)
    {
        if (!_acceptEvents) return;
        Update(s => s with { State = PlaybackState.Paused });
        OnOutputReady(sender, e);
    }
    private void OnEnded(object? sender, EventArgs e)
    {
        if (!_acceptEvents) return;
        if (_reloadAwaitingMetadata || Current.DurationMs <= 0)
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
        if (!_acceptEvents) return;
        // VLC may briefly report zero while rebuilding the input. Keep the resume point until it catches up.
        var resumePosition = Interlocked.Read(ref _reloadPositionMs);
        if (resumePosition >= 0 && e.Time < resumePosition) return;
        Interlocked.Exchange(ref _reloadPositionMs, -1);
        Update(s => s with { PositionMs = PlaybackMath.ClampPosition(e.Time, s.DurationMs) });
    }
    private void OnLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        if (!_acceptEvents) return;
        if (e.Length > 0) _reloadAwaitingMetadata = false;
        Update(s => s with { DurationMs = Math.Max(0, e.Length) });
    }
    private void OnSeekableChanged(object? sender, MediaPlayerSeekableChangedEventArgs e)
    {
        if (!_acceptEvents) return;
        Update(s => s with { CanSeek = e.Seekable != 0 });
        if (e.Seekable != 0 && Interlocked.Read(ref _reloadPositionMs) >= 0) OnOutputReady(sender, e);
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
