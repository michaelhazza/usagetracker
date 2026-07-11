using UsageWidget.Core.Config;
using UsageWidget.Core.Model;

namespace UsageWidget.Core.Polling;

/// <summary>
/// Background refresh loop. Slows when idle/locked and reports results back to the host via
/// <see cref="OnResults"/> (the host marshals to its UI thread). Refresh isolation/backoff live in
/// <see cref="AccountRefresher"/>; per-cycle stagger lives in <see cref="RefreshCoordinator"/>;
/// this drives the cadence (§11) and guarantees cycles never overlap: a manual "Refresh now"
/// landing mid-cycle must not double-fetch every account (double traffic is how rate limits start)
/// or interleave writes to per-account backoff state.
/// </summary>
public sealed class PollingLoop : IDisposable
{
    private readonly AccountRefresher _refresher;
    private readonly Func<AdapterConfig> _config;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _cycleGate = new(1, 1);

    /// <summary>Raised with the latest results (account id -> result) after each cycle.</summary>
    public event Action<IReadOnlyDictionary<string, UsageResult>>? OnResults;

    /// <summary>Supplied by the host: true when the workstation is locked or the user is idle.</summary>
    public Func<bool> IsIdle { get; set; } = () => false;

    public PollingLoop(AccountRefresher refresher, Func<AdapterConfig> config)
    {
        _refresher = refresher;
        _config = config;
    }

    public void Start() => _ = RunAsync(_cts.Token);

    /// <summary>Manual "Refresh now" — bypasses the cadence timer (safety controls still apply downstream).</summary>
    public Task RefreshNowAsync() => RunOneCycleAsync(_cts.Token);

    private async Task RunOneCycleAsync(CancellationToken ct)
    {
        await _cycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var results = await _refresher
                .RefreshAllAsync(_config(), DateTimeOffset.UtcNow, ct)
                .ConfigureAwait(false);

            // Publish INSIDE the gate so publication order matches execution order: a slow cadence
            // cycle can never deliver its now-stale results after a later manual refresh's.
            OnResults?.Invoke(results);
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var cadence = TimeSpan.FromMinutes(3); // survives even a throwing config callback
            try
            {
                var config = _config();
                cadence = SafeCadence(IsIdle() ? config.Polling.IdleCadence : config.Polling.DefaultCadence);

                if (config.Accounts.Count > 0) // no polling on the empty state (v3.1)
                {
                    await RunOneCycleAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* a whole-cycle failure (even in config/event handlers) must never kill the loop */ }

            try { await Task.Delay(cadence, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// A hand-edited config with a zero/negative/absurd cadence must not crash the loop
    /// (Task.Delay throws on negatives) or hammer the provider. Clamp to sane bounds.
    /// </summary>
    private static TimeSpan SafeCadence(TimeSpan cadence)
    {
        if (cadence < TimeSpan.FromSeconds(30)) return TimeSpan.FromSeconds(30);
        if (cadence > TimeSpan.FromHours(24)) return TimeSpan.FromHours(24);
        return cadence;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
