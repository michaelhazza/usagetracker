using System.Diagnostics;
using System.IO;
using System.Windows;
using UsageWidget.App.Services;
using UsageWidget.App.Tray;
using UsageWidget.App.UI;
using UsageWidget.Core.Accounts;
using UsageWidget.Core.Config;
using UsageWidget.Core.Import;
using UsageWidget.Core.Model;
using UsageWidget.Core.Net;
using UsageWidget.Core.Secrets;
using UsageWidget.Core.Security;

// Both WPF and WinForms are enabled (WinForms only for the tray NotifyIcon). Disambiguate the
// types whose names collide so the WPF ones win in this file.
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace UsageWidget.App;

public partial class App : Application
{
    private SingleInstance? _single;
    private TrayService? _tray;
    private PollingLoop? _loop;
    private HttpClientSender? _sender;
    private PopupWindow? _popup;
    private MainViewModel? _vm;
    private ISecretStore? _secrets;

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

        // Last-resort guard: surface errors instead of hard-crashing the widget.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                Redactor.Redact(args.Exception.Message),
                "Usage Widget", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        _config = LoadOrSeedConfig();

        // DPAPI-encrypted file store: handles large session cookies that exceed Credential Manager.
        _secrets = new DpapiSecretStore();
        _sender = new HttpClientSender();
        var factory = new AdapterFactory(_config.Polling);
        var refresher = new AccountRefresher(factory, _secrets, _sender);

        _vm = new MainViewModel();
        _vm.Sync(_config.Accounts);
        _popup = new PopupWindow { DataContext = _vm };
        _popup.RestorePosition(_config.Window);
        _popup.RestorePinned(_config.Pinned);
        _popup.BoundsChanged += OnBoundsChanged;
        _popup.PinnedChanged += OnPinnedChanged;
        _popup.MoveRequested += OnMoveAccount;
        _popup.ManageRequested += OnManageAccount;
        _popup.RemoveRequested += OnRemoveAccount;
        _popup.RepasteRequested += OnRepasteRequested;
        _popup.AddAccountRequested += OnAddAccount;
        _popup.OpenEditorRequested += OnOpenTemplateEditor;
        _popup.OpenHelpRequested += OnOpenHelp;
        _popup.RefreshRequested += OnPopupRefresh;

        _tray = new TrayService();
        _tray.OnLeftClick += ShowPopup;
        _tray.OnRefreshNow += () => FireAndLogRefresh("tray-refresh");
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
        var dialog = new AddAccountWindow(_config, SaveAndCheckAccountAsync);
        var saved = dialog.ShowDialog() == true;

        // Show the popup so the freshly added row (with its live bars or a clear status) is visible.
        if (saved) ShowPopup();
    }

    /// <summary>
    /// Persists the account + secret, then runs one live refresh so the row shows real state right
    /// away. Returns null on success, or a user-facing message the dialog displays inline.
    /// </summary>
    private async Task<string?> SaveAndCheckAccountAsync(Account account, string secret)
    {
        try
        {
            _secrets!.Set(account.Id, account.Source, secret);
            account.Order = _config.Accounts.Count;
            _config.Accounts.Add(account);
            _configStore.Save(_config);
            _vm!.Sync(_config.Accounts);
        }
        catch (Exception ex)
        {
            // Don't leave a half-added account in memory if persisting the secret/config failed, and
            // clean up the secret blob so a failed add doesn't leak a stored login.
            _config.Accounts.RemoveAll(a => a.Id == account.Id);
            try { _secrets!.Delete(account.Id, account.Source); } catch { /* best-effort */ }
            Log("save-account", ex);
            return "Couldn't save the account: " + Redactor.Redact(ex.Message);
        }

        // First live check. A failure here (bad token, Cloudflare, etc.) is NOT a save failure — the
        // account is valid config, so keep it and let the row surface the reason.
        try { await _loop!.RefreshNowAsync(); }
        catch (Exception ex) { Log("first-refresh", ex); }

        return null;
    }

    private void OnBoundsChanged(double left, double top, double width)
    {
        _config.Window = new WindowBounds { Left = left, Top = top, Width = width };
        try { _configStore.Save(_config); }
        catch (Exception ex) { Log("save-window-bounds", ex); }
    }

    private void OnPinnedChanged(bool pinned)
    {
        _config.Pinned = pinned;
        try { _configStore.Save(_config); }
        catch (Exception ex) { Log("save-pinned", ex); }
    }

    private void OnMoveAccount(AccountRowViewModel row, int delta)
    {
        if (!_vm!.Move(row, delta)) return;
        try
        {
            _configStore.Save(_config);
        }
        catch (Exception ex)
        {
            Log("save-order", ex);
            _vm.Move(row, -delta); // keep the visible order matching what's actually persisted
        }
    }

    private void OnManageAccount(AccountRowViewModel row)
    {
        var dialog = new ManageAccountWindow(row.Account, ManageAccountAsync);
        dialog.ShowDialog();
        if (_config.Pinned) ShowPopup();
    }

    /// <summary>
    /// Apply a rename and/or a re-pasted login from the Manage dialog. Returns null on success or a
    /// user-facing error. The new cURL (if any) refreshes both the captured template and the secret.
    /// </summary>
    private async Task<string?> ManageAccountAsync(Account account, string? nickname, string? curl)
    {
        var oldNickname = account.Nickname;
        var oldTemplate = account.Template;
        string? newSecret = null;

        try
        {
            // Validate the new login (if any) and apply in-memory edits BEFORE persisting, so a bad
            // cURL throws without mutating anything that's already saved.
            if (!string.IsNullOrWhiteSpace(curl))
            {
                var imported = CurlAccountImport.Build(curl, account.Source);
                account.Template = imported.Template;
                newSecret = imported.Secret;
            }

            account.Nickname = string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim();
            _configStore.Save(_config); // durable config change first
        }
        catch (Exception ex)
        {
            // Roll back the in-memory edits so the VM and disk can't diverge.
            account.Nickname = oldNickname;
            account.Template = oldTemplate;
            Log("manage-account", ex);
            return "Couldn't update the account: " + Redactor.Redact(ex.Message);
        }

        // Config is saved; now store the refreshed secret (only after the durable change succeeded).
        if (newSecret is not null)
        {
            try
            {
                _secrets!.Set(account.Id, account.Source, newSecret);
            }
            catch (Exception ex)
            {
                Log("manage-secret", ex);

                // The saved config now points at a new template with no matching secret. Roll the
                // template back to the prior (working) login so the account isn't left broken; keep
                // the rename. If re-saving the rollback also fails, surface that it may need re-pasting.
                account.Template = oldTemplate;
                try { _configStore.Save(_config); }
                catch (Exception rollbackEx) { Log("manage-secret-rollback", rollbackEx); }

                UpdateRowLabel(account);
                return "Name saved, but the login refresh failed — please re-paste again: "
                    + Redactor.Redact(ex.Message);
            }
        }

        UpdateRowLabel(account);

        try { await _loop!.RefreshNowAsync(); }
        catch (Exception ex) { Log("manage-refresh", ex); }

        return null;
    }

    private void OnRemoveAccount(AccountRowViewModel row)
    {
        var result = MessageBox.Show(
            $"Remove \"{row.Label}\"? This deletes its stored login from this PC.",
            "Usage Widget", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            // Durable config removal first, then update the UI — so a later secret-delete hiccup can't
            // leave a visible row whose login is already gone.
            _config.Accounts.RemoveAll(a => a.Id == row.Account.Id);
            for (var i = 0; i < _config.Accounts.Count; i++) _config.Accounts[i].Order = i;
            _configStore.Save(_config);
            _vm!.Sync(_config.Accounts);
        }
        catch (Exception ex)
        {
            Log("remove-account", ex);
            MessageBox.Show(
                "Couldn't remove the account: " + Redactor.Redact(ex.Message),
                "Usage Widget", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Best-effort secret cleanup. An orphaned encrypted blob is harmless (ids are GUIDs, never
        // reused), so this never blocks the removal the user already confirmed.
        try { _secrets!.Delete(row.Account.Id, row.Account.Source); }
        catch (Exception ex) { Log("remove-secret", ex); }
    }

    private void UpdateRowLabel(Account account)
    {
        var row = _vm!.Rows.FirstOrDefault(r => r.Account.Id == account.Id);
        if (row is not null) row.Label = account.DisplayLabel(null);
    }

    /// <summary>Trigger a refresh without awaiting, but still observe + log any failure.</summary>
    private async void FireAndLogRefresh(string context, bool force = false)
    {
        try { await _loop!.RefreshNowAsync(force); }
        catch (Exception ex) { Log(context, ex); }
    }

    // Header ↻ : forced refresh (bypasses backoff). Disable the button for the in-flight duration
    // so rapid clicks can't stack concurrent requests at a challenged endpoint.
    private async void OnPopupRefresh()
    {
        _popup!.SetRefreshing(true);
        try { await _loop!.RefreshNowAsync(force: true); }
        catch (Exception ex) { Log("popup-refresh", ex); }
        finally { _popup!.SetRefreshing(false); }
    }

    /// <summary>Best-effort redacted log to %APPDATA%\UsageWidget\log.txt for post-hoc diagnosis.</summary>
    private static void Log(string context, Exception ex)
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigStore.DefaultPath());
            if (dir is null) return;
            Directory.CreateDirectory(dir);
            var line = $"{DateTimeOffset.Now:O}\t{context}\t{Redactor.Redact(ex.ToString())}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(dir, "log.txt"), line);
        }
        catch { /* logging must never throw */ }
    }

    private void OnRepasteRequested(AccountRowViewModel row)
    {
        var newToken = TokenPromptWindow.Prompt(row.Label);
        if (string.IsNullOrEmpty(newToken)) return;

        _secrets!.Set(row.Account.Id, row.Account.Source, newToken);
        FireAndLogRefresh("repaste-refresh");
    }

    private void OnOpenTemplateEditor()
    {
        var editor = new TemplateEditorWindow(_config);
        if (editor.ShowDialog() == true)
        {
            _configStore.Save(_config);
            FireAndLogRefresh("editor-refresh");
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
