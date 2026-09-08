using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TokenMonitor.App.Infrastructure;
using TokenMonitor.App.ViewModels;

namespace TokenMonitor.App;

public partial class TaskbarBarWindow : Window
{
    private readonly TaskbarPlacementService _placementService;
    private readonly AppSettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _placementTimer;

    internal TaskbarBarWindow(
        MainViewModel viewModel,
        TaskbarPlacementService placementService,
        AppSettingsStore settingsStore,
        AppSettings settings)
    {
        InitializeComponent();
#if DEBUG
        ShowInTaskbar = true;
        ShowActivated = true;
#endif
        DataContext = viewModel;
        _placementService = placementService;
        _settingsStore = settingsStore;
        _settings = settings;
        ApplyTaskbarTextSettings();

        _placementTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _placementTimer.Tick += (_, _) => EnsurePlacement();

        Loaded += (_, _) =>
        {
            EnsurePlacement();
            _placementTimer.Start();
        };
        Closed += (_, _) => _placementTimer.Stop();
    }

    public event EventHandler? OpenRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? HideRequested;
    public event EventHandler? ExitRequested;

    public void EnsurePlacement()
    {
        var result = _placementService.Place(
            this,
            _settings.TaskbarPositionRatio,
            verticalOffsetDip: _settings.TaskbarWindowOffsetTop);
        var hostText = result.IsEmbedded ? "已嵌入任务栏" : "任务栏覆盖层";
        var collisionText = result.TrafficMonitorDetected
            ? result.AvoidedCollision ? " · 已避让 TrafficMonitor" : " · 需要手动调整位置"
            : string.Empty;
        Title = $"TokenMonitor 任务栏文本条 · {hostText}{collisionText}";
    }

    public void ApplyTaskbarTextSettings()
    {
        CodexTaskbarLabel.Text = _settings.TaskbarCodexLabel;
        ClaudeTaskbarLabel.Text = _settings.TaskbarClaudeLabel;
        CodexTaskbarCountdownPrefix.Text = _settings.TaskbarCodexCountdownPrefix;
        ClaudeTaskbarCountdownPrefix.Text = _settings.TaskbarClaudeCountdownPrefix;
        CodexTaskbarWeeklyLabel.Text = _settings.TaskbarCodexWeeklyRemainingText;
        CodexTaskbarWeeklyResetText.Text = _settings.TaskbarCodexWeeklyResetText;
        ClaudeTaskbarWeeklyLabel.Text = _settings.TaskbarClaudeWeeklyRemainingText;
        ClaudeTaskbarWeeklyResetText.Text = _settings.TaskbarClaudeWeeklyResetText;
        ClaudeTaskbarWeeklyPercent.Text = _settings.TaskbarClaudeWeeklyPercentPlaceholder;
        ClaudeTaskbarWeeklyCountdown.Text = _settings.TaskbarClaudeWeeklyCountdownPlaceholder;

        var fontFamily = new System.Windows.Media.FontFamily(_settings.TaskbarFontFamily);
        var fontSizeDip = _settings.TaskbarFontSizePoints * 96d / 72d;
        var textBlocks = new[]
        {
            CodexTaskbarLabel,
            CodexTaskbarPercent,
            CodexTaskbarCountdownPrefix,
            CodexTaskbarCountdown,
            CodexTaskbarWeeklyLabel,
            CodexTaskbarWeeklyPercent,
            CodexTaskbarWeeklyResetText,
            CodexTaskbarWeeklyCountdown,
            ClaudeTaskbarLabel,
            ClaudeTaskbarPercent,
            ClaudeTaskbarCountdownPrefix,
            ClaudeTaskbarCountdown,
            ClaudeTaskbarWeeklyLabel,
            ClaudeTaskbarWeeklyPercent,
            ClaudeTaskbarWeeklyResetText,
            ClaudeTaskbarWeeklyCountdown,
        };
        foreach (var textBlock in textBlocks)
        {
            textBlock.FontFamily = fontFamily;
            textBlock.FontSize = fontSizeDip;
            textBlock.FontWeight = FontWeights.Normal;
            textBlock.Foreground = System.Windows.Media.Brushes.Black;
        }

        CodexTaskbarPercent.Visibility = _settings.TaskbarShowPercent
            ? Visibility.Visible
            : Visibility.Collapsed;
        ClaudeTaskbarPercent.Visibility = CodexTaskbarPercent.Visibility;
        CodexTaskbarCountdown.Visibility = _settings.TaskbarShowCountdown
            ? Visibility.Visible
            : Visibility.Collapsed;
        ClaudeTaskbarCountdown.Visibility = CodexTaskbarCountdown.Visibility;
        CodexTaskbarCountdownPrefix.Visibility = CodexTaskbarCountdown.Visibility;
        ClaudeTaskbarCountdownPrefix.Visibility = CodexTaskbarCountdown.Visibility;

        var weeklyVisibility = _settings.TaskbarShowWeeklyQuota
            ? Visibility.Visible
            : Visibility.Collapsed;
        CodexTaskbarWeeklyLabel.Visibility = weeklyVisibility;
        CodexTaskbarWeeklyPercent.Visibility = weeklyVisibility;
        CodexTaskbarWeeklyResetText.Visibility = weeklyVisibility;
        CodexTaskbarWeeklyCountdown.Visibility = weeklyVisibility;
        ClaudeTaskbarWeeklyLabel.Visibility = weeklyVisibility;
        ClaudeTaskbarWeeklyPercent.Visibility = weeklyVisibility;
        ClaudeTaskbarWeeklyResetText.Visibility = weeklyVisibility;
        ClaudeTaskbarWeeklyCountdown.Visibility = weeklyVisibility;

        TwoLinePercentColumn.Width = _settings.TaskbarShowPercent
            ? GridLength.Auto
            : new GridLength(0);
        TwoLineCountdownColumn.Width = _settings.TaskbarShowCountdown
            ? GridLength.Auto
            : new GridLength(0);
        TwoLineCountdownPrefixColumn.Width = TwoLineCountdownColumn.Width;
        TwoLineWeeklyLabelColumn.Width = _settings.TaskbarShowWeeklyQuota
            ? GridLength.Auto
            : new GridLength(0);
        TwoLineWeeklyPercentColumn.Width = TwoLineWeeklyLabelColumn.Width;
        TwoLineWeeklyCountdownTextColumn.Width = TwoLineWeeklyLabelColumn.Width;
        TwoLineWeeklyCountdownColumn.Width = TwoLineWeeklyLabelColumn.Width;

        var hasFirstItem = !string.IsNullOrEmpty(_settings.TaskbarCodexLabel)
            || !string.IsNullOrEmpty(_settings.TaskbarClaudeLabel)
            || _settings.TaskbarShowPercent;
        TwoLinePrimaryGapColumn.Width = hasFirstItem && _settings.TaskbarShowCountdown
            ? new GridLength(_settings.TaskbarItemSpacing)
            : new GridLength(0);
        var hasPrimaryGroup = hasFirstItem || _settings.TaskbarShowCountdown;
        TwoLineWeeklyGapColumn.Width = hasPrimaryGroup && _settings.TaskbarShowWeeklyQuota
            ? new GridLength(_settings.TaskbarItemSpacing)
            : new GridLength(0);
        TwoLineWeeklyCountdownGapColumn.Width = _settings.TaskbarShowWeeklyQuota
            ? new GridLength(_settings.TaskbarItemSpacing)
            : new GridLength(0);
        TwoLineTextRoot.Margin = new Thickness(
            _settings.TaskbarItemSpacing,
            0,
            _settings.TaskbarItemSpacing,
            0);

        var typeface = new Typeface(fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var percentMinWidth = _settings.TaskbarShowPercent
            ? MeasureText("100%", typeface, fontSizeDip)
            : 0;
        var countdownMinWidth = _settings.TaskbarShowCountdown
            ? MeasureText("00:00", typeface, fontSizeDip)
            : 0;
        var weeklyPercentMinWidth = _settings.TaskbarShowWeeklyQuota
            ? MeasureText("100%", typeface, fontSizeDip)
            : 0;
        var weeklyCountdownMinWidth = _settings.TaskbarShowWeeklyQuota
            ? MeasureText("7d00:00", typeface, fontSizeDip)
            : 0;
        CodexTaskbarPercent.MinWidth = percentMinWidth;
        ClaudeTaskbarPercent.MinWidth = percentMinWidth;
        CodexTaskbarCountdown.MinWidth = countdownMinWidth;
        ClaudeTaskbarCountdown.MinWidth = countdownMinWidth;
        CodexTaskbarWeeklyPercent.MinWidth = weeklyPercentMinWidth;
        ClaudeTaskbarWeeklyPercent.MinWidth = weeklyPercentMinWidth;
        CodexTaskbarWeeklyCountdown.MinWidth = weeklyCountdownMinWidth;
        ClaudeTaskbarWeeklyCountdown.MinWidth = weeklyCountdownMinWidth;

        var topOffset = -_settings.TaskbarVerticalMargin / 2d;
        var bottomOffset = _settings.TaskbarVerticalMargin / 2d;
        CodexTaskbarLabel.RenderTransform = new TranslateTransform(0, topOffset);
        CodexTaskbarPercent.RenderTransform = new TranslateTransform(0, topOffset);
        CodexTaskbarCountdownPrefix.RenderTransform = new TranslateTransform(0, topOffset);
        CodexTaskbarCountdown.RenderTransform = new TranslateTransform(0, topOffset);
        CodexTaskbarWeeklyLabel.RenderTransform = new TranslateTransform(0, topOffset);
        CodexTaskbarWeeklyPercent.RenderTransform = new TranslateTransform(0, topOffset);
        CodexTaskbarWeeklyResetText.RenderTransform = new TranslateTransform(0, topOffset);
        CodexTaskbarWeeklyCountdown.RenderTransform = new TranslateTransform(0, topOffset);
        ClaudeTaskbarLabel.RenderTransform = new TranslateTransform(0, bottomOffset);
        ClaudeTaskbarPercent.RenderTransform = new TranslateTransform(0, bottomOffset);
        ClaudeTaskbarCountdownPrefix.RenderTransform = new TranslateTransform(0, bottomOffset);
        ClaudeTaskbarCountdown.RenderTransform = new TranslateTransform(0, bottomOffset);
        ClaudeTaskbarWeeklyLabel.RenderTransform = new TranslateTransform(0, bottomOffset);
        ClaudeTaskbarWeeklyPercent.RenderTransform = new TranslateTransform(0, bottomOffset);
        ClaudeTaskbarWeeklyResetText.RenderTransform = new TranslateTransform(0, bottomOffset);
        ClaudeTaskbarWeeklyCountdown.RenderTransform = new TranslateTransform(0, bottomOffset);

        Height = 32;
        TwoLineTextRoot.Measure(new System.Windows.Size(double.PositiveInfinity, 32));
        Width = Math.Max(1, Math.Ceiling(TwoLineTextRoot.DesiredSize.Width));

        if (IsLoaded)
        {
            Dispatcher.BeginInvoke(EnsurePlacement, DispatcherPriority.Loaded);
        }
    }

    private double MeasureText(string text, Typeface typeface, double fontSizeDip)
    {
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var formattedText = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            typeface,
            fontSizeDip,
            System.Windows.Media.Brushes.Black,
            pixelsPerDip);
        return Math.Ceiling(formattedText.WidthIncludingTrailingWhitespace);
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (_placementService.IsEmbedded(this))
        {
            OpenRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        var startLeft = Left;
        var startTop = Top;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        var moved = Math.Abs(Left - startLeft) >= SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(Top - startTop) >= SystemParameters.MinimumVerticalDragDistance;
        if (!moved)
        {
            OpenRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        var result = _placementService.Place(
            this,
            preferredRatio: null,
            desiredLeftDip: Left,
            verticalOffsetDip: _settings.TaskbarWindowOffsetTop);
        _settings.TaskbarPositionRatio = result.PositionRatio;
        _settingsStore.Save(_settings);
    }

    private void OpenMenuItem_Click(object sender, RoutedEventArgs e) =>
        OpenRequested?.Invoke(this, EventArgs.Empty);

    private void RefreshMenuItem_Click(object sender, RoutedEventArgs e) =>
        RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void AutoPlaceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _settings.TaskbarPositionRatio = null;
        _settingsStore.Save(_settings);
        EnsurePlacement();
    }

    private void HideMenuItem_Click(object sender, RoutedEventArgs e) =>
        HideRequested?.Invoke(this, EventArgs.Empty);

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e) =>
        ExitRequested?.Invoke(this, EventArgs.Empty);
}
