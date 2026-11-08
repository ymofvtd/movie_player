namespace MyCinema.Core.Playback;

public sealed record PlaybackTrack(int Id, string Label, string? Language = null)
{
    public override string ToString() => Label;
}
