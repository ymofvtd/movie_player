using Person_Movie_Player.Services;
using Xunit;

namespace MyCinema.Tests;

public sealed class SingleInstanceTests
{
    [Fact]
    public async Task SubsequentLaunchesNotifyThePrimaryInsteadOfBecomingAnotherInstance()
    {
        var name = "MyCinema.Tests." + Guid.NewGuid().ToString("N");
        using var activations = new SemaphoreSlim(0);
        using var primary = new PrimaryInstance(name, () => activations.Release());
        await primary.Ready;
        for (var launch = 0; launch < 3; launch++)
        {
            using var secondary = new SingleInstanceApplication(name);
            Assert.False(secondary.IsPrimaryInstance);
            Assert.True(await secondary.ActivatePrimaryAsync());
            Assert.True(await activations.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task ClosingThePrimaryLetsTheNextLaunchOwnTheInstanceAndListenAgain()
    {
        var name = "MyCinema.Tests." + Guid.NewGuid().ToString("N");
        using (var first = new PrimaryInstance(name, () => { })) await first.Ready;
        using var activated = new SemaphoreSlim(0);
        using var next = new PrimaryInstance(name, () => activated.Release());
        await next.Ready;
        using var secondary = new SingleInstanceApplication(name);
        Assert.False(secondary.IsPrimaryInstance);
        Assert.True(await secondary.ActivatePrimaryAsync());
        Assert.True(await activated.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ADisconnectedLaunchDoesNotStopTheActivationListener()
    {
        var name = "MyCinema.Tests." + Guid.NewGuid().ToString("N");
        using var activated = new SemaphoreSlim(0);
        using var primary = new PrimaryInstance(name, () => activated.Release());
        await primary.Ready;
        using (var disconnected = new System.IO.Pipes.NamedPipeClientStream(".", name + ".Activate",
            System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous))
            await disconnected.ConnectAsync(5000);
        using var secondary = new SingleInstanceApplication(name);
        Assert.False(secondary.IsPrimaryInstance);
        Assert.True(await secondary.ActivatePrimaryAsync());
        Assert.True(await activated.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class PrimaryInstance : IDisposable
    {
        private readonly ManualResetEventSlim _stop = new();
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;

        public PrimaryInstance(string name, Action activate)
        {
            // Mirror WPF: the primary acquires and releases its mutex on one dedicated thread.
            _thread = new Thread(() =>
            {
                try
                {
                    using var primary = new SingleInstanceApplication(name);
                    Assert.True(primary.IsPrimaryInstance);
                    primary.ListenForActivation(activate);
                    _ready.SetResult();
                    _stop.Wait();
                }
                catch (Exception error) { _ready.TrySetException(error); }
            });
            _thread.IsBackground = true;
            _thread.Start();
        }

        public Task Ready => _ready.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Dispose()
        {
            _stop.Set();
            Assert.True(_thread.Join(TimeSpan.FromSeconds(5)), "The instance listener did not stop.");
            _stop.Dispose();
        }
    }
}
