using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Playback;

namespace Person_Movie_Player.ViewModels;

public sealed class PlaybackTimingEditorViewModel : ObservableObject
{
    private readonly string _filePath;
    private readonly long _durationMs;
    private readonly IPlaybackSegmentService _service;
    private readonly Func<long> _currentPosition;
    private readonly Action<IReadOnlyList<PlaybackSegment>> _saved;
    private string? _message;
    private bool _isSaving;

    public PlaybackTimingEditorViewModel(string filePath, long durationMs, PlaybackSegmentData data,
        IPlaybackSegmentService service, Func<long> currentPosition, Action<IReadOnlyList<PlaybackSegment>> saved)
    {
        _filePath = filePath;
        _durationMs = durationMs;
        _service = service;
        _currentPosition = currentPosition;
        _saved = saved;
        foreach (var segment in data.Segments.Where(s => s.Kind is PlaybackSegmentKind.Intro or PlaybackSegmentKind.Outro))
            Ranges.Add(CreateRange(segment.Kind, FormatTime(segment.StartMs), segment.EndMs == long.MaxValue ? "end" : FormatTime(segment.EndMs)));
        AddIntroCommand = new RelayCommand(() => AddRange(PlaybackSegmentKind.Intro));
        AddOutroCommand = new RelayCommand(() => AddRange(PlaybackSegmentKind.Outro));
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        Ranges.CollectionChanged += (_, _) => { Message = null; OnPropertyChanged(nameof(IsEmpty)); };
    }

    public ObservableCollection<PlaybackTimingRangeViewModel> Ranges { get; } = [];
    public bool IsEmpty => Ranges.Count == 0;
    public bool IsSaving { get => _isSaving; private set { if (SetProperty(ref _isSaving, value)) OnPropertyChanged(nameof(CanEdit)); } }
    public bool CanEdit => !IsSaving;
    public string? Message { get => _message; private set => SetProperty(ref _message, value); }
    public IRelayCommand AddIntroCommand { get; }
    public IRelayCommand AddOutroCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }

    private PlaybackTimingRangeViewModel CreateRange(PlaybackSegmentKind kind, string start, string end)
    {
        var range = new PlaybackTimingRangeViewModel(kind, start, end, _currentPosition, r => Ranges.Remove(r));
        range.PropertyChanged += (_, _) => Message = null;
        return range;
    }

    private void AddRange(PlaybackSegmentKind kind) => Ranges.Add(CreateRange(kind,
        FormatTime(Math.Clamp(_currentPosition(), 0, _durationMs)), kind == PlaybackSegmentKind.Outro ? "end" : ""));

    private async Task SaveAsync()
    {
        Message = null;
        var segments = new List<PlaybackSegment>();
        foreach (var range in Ranges)
        {
            var toEnd = range.Kind == PlaybackSegmentKind.Outro && range.End.Trim().Equals("end", StringComparison.OrdinalIgnoreCase);
            var end = long.MaxValue;
            if (!TryTime(range.Start, out var start) || !toEnd && !TryTime(range.End, out end))
            {
                Message = $"Enter {range.Label.ToLowerInvariant()} times as m:ss or h:mm:ss. An outro can end at ‘end’.";
                return;
            }
            var actualEnd = toEnd ? _durationMs : end;
            if (start >= actualEnd || actualEnd > _durationMs)
            {
                Message = $"{range.Label} must end after its start and fit within this video's duration.";
                return;
            }
            segments.Add(new(range.Kind, start, toEnd ? long.MaxValue : end));
        }
        var ordered = segments.OrderBy(s => s.StartMs).ToArray();
        for (var i = 1; i < ordered.Length; i++)
            if (ordered[i].StartMs < ordered[i - 1].EndAt(_durationMs))
            {
                Message = "Timing ranges cannot overlap. Adjust their start or end times.";
                return;
            }
        IsSaving = true;
        try
        {
            await _service.SaveAsync(_filePath, ordered);
            _saved(ordered);
            Message = "Saved for this video. Ready now and next time you watch.";
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        {
            Message = "Couldn't save timings. Your previous timings are unchanged. Try saving again.";
        }
        finally { IsSaving = false; }
    }

    internal static string FormatTime(long milliseconds)
    {
        var seconds = milliseconds / 1000;
        var clock = seconds >= 3600 ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}" : $"{seconds / 60}:{seconds % 60:00}";
        return milliseconds % 1000 == 0 ? clock : $"{clock}.{milliseconds % 1000:000}";
    }

    private static bool TryTime(string value, out long milliseconds)
    {
        milliseconds = 0;
        var parts = value.Trim().Split(':');
        if (parts.Length is < 2 or > 3 ||
            !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first) ||
            !decimal.TryParse(parts[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) ||
            seconds is < 0 or >= 60) return false;
        long minutes = 0;
        if (parts.Length == 3 && (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minutes) || minutes >= 60)) return false;
        var total = ((decimal)first * (parts.Length == 3 ? 3600 : 60) + minutes * 60 + seconds) * 1000;
        if (total > long.MaxValue) return false;
        milliseconds = (long)total;
        return true;
    }
}

public sealed class PlaybackTimingRangeViewModel : ObservableObject
{
    private string _start;
    private string _end;
    public PlaybackTimingRangeViewModel(PlaybackSegmentKind kind, string start, string end, Func<long> currentPosition,
        Action<PlaybackTimingRangeViewModel> remove)
    {
        Kind = kind;
        _start = start;
        _end = end;
        UseStartCommand = new RelayCommand(() => Start = PlaybackTimingEditorViewModel.FormatTime(currentPosition()));
        UseEndCommand = new RelayCommand(() => End = PlaybackTimingEditorViewModel.FormatTime(currentPosition()));
        RemoveCommand = new RelayCommand(() => remove(this));
    }
    public PlaybackSegmentKind Kind { get; }
    public string Label => Kind == PlaybackSegmentKind.Intro ? "Intro" : "Outro";
    public string Start { get => _start; set => SetProperty(ref _start, value); }
    public string End { get => _end; set => SetProperty(ref _end, value); }
    public IRelayCommand UseStartCommand { get; }
    public IRelayCommand UseEndCommand { get; }
    public IRelayCommand RemoveCommand { get; }
}
