using TokenMonitor.Core.Models;

namespace TokenMonitor.App.ViewModels;

public sealed class ProviderQuotaViewModel : ObservableObject
{
    private readonly TimeSpan _staleAfter;
    private QuotaSnapshot? _snapshot;
    private string _percentText = "--";
    private string _resetText = "正在读取…";
    private string _statusText = "等待首次刷新";
    private string _statusColor = "#7C8798";
    private string _secondaryText = string.Empty;
    private bool _hasSecondary;
    private double _progressValue;
    private string _taskbarCountdownText = "--:--";
    private string _taskbarSecondaryPercentText = "--";
    private string _taskbarSecondaryCountdownText = "--:--";

    public ProviderQuotaViewModel(string id, string displayName, string accent, TimeSpan staleAfter)
    {
        Id = id;
        DisplayName = displayName;
        Accent = accent;
        _staleAfter = staleAfter;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string Accent { get; }
    public double? RemainingPercent => _snapshot?.RemainingPercent;

    public string PercentText
    {
        get => _percentText;
        private set => SetProperty(ref _percentText, value);
    }

    public string ResetText
    {
        get => _resetText;
        private set => SetProperty(ref _resetText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string StatusColor
    {
        get => _statusColor;
        private set => SetProperty(ref _statusColor, value);
    }

    public string SecondaryText
    {
        get => _secondaryText;
        private set => SetProperty(ref _secondaryText, value);
    }

    public bool HasSecondary
    {
        get => _hasSecondary;
        private set => SetProperty(ref _hasSecondary, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    public string TaskbarCountdownText
    {
        get => _taskbarCountdownText;
        private set => SetProperty(ref _taskbarCountdownText, value);
    }

    public string TaskbarSecondaryPercentText
    {
        get => _taskbarSecondaryPercentText;
        private set => SetProperty(ref _taskbarSecondaryPercentText, value);
    }

    public string TaskbarSecondaryCountdownText
    {
        get => _taskbarSecondaryCountdownText;
        private set => SetProperty(ref _taskbarSecondaryCountdownText, value);
    }

    public void Update(QuotaSnapshot snapshot, DateTimeOffset now)
    {
        _snapshot = snapshot;
        OnPropertyChanged(nameof(RemainingPercent));
        RefreshDisplay(now);
    }

    public void RefreshDisplay(DateTimeOffset now)
    {
        var snapshot = _snapshot;
        if (snapshot is null)
        {
            return;
        }

        if (!snapshot.IsAvailable)
        {
            PercentText = "--";
            ProgressValue = 0;
            ResetText = "暂时无法读取";
            StatusText = snapshot.Error ?? "数据源不可用";
            StatusColor = "#F06A73";
            SecondaryText = string.Empty;
            HasSecondary = false;
            TaskbarCountdownText = "--:--";
            TaskbarSecondaryPercentText = "--";
            TaskbarSecondaryCountdownText = "--:--";
            return;
        }

        var remaining = snapshot.RemainingPercent ?? 0;
        PercentText = $"{remaining:0}%";
        ProgressValue = remaining;

        if (snapshot.ResetAt is { } resetAt)
        {
            var untilReset = resetAt - now;
            if (untilReset > TimeSpan.Zero)
            {
                var prefix = snapshot.ResetIsEstimated ? "约 " : string.Empty;
                ResetText = $"{prefix}{resetAt.ToLocalTime():HH:mm} 刷新 · {FormatDuration(untilReset)}";
                TaskbarCountdownText = FormatCompactDuration(untilReset);
            }
            else
            {
                ResetText = "已到刷新时间 · 等待新样本";
                TaskbarCountdownText = "00:00";
            }
        }
        else
        {
            ResetText = "刷新时间未知";
            TaskbarCountdownText = "--:--";
        }

        var age = now - snapshot.ObservedAt;
        var isStale = age > _staleAfter || snapshot.ResetAt is { } expiredReset && expiredReset <= now;
        StatusColor = isStale ? "#F1B95B" : "#58C98D";
        StatusText = isStale
            ? $"数据可能过期 · {FormatAge(age)}前"
            : $"{snapshot.ObservedAt.ToLocalTime():HH:mm} 采样 · {snapshot.Source}";

        if (snapshot.SecondaryRemainingPercent is { } secondaryRemaining)
        {
            SecondaryText = $"7d 剩余 {secondaryRemaining:0}%";
            HasSecondary = true;
            TaskbarSecondaryPercentText = $"{secondaryRemaining:0}%";
            if (snapshot.SecondaryResetAt is { } secondaryResetAt)
            {
                var untilSecondaryReset = secondaryResetAt - now;
                TaskbarSecondaryCountdownText = untilSecondaryReset > TimeSpan.Zero
                    ? FormatWeeklyCompactDuration(untilSecondaryReset)
                    : "00:00";
            }
            else
            {
                TaskbarSecondaryCountdownText = "--:--";
            }
        }
        else
        {
            SecondaryText = snapshot.ResetIsEstimated ? "刷新时间为估算" : string.Empty;
            HasSecondary = snapshot.ResetIsEstimated;
            TaskbarSecondaryPercentText = "--";
            TaskbarSecondaryCountdownText = "--:--";
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var totalHours = Math.Max(0, (int)Math.Floor(duration.TotalHours));
        return $"{totalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero || age < TimeSpan.FromMinutes(1))
        {
            return "不到1分钟";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{Math.Floor(age.TotalMinutes):0}分钟";
        }

        return $"{Math.Floor(age.TotalHours):0}小时";
    }

    private static string FormatCompactDuration(TimeSpan duration)
    {
        var totalHours = Math.Max(0, (int)Math.Floor(duration.TotalHours));
        return $"{totalHours:00}:{duration.Minutes:00}";
    }

    private static string FormatWeeklyCompactDuration(TimeSpan duration)
    {
        var totalMinutes = Math.Max(0, (int)Math.Floor(duration.TotalMinutes));
        var days = totalMinutes / (24 * 60);
        var hours = totalMinutes / 60 % 24;
        var minutes = totalMinutes % 60;
        return days > 0
            ? $"{days}d{hours:00}:{minutes:00}"
            : $"{hours:00}:{minutes:00}";
    }
}
