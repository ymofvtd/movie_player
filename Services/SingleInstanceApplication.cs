using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Person_Movie_Player.Services;

/// <summary>Owns the per-user/session app instance and forwards subsequent launches to it.</summary>
public sealed class SingleInstanceApplication : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listener;
    private bool _disposed;

    public SingleInstanceApplication(string? instanceName = null)
    {
        instanceName ??= $"MyCinema.{WindowsIdentity.GetCurrent().User!.Value}.{Process.GetCurrentProcess().SessionId}";
        _pipeName = instanceName + ".Activate";
        _mutex = new Mutex(false, @"Local\" + instanceName);
        try { IsPrimaryInstance = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimaryInstance = true; }
    }

    public bool IsPrimaryInstance { get; }

    public void ListenForActivation(Action activate)
    {
        if (!IsPrimaryInstance || _listener is not null)
            throw new InvalidOperationException("Only the primary instance can listen for launches, once.");
        _listener = ListenAsync(activate, _shutdown.Token);
    }

    public async Task<bool> ActivatePrimaryAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            var processId = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (!uint.TryParse(processId, NumberStyles.None, CultureInfo.InvariantCulture, out var primaryId)) return false;
            // The newly launched process can grant the existing window foreground permission.
            AllowSetForegroundWindow(primaryId);
            await writer.WriteLineAsync("activate".AsMemory(), timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not activate the existing MyCinema instance: {error}");
            return false;
        }
    }

    private async Task ListenAsync(Action activate, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(Environment.ProcessId.ToString(CultureInfo.InvariantCulture).AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) == "activate") activate();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (IOException error)
            {
                // A second launch can exit/disconnect before completing its activation request.
                Debug.WriteLine($"MyCinema activation connection closed: {error}");
            }
        }
    }

    // The owning WPF UI thread creates and disposes this object, as required by Mutex.
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _listener?.GetAwaiter().GetResult();
        _shutdown.Dispose();
        if (IsPrimaryInstance) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
