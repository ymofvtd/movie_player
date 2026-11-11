using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Person_Movie_Player.Views;

/// <summary>A slider that scrubs continuously from anywhere along its track.</summary>
public class DragSlider : Slider
{
    private bool _dragging;
    public event EventHandler? ScrubStarted;
    public event EventHandler? ScrubCompleted;

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsEnabled || !CaptureMouse()) return;
        Focus();
        _dragging = true;
        ScrubStarted?.Invoke(this, EventArgs.Empty);
        SetValueFromPoint(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        if (!_dragging) { base.OnPreviewMouseMove(e); return; }
        if (e.LeftButton == MouseButtonState.Pressed) SetValueFromPoint(e.GetPosition(this));
        else CompleteScrub();
        e.Handled = true;
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) { base.OnPreviewMouseLeftButtonUp(e); return; }
        SetValueFromPoint(e.GetPosition(this));
        CompleteScrub();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging) CompleteScrub();
    }

    private void CompleteScrub()
    {
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        ScrubCompleted?.Invoke(this, EventArgs.Empty);
    }

    protected void SetValueFromPoint(Point point)
    {
        if (Template?.FindName("PART_Track", this) is not Track { Thumb: not null } track) return;
        var local = TranslatePoint(point, track);
        var horizontal = Orientation == Orientation.Horizontal;
        var thumbSize = horizontal ? track.Thumb.ActualWidth : track.Thumb.ActualHeight;
        var length = (horizontal ? track.ActualWidth : track.ActualHeight) - thumbSize;
        if (length <= 0) return;
        var fraction = ((horizontal ? local.X : local.Y) - thumbSize / 2) / length;
        if (!horizontal) fraction = 1 - fraction;
        if (IsDirectionReversed ^ (horizontal && FlowDirection == FlowDirection.RightToLeft)) fraction = 1 - fraction;
        SetCurrentValue(ValueProperty, Minimum + Math.Clamp(fraction, 0, 1) * (Maximum - Minimum));
    }
}
