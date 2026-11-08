using MyCinema.Core.Playback;
using Xunit;

namespace MyCinema.Tests;

public sealed class PlaybackMathTests
{
    [Theory]
    [InlineData(-100, 1000, 0)]
    [InlineData(1500, 1000, 1000)]
    [InlineData(500, -1, 0)]
    [InlineData(500, 1000, 500)]
    public void SeeksStayInsideKnownDuration(long position, long duration, long expected) =>
        Assert.Equal(expected, PlaybackMath.ClampPosition(position, duration));

    [Theory]
    [InlineData(5000, -10000, 60000, 0)]
    [InlineData(55000, 10000, 60000, 60000)]
    [InlineData(long.MaxValue - 5, long.MaxValue, long.MaxValue, long.MaxValue)]
    [InlineData(5, long.MinValue, long.MaxValue, 0)]
    public void SkipOffsetsClampWithoutOverflow(long position, long offset, long duration, long expected) =>
        Assert.Equal(expected, PlaybackMath.OffsetPosition(position, offset, duration));

    [Fact]
    public void TimeLabelsSupportMoviesLongerThanOneDay()
    {
        Assert.Equal("26:03:04", PlaybackMath.FormatTime((26 * 3600L + 3 * 60 + 4) * 1000));
        Assert.Equal("00:00:00", PlaybackMath.FormatTime(-1));
    }
}
