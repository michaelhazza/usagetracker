using System.IO;
using System.IO.Pipes;
using System.Text;

namespace UsageWidget.App;

/// <summary>
/// Contract #9: a second launch focuses the existing popup instead of starting a new process.
/// A named mutex detects the first instance; a named pipe lets the second instance signal "show".
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\UsageWidget.SingleInstance";
    private const string PipeName = "UsageWidget.Activate";

    private readonly Mutex _mutex;
    private CancellationTokenSource? _listenerCts;

    public bool IsPrimary { get; }

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        IsPrimary = createdNew;
    }

    /// <summary>On the primary instance, listen for activation requests from later launches.</summary>
    public void StartActivationListener(Action onActivate)
    {
        _listenerCts = new CancellationTokenSource();
        var ct = _listenerCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In);
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                    onActivate();
                }
                catch (OperationCanceledException) { break; }
                catch { /* keep listening */ }
            }
        }, ct);
    }

    /// <summary>From a secondary instance: ask the primary to show its popup, then exit.</summary>
    public static void SignalExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(500);
            using var writer = new StreamWriter(client, Encoding.UTF8);
            writer.Write("show");
        }
        catch { /* primary may be mid-shutdown; nothing to do */ }
    }

    public void Dispose()
    {
        _listenerCts?.Cancel();
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
