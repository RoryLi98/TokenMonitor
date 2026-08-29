using System.Collections.ObjectModel;
using TokenMonitor.Core.Models;
using TokenMonitor.Core.Services;

namespace TokenMonitor.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IReadOnlyList<IQuotaProvider> _providers;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private string _lastUpdatedText = "最近刷新 --:--:--";
    private bool _isRefreshing;

    public MainViewModel(IReadOnlyList<IQuotaProvider> providers)
    {
        _providers = providers;
        Codex = new ProviderQuotaViewModel("codex", "Codex", "#538FFF", TimeSpan.FromMinutes(5));
        Claude = new ProviderQuotaViewModel("claude", "Claude", "#E17D4D", TimeSpan.FromMinutes(20));
        Providers = new ObservableCollection<ProviderQuotaViewModel> { Codex, Claude };
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsRefreshing);
    }

    public event EventHandler? SnapshotsChanged;

    public ObservableCollection<ProviderQuotaViewModel> Providers { get; }
    public ProviderQuotaViewModel Codex { get; }
    public ProviderQuotaViewModel Claude { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    public string LastUpdatedText
    {
        get => _lastUpdatedText;
        private set => SetProperty(ref _lastUpdatedText, value);
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                RefreshCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string TrayTooltip
    {
        get
        {
            var codex = Codex.RemainingPercent is { } c ? $"C {c:0}%" : "C --";
            var claude = Claude.RemainingPercent is { } a ? $"Claude {a:0}%" : "Claude --";
            return $"TokenMonitor · {codex} · {claude}";
        }
    }

    public string TaskbarTooltip =>
        $"Codex {Codex.PercentText} · {Codex.TaskbarCountdownText} 后刷新 · 周 {Codex.TaskbarSecondaryPercentText} / {Codex.TaskbarSecondaryCountdownText}\n" +
        $"Claude {Claude.PercentText} · {Claude.TaskbarCountdownText} 后刷新";

    public async Task RefreshAsync()
    {
        if (!await _refreshGate.WaitAsync(0))
        {
            return;
        }

        IsRefreshing = true;
        LastUpdatedText = "正在刷新…";
        try
        {
            var tasks = _providers.Select(ReadProviderSafelyAsync).ToArray();
            var snapshots = await Task.WhenAll(tasks);
            var now = DateTimeOffset.Now;

            foreach (var snapshot in snapshots)
            {
                var target = Providers.FirstOrDefault(item => item.Id == snapshot.ProviderId);
                target?.Update(snapshot, now);
            }

            LastUpdatedText = $"最近刷新 {now:HH:mm:ss}";
            OnPropertyChanged(nameof(TaskbarTooltip));
            SnapshotsChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsRefreshing = false;
            _refreshGate.Release();
        }
    }

    public void UpdateClock()
    {
        var now = DateTimeOffset.Now;
        foreach (var provider in Providers)
        {
            provider.RefreshDisplay(now);
        }

        OnPropertyChanged(nameof(TaskbarTooltip));
    }

    private static async Task<QuotaSnapshot> ReadProviderSafelyAsync(IQuotaProvider provider)
    {
        try
        {
            return await provider.GetSnapshotAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            return QuotaSnapshot.Unavailable(
                provider.Id,
                provider.DisplayName,
                provider.SourceName,
                exception.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var provider in _providers)
            {
                if (provider is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                else if (provider is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }
        finally
        {
            _refreshGate.Release();
            _refreshGate.Dispose();
        }
    }
}
