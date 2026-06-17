using System.Diagnostics;
using System.IO;
using System.Windows;
using UsageWidget.App.Services;
using UsageWidget.App.Tray;
using UsageWidget.App.UI;
using UsageWidget.Core.Accounts;
using UsageWidget.Core.Config;
using UsageWidget.Core.Model;
using UsageWidget.Core.Net;
using UsageWidget.Core.Secrets;

// Both WPF and WinForms are enabled (WinForms only for the tray NotifyIcon). Disambiguate
// 'Application' so the WPF one wins in this file.
using Application = System.Windows.Application;

namespace UsageWidget.App;

public partial class App : Application
{
    private SingleInstance? _single;
    private TrayService? _tray;
    private PollingLoop? _loop;
    private HttpClientSender? _sender;
    private PopupWindow? _popup;
    private MainViewModel? _vm;
    private WindowsCredentialManagerStore? _secrets;

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

        _secrets = new WindowsCredentialManagerStore();
        _sender = new HttpClientSender();
        var factory = new AdapterFactory(_config.Polling);
        var refresher = new AccountRefresher(factory, _secrets, _sender);

        _vm = new MainViewModel();
        _vm.Sync(_config.Accounts);
        _popup = new PopupWindow { DataContext = _vm };
        _popup.RepasteRequested += OnRepasteRequested;
        _popup.AddAccountRequested += OnAddAccount;
        _popup.OpenEditorRequested += OnOpenTemplateEditor;
        _popup.OpenHelpRequested += OnOpenHelp;

        _tray = new TrayService();
        _tray.OnLeftClick += ShowPopup;
        _tray.OnRefreshNow += async () => await _loop!.RefreshNowAsync();
        _tray.OnAddAccount += OnAddAccount;
        _tray.OnSettings += OnOpenTemplateEditor;
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

    private void OnAddAccount()
    {
        var dialog = new AddAccountWindow(_config);
        if (dialog.ShowDialog() != true || dialog.Result is null) return;

        var (account, secret) = dialog.Result.Value;
        _secrets!.Set(account.Id, account.Source, secret);
        account.Order = _config.Accounts.Count;
        _config.Accounts.Add(account);
        _configStore.Save(_config);

        _vm!.Sync(_config.Accounts);
        _ = _loop!.RefreshNowAsync();
    }

    private void OnRepasteRequested(AccountRowViewModel row)
    {
        var newToken = TokenPromptWindow.Prompt(row.Label);
        if (string.IsNullOrEmpty(newToken)) return;

        _secrets!.Set(row.Account.Id, row.Account.Source, newToken);
        _ = _loop!.RefreshNowAsync();
    }

    private void OnOpenTemplateEditor()
    {
        var editor = new TemplateEditorWindow(_config);
        if (editor.ShowDialog() == true)
        {
            _configStore.Save(_config);
            _ = _loop!.RefreshNowAsync();
        }
    }

    private void OnOpenHelp()
    {
        var path = ConfigStore.DefaultPath();
        var folder = Path.GetDirectoryName(path);
        if (folder is not null && Directory.Exists(folder))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
        }
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
