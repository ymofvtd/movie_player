using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Person_Movie_Player.Views;
using Xunit;

namespace MyCinema.Tests;

public sealed partial class LibraryTemplateTests
{
    private static void VerifySliderInputGeometry(PlayerView view)
    {
        var overlay = Assert.IsType<Grid>(view.FindName("PlayerOverlay"));
        var source = new SliderValue { Value = 20 };
        var slider = new SliderProbe { Minimum = 20, Maximum = 120, Style = (Style)overlay.Resources["PlayerSliderStyle"] };
        slider.SetBinding(RangeBase.ValueProperty, new Binding(nameof(SliderValue.Value)) { Source = source, Mode = BindingMode.TwoWay });
        slider.Measure(new Size(240, 26));
        slider.Arrange(new Rect(0, 0, 240, 26));
        slider.UpdateLayout();
        var track = Assert.IsType<Track>(slider.Template.FindName("PART_Track", slider));
        Assert.Equal(26, slider.ActualHeight);
        var halfThumb = track.Thumb.ActualWidth / 2;
        var span = track.ActualWidth - track.Thumb.ActualWidth;
        // Dragging successive points updates the source, including outside the track, without replacing its binding.
        foreach (var fraction in new[] { .25, .75, 1.5, -.5, .5 })
        {
            slider.SetAt(new Point(halfThumb + span * fraction, 2));
            Assert.Equal(20 + Math.Clamp(fraction, 0, 1) * 100, source.Value, 5);
            Assert.True(BindingOperations.IsDataBound(slider, RangeBase.ValueProperty));
        }
        slider.IsDirectionReversed = true;
        slider.SetAt(new Point(halfThumb + span * .25, 24));
        Assert.Equal(95, source.Value, 5);
        slider.IsDirectionReversed = false;
        slider.Minimum = 0;
        slider.Maximum = 100;
        slider.SetAt(new Point(halfThumb + span * .7, 13));
        Assert.Equal(70, source.Value, 5);
    }

    private sealed class SliderProbe : DragSlider
    {
        public void SetAt(Point point) => SetValueFromPoint(point);
    }

    private sealed class SliderValue
    {
        public double Value { get; set; }
    }
}
