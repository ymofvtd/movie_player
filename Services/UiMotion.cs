using System.Windows;
using System.Windows.Media.Animation;

namespace Person_Movie_Player.Services;

/// <summary>Short page fades, controlled by the saved preference and Windows animation setting.</summary>
public static class UiMotion
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(UiMotion), new FrameworkPropertyMetadata(true,
            FrameworkPropertyMetadataOptions.Inherits, OnEnabledChanged));
    public static readonly DependencyProperty FadeOnLoadProperty = DependencyProperty.RegisterAttached(
        "FadeOnLoad", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, OnFadeChanged));
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    public static bool GetFadeOnLoad(DependencyObject target) => (bool)target.GetValue(FadeOnLoadProperty);
    public static void SetFadeOnLoad(DependencyObject target, bool value) => target.SetValue(FadeOnLoadProperty, value);

    private static void OnFadeChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement element) return;
        element.Loaded -= OnLoaded;
        element.Unloaded -= OnUnloaded;
        if ((bool)args.NewValue) { element.Loaded += OnLoaded; element.Unloaded += OnUnloaded; }
        else element.BeginAnimation(UIElement.OpacityProperty, null);
    }
    private static void OnEnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (!(bool)args.NewValue && target is FrameworkElement element && GetFadeOnLoad(element))
            element.BeginAnimation(UIElement.OpacityProperty, null);
    }
    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        if (!GetEnabled(element) || !SystemParameters.ClientAreaAnimation) return;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.75, 1,
            TimeSpan.FromMilliseconds(140)) { FillBehavior = FillBehavior.Stop });
    }
    private static void OnUnloaded(object sender, RoutedEventArgs e) =>
        ((FrameworkElement)sender).BeginAnimation(UIElement.OpacityProperty, null);
}
