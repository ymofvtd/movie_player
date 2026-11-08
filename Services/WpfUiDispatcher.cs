using System.Windows.Threading;

namespace Person_Movie_Player.Services;

public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action)
    {
        if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(action, DispatcherPriority.DataBind);
    }

    // Allow data templates and the native video host to attach before starting playback.
    public async Task YieldAsync() => await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
}
