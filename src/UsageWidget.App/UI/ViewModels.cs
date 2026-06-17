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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>One popup row per account: label, icon, two bars, countdowns, and per-row state.</summary>
public sealed class AccountRowViewModel : ObservableObject
{
    private string _label = "";
    private double? _sessionPct;
    private double? _weeklyPct;
    private string _sessionCountdown = "";
    private string _weeklyCountdown = "";
    private RowState _state = RowState.Loading;
    private string _stateMessage = "";

    public AccountRowViewModel(Account account) => Account = account;

    public Account Account { get; }
    public string ServiceGlyph => Account.Source.ToString().StartsWith("Codex") ? "X" : "C";

    public string Label { get => _label; set => Set(ref _label, value); }
    public double? SessionPct { get => _sessionPct; set => Set(ref _sessionPct, value); }
    public double? WeeklyPct { get => _weeklyPct; set => Set(ref _weeklyPct, value); }
    public string SessionCountdown { get => _sessionCountdown; set => Set(ref _sessionCountdown, value); }
    public string WeeklyCountdown { get => _weeklyCountdown; set => Set(ref _weeklyCountdown, value); }
    public RowState State { get => _state; set => Set(ref _state, value); }
    public string StateMessage { get => _stateMessage; set => Set(ref _stateMessage, value); }

    public bool NeedsRepaste => State == RowState.Unauthorized;

    /// <summary>Apply a refresh result. Countdowns are rendered in local time (contract #6).</summary>
    public void Apply(UsageResult result, DateTimeOffset nowLocal)
    {
        Label = result.AccountLabel;
        if (result.IsSuccess)
        {
            State = RowState.Ok;
            StateMessage = "";
            SessionPct = result.Session?.Pct;
            WeeklyPct = result.Weekly?.Pct;
            SessionCountdown = Countdown(result.Session?.ResetAt, nowLocal);
            WeeklyCountdown = Countdown(result.Weekly?.ResetAt, nowLocal);
        }
        else
        {
            State = result.ErrorKind switch
            {
                RefreshErrorKind.Unauthorized => RowState.Unauthorized,
                RefreshErrorKind.RateLimited => RowState.RateLimited,
                RefreshErrorKind.NetworkTimeout => RowState.Stale,
                RefreshErrorKind.Challenge => RowState.Challenge,
                _ => RowState.ConfigProblem,
            };
            StateMessage = result.ErrorMessage ?? "";
        }
    }

    private static string Countdown(DateTimeOffset? resetAt, DateTimeOffset nowLocal) =>
        resetAt is null ? "" : TimeMath.FormatCountdown(resetAt.Value.ToLocalTime(), nowLocal);
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
        Rows.Clear();
        foreach (var a in accounts.OrderBy(x => x.Order))
        {
            Rows.Add(new AccountRowViewModel(a));
        }

        IsEmpty = Rows.Count == 0;
    }
}
