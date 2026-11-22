namespace MyCinema.Core.Playback;

public sealed record SubtitleCue(long StartMs, long EndMs, string Text);

public sealed class SubtitleTimeline
{
    private readonly SubtitleCue[] _cues;
    private readonly long[] _latestEnd;

    public SubtitleTimeline(IEnumerable<SubtitleCue> cues)
    {
        _cues = cues.Where(cue => cue.EndMs > cue.StartMs && cue.Text.Length > 0).OrderBy(cue => cue.StartMs).ToArray();
        _latestEnd = new long[_cues.Length];
        long end = 0;
        for (var i = 0; i < _cues.Length; i++)
            _latestEnd[i] = end = Math.Max(end, _cues[i].EndMs);
    }

    public bool IsEmpty => _cues.Length == 0;

    public string TextAt(long positionMs)
    {
        var first = 0;
        var last = _cues.Length;
        while (first < last)
        {
            var middle = first + (last - first) / 2;
            if (_latestEnd[middle] <= positionMs) first = middle + 1;
            else last = middle;
        }

        var text = string.Empty;
        for (var i = first; i < _cues.Length && _cues[i].StartMs <= positionMs; i++)
        {
            var cue = _cues[i];
            if (cue.EndMs > positionMs)
                text = text.Length == 0 ? cue.Text : string.Concat(text, "\n", cue.Text);
        }
        return text;
    }
}

public sealed record SubtitleOverlay(SubtitleTimeline? Primary, SubtitleTimeline Secondary);
