using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using UsageWidget.Core.Accounts;
using UsageWidget.Core.Model;
using UsageWidget.Core.Time;

namespace UsageWidget.App.UI;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
    }

    protected void Raise(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One popup row per account: label, icon, two bars, countdowns, and per-row state.</summary>
public sealed class AccountRowViewModel : ObservableObject
{
    private string _label;
    private double? _sessionPct;
    private double? _weeklyPct;
    private string _sessionCountdown = "";
    private string _weeklyCountdown = "";
    private RowState _state = RowState.Loading;
    private string _stateMessage = "";

    public AccountRowViewModel(Account account)
    {
        Account = account;
        _label = account.DisplayLabel(null); // show the nickname immediately, before the first fetch
    }

    public Account Account { get; }
    public string ServiceGlyph => Account.Source.ToString().StartsWith("Codex") ? "X" : "C";

    public string Label { get => _label; set => Set(ref _label, value); }
    public double? SessionPct { get => _sessionPct; set => Set(ref _sessionPct, value); }
    public double? WeeklyPct { get => _weeklyPct; set => Set(ref _weeklyPct, value); }
    public string SessionCountdown { get => _sessionCountdown; set => Set(ref _sessionCountdown, value); }
    public string WeeklyCountdown { get => _weeklyCountdown; set => Set(ref _weeklyCountdown, value); }

    public RowState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            Raise(nameof(State));
            Raise(nameof(NeedsRepaste));
            Raise(nameof(ShowStatus));
            Raise(nameof(StatusText));
        }
    }

    public string StateMessage
    {
        get => _stateMessage;
        set
        {
            if (_stateMessage == value) return;
            _stateMessage = value;
            Raise(nameof(StateMessage));
            Raise(nameof(StatusText));
        }
    }

    /// <summary>
    /// Re-paste is the remedy for an expired session AND for a challenge (a fresh capture brings
    /// fresh edge cookies) — both need the button, or the row's advice is a dead end.
    /// </summary>
    public bool NeedsRepaste => State is RowState.Unauthorized or RowState.Challenge;

    /// <summary>Show a status line for anything that isn't a clean success (loading or any error).</summary>
    public bool ShowStatus => State != RowState.Ok;

    /// <summary>
    /// Human-readable status for the row. Previously only Unauthorized rows said anything; every other
    /// non-OK state rendered as two blank bars with no explanation ("can't see what's going on").
    /// </summary>
    public string StatusText => State switch
    {
        RowState.Ok => "",
        RowState.Loading => "Checking your usage…",
        RowState.Unauthorized => Fallback("Session expired — click Re-paste token."),
        RowState.Challenge => Fallback("Blocked by a security check (Cloudflare). Re-paste a fresh request."),
        RowState.RateLimited => Fallback("Rate limited — will retry automatically."),
        RowState.Stale => Fallback("Couldn't reach the server — will retry."),
        RowState.ConfigProblem => Fallback("Endpoint not set up — open Advanced."),
        _ => _stateMessage,
    };

    private string Fallback(string defaultText) =>
        string.IsNullOrWhiteSpace(_stateMessage) ? defaultText : _stateMessage;

    private DateTimeOffset? _lastGoodAt;

    /// <summary>Apply a refresh result. Countdowns are rendered in local time (contract #6).</summary>
    public void Apply(UsageResult result, DateTimeOffset nowLocal, TimeSpan staleAfter)
    {
        if (result.IsSuccess)
        {
            _lastGoodAt = result.FetchedAt;
            Label = result.AccountLabel;
            State = RowState.Ok;
            StateMessage = "";
            SessionPct = result.Session?.Pct;
            WeeklyPct = result.Weekly?.Pct;
            SessionCountdown = Countdown(result.Session?.ResetAt, nowLocal);
            WeeklyCountdown = Countdown(result.Weekly?.ResetAt, nowLocal);
        }
        else
        {
            // A transient failure must not make the row look like a dead account: keep the last
            // good bars and the last resolved label (a failure result only knows the nickname).
            State = result.ErrorKind switch
            {
                RefreshErrorKind.Unauthorized => RowState.Unauthorized,
                RefreshErrorKind.RateLimited => RowState.RateLimited,
                RefreshErrorKind.NetworkTimeout => RowState.Stale,
                RefreshErrorKind.Challenge => RowState.Challenge,
                _ => RowState.ConfigProblem,
            };

            // §11 stale indicator: once the last good refresh is older than 2× the cadence, say
            // how old the numbers on screen actually are.
            var message = result.ErrorMessage ?? "";
            if (_lastGoodAt is { } lastGood && nowLocal - lastGood > staleAfter)
            {
                message = $"{message} Showing data from {TimeMath.FormatAge(lastGood, nowLocal)}.".TrimStart();
            }

            StateMessage = message;
        }
    }

    private static string Countdown(DateTimeOffset? resetAt, DateTimeOffset nowLocal) =>
        // Claude returns resets_at:null for a window that hasn't been used yet (e.g. a fresh 5h
        // session at 0%). Show "Not started" rather than leaving the slot blank.
        resetAt is null ? "Not started" : TimeMath.FormatResetLabel(resetAt.Value, nowLocal);
}

public enum RowState { Loading, Ok, Stale, RateLimited, Unauthorized, Challenge, ConfigProblem }

public sealed class MainViewModel : ObservableObject
{
    private bool _isEmpty = true;

    public ObservableCollection<AccountRowViewModel> Rows { get; } = new();

    /// <summary>First-run empty state (v3.1): no rows, no polling, show the Add/Editor/Help affordances.</summary>
    public bool IsEmpty { get => _isEmpty; set => Set(ref _isEmpty, value); }

    public void Sync(IReadOnlyCollection<Account> accounts)
    {
        // Preserve the live view-model (bars, countdowns, state) for accounts that still exist.
        // Rebuilding every row on any add/rename blanked ALL accounts until the next poll cycle —
        // which reads as "the widget lost every connection" each time the user touches settings.
        // Tolerate duplicate ids from a hand-edited config (last wins) rather than throwing.
        var existing = new Dictionary<string, AccountRowViewModel>();
        foreach (var r in Rows) existing[r.Account.Id] = r;

        Rows.Clear();
        foreach (var a in accounts.OrderBy(x => x.Order))
        {
            Rows.Add(existing.TryGetValue(a.Id, out var row) ? row : new AccountRowViewModel(a));
        }

        IsEmpty = Rows.Count == 0;
    }

    /// <summary>
    /// Move a row up (delta -1) or down (delta +1), preserving each row's live state (we reorder the
    /// existing items rather than rebuilding). Reassigns Account.Order so the new order persists.
    /// Returns true if anything moved.
    /// </summary>
    public bool Move(AccountRowViewModel row, int delta)
    {
        var from = Rows.IndexOf(row);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= Rows.Count) return false;

        Rows.Move(from, to);
        for (var i = 0; i < Rows.Count; i++) Rows[i].Account.Order = i;
        return true;
    }
}
