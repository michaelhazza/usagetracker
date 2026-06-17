using System.Windows;
using UsageWidget.App.Services;
using UsageWidget.App.Tray;
using UsageWidget.App.UI;
using UsageWidget.Core.Config;
using UsageWidget.Core.Model;
using UsageWidget.Core.Net;
using UsageWidget.Core.Secrets;

namespace UsageWidget.App;

public partial class App : Application
{
    private SingleInstance? _single;
    private TrayService? _tray;
    private PollingLoop? _loop;
    private HttpClientSender? _sender;
    private PopupWindow? _popup;
    private MainViewModel? _vm;

    private readonly ConfigStore _configStore = new(ConfigStore.DefaultPath());
    private AdapterConfig _config = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Contract #9: only one instance — a second launch focuses the existing popup.
        _single = new SingleInstance();
        if (!_single.IsPrimary)
        {
            SingleInstance.SignalExistingInstance();
            Shutdown();
            return;
        }

        _single.StartActivationListener(() => Dispatcher.Invoke(ShowPopup));

        _config = LoadOrSeedConfig();

        var secrets = new WindowsCredentialManagerStore();
        _sender = new HttpClientSender();
        var factory = new AdapterFactory(_config.Polling);
        var refresher = new AccountRefresher(factory, secrets, _sender);

        _vm = new MainViewModel();
        _vm.Sync(_config.Accounts);
        _popup = new PopupWindow { DataContext = _vm };

        _tray = new TrayService();
        _tray.OnLeftClick += ShowPopup;
        _tray.OnRefreshNow += async () => await _loop!.RefreshNowAsync();
        _tray.OnQuit += Shutdown;
        _tray.SetSeverity(null, null);

        _loop = new PollingLoop(refresher, () => _config);
        _loop.OnResults += ApplyResults;
        _loop.Start();

        // Start minimized to tray; show only if not launched at startup.
        if (!e.Args.Contains("--minimized")) ShowPopup();
    }

    private AdapterConfig LoadOrSeedConfig()
    {
        var loaded = _configStore.Load();
        if (loaded.TemplatesBySource.Count == 0)
        {
            loaded = DefaultConfig.Create();
            _configStore.Save(loaded);
        }

        return loaded;
    }

    private void ApplyResults(IReadOnlyDictionary<string, UsageResult> results)
    {
        Dispatcher.Invoke(() =>
        {
            var nowLocal = DateTimeOffset.Now;
            foreach (var row in _vm!.Rows)
            {
                if (results.TryGetValue(row.Account.Id, out var r)) row.Apply(r, nowLocal);
            }

            _tray!.SetSeverity(
                TrayStatus.WorstSessionPct(results.Values),
                TrayStatus.OverallSeverity(results.Values));
        });
    }

    private void ShowPopup()
    {
        if (_popup is null) return;
        _popup.Show();
        _popup.Activate();
        _popup.Topmost = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _loop?.Dispose();
        _tray?.Dispose();
        _sender?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }
}
