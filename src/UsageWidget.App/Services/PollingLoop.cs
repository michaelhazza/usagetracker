using UsageWidget.Core.Config;
using UsageWidget.Core.Model;

namespace UsageWidget.App.Services;

/// <summary>
/// Background refresh loop. Staggers accounts, slows when idle/locked, and reports results back to
/// the UI thread via <see cref="OnResults"/>. Refresh isolation/backoff live in
/// <see cref="AccountRefresher"/>; this just drives the cadence (§11).
/// </summary>
public sealed class PollingLoop : IDisposable
{
    private readonly AccountRefresher _refresher;
    private readonly Func<AdapterConfig> _config;
    private readonly CancellationTokenSource _cts = new();

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
    public async Task RefreshNowAsync()
    {
        var results = await _refresher
            .RefreshAllAsync(_config(), DateTimeOffset.UtcNow, _cts.Token)
            .ConfigureAwait(false);
        OnResults?.Invoke(results);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var config = _config();
            if (config.Accounts.Count > 0) // no polling on the empty state (v3.1)
            {
                try
                {
                    var results = await _refresher
                        .RefreshAllAsync(config, DateTimeOffset.UtcNow, ct)
                        .ConfigureAwait(false);
                    OnResults?.Invoke(results);
                }
                catch (OperationCanceledException) { break; }
                catch { /* a whole-cycle failure must never kill the loop */ }
            }

            var cadence = IsIdle() ? config.Polling.IdleCadence : config.Polling.DefaultCadence;
            try { await Task.Delay(cadence, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
