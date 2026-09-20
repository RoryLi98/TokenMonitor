using System.Windows;
using TokenMonitor.App.Infrastructure;

namespace TokenMonitor.App;

public partial class TaskbarSettingsWindow : Window
{
    private readonly AppSettings _settings;
    private bool _restoreDefaultsRequested;

    internal TaskbarSettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        LoadValues();
    }

    private void LoadValues()
    {
        CodexLabelTextBox.Text = _settings.TaskbarCodexLabel;
        ClaudeLabelTextBox.Text = _settings.TaskbarClaudeLabel;
        CodexCountdownPrefixTextBox.Text = _settings.TaskbarCodexCountdownPrefix;
        ClaudeCountdownPrefixTextBox.Text = _settings.TaskbarClaudeCountdownPrefix;
        ShowPercentCheckBox.IsChecked = _settings.TaskbarShowPercent;
        ShowCountdownCheckBox.IsChecked = _settings.TaskbarShowCountdown;
        ShowWeeklyQuotaCheckBox.IsChecked = _settings.TaskbarShowWeeklyQuota;
        CodexWeeklyRemainingTextBox.Text = _settings.TaskbarCodexWeeklyRemainingText;
        CodexWeeklyResetTextBox.Text = _settings.TaskbarCodexWeeklyResetText;
        ClaudeWeeklyRemainingTextBox.Text =
            _settings.TaskbarClaudeWeeklyRemainingText + _settings.TaskbarClaudeWeeklyPercentPlaceholder;
        ClaudeWeeklyResetTextBox.Text =
            _settings.TaskbarClaudeWeeklyResetText + _settings.TaskbarClaudeWeeklyCountdownPlaceholder;
        FontFamilyTextBox.Text = _settings.TaskbarFontFamily;
        FontSizeTextBox.Text = _settings.TaskbarFontSizePoints.ToString();
        ItemSpacingTextBox.Text = _settings.TaskbarItemSpacing.ToString();
        VerticalMarginTextBox.Text = _settings.TaskbarVerticalMargin.ToString();
        WindowOffsetTopTextBox.Text = _settings.TaskbarWindowOffsetTop.ToString();
        LockTaskbarCheckBox.IsChecked = _settings.TaskbarLocked;
    }

    private void RestoreDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();
        CodexLabelTextBox.Text = defaults.TaskbarCodexLabel;
        ClaudeLabelTextBox.Text = defaults.TaskbarClaudeLabel;
        CodexCountdownPrefixTextBox.Text = defaults.TaskbarCodexCountdownPrefix;
        ClaudeCountdownPrefixTextBox.Text = defaults.TaskbarClaudeCountdownPrefix;
        ShowPercentCheckBox.IsChecked = defaults.TaskbarShowPercent;
        ShowCountdownCheckBox.IsChecked = defaults.TaskbarShowCountdown;
        ShowWeeklyQuotaCheckBox.IsChecked = defaults.TaskbarShowWeeklyQuota;
        CodexWeeklyRemainingTextBox.Text = defaults.TaskbarCodexWeeklyRemainingText;
        CodexWeeklyResetTextBox.Text = defaults.TaskbarCodexWeeklyResetText;
        ClaudeWeeklyRemainingTextBox.Text = defaults.TaskbarClaudeWeeklyRemainingText;
        ClaudeWeeklyResetTextBox.Text = defaults.TaskbarClaudeWeeklyResetText;
        FontFamilyTextBox.Text = defaults.TaskbarFontFamily;
        FontSizeTextBox.Text = defaults.TaskbarFontSizePoints.ToString();
        ItemSpacingTextBox.Text = defaults.TaskbarItemSpacing.ToString();
        VerticalMarginTextBox.Text = defaults.TaskbarVerticalMargin.ToString();
        WindowOffsetTopTextBox.Text = defaults.TaskbarWindowOffsetTop.ToString();
        LockTaskbarCheckBox.IsChecked = defaults.TaskbarLocked;
        _restoreDefaultsRequested = true;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadNumber(FontSizeTextBox.Text, 5, 72, "字号", out var fontSize)
            || !TryReadNumber(ItemSpacingTextBox.Text, 0, 32, "项目间距", out var itemSpacing)
            || !TryReadNumber(VerticalMarginTextBox.Text, -10, 10, "垂直间距", out var verticalMargin)
            || !TryReadNumber(WindowOffsetTopTextBox.Text, -20, 20, "窗口顶部偏移", out var offsetTop))
        {
            return;
        }

        var codexLabel = CodexLabelTextBox.Text ?? string.Empty;
        var claudeLabel = ClaudeLabelTextBox.Text ?? string.Empty;
        var showPercent = ShowPercentCheckBox.IsChecked == true;
        var showCountdown = ShowCountdownCheckBox.IsChecked == true;
        var showWeeklyQuota = ShowWeeklyQuotaCheckBox.IsChecked == true;
        if (codexLabel.Length == 0 && claudeLabel.Length == 0 && !showPercent && !showCountdown && !showWeeklyQuota)
        {
            System.Windows.MessageBox.Show(this, "请至少保留一项显示内容。", "任务栏显示设置", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _settings.TaskbarCodexLabel = codexLabel;
        _settings.TaskbarClaudeLabel = claudeLabel;
        _settings.TaskbarCodexCountdownPrefix = CodexCountdownPrefixTextBox.Text ?? string.Empty;
        _settings.TaskbarClaudeCountdownPrefix = ClaudeCountdownPrefixTextBox.Text ?? string.Empty;
        _settings.TaskbarShowPercent = showPercent;
        _settings.TaskbarShowCountdown = showCountdown;
        _settings.TaskbarShowWeeklyQuota = showWeeklyQuota;
        _settings.TaskbarCodexWeeklyRemainingText = CodexWeeklyRemainingTextBox.Text ?? string.Empty;
        _settings.TaskbarCodexWeeklyResetText = CodexWeeklyResetTextBox.Text ?? string.Empty;
        _settings.TaskbarClaudeWeeklyRemainingText = ClaudeWeeklyRemainingTextBox.Text ?? string.Empty;
        _settings.TaskbarClaudeWeeklyResetText = ClaudeWeeklyResetTextBox.Text ?? string.Empty;
        _settings.TaskbarClaudeWeeklyPercentPlaceholder = string.Empty;
        _settings.TaskbarClaudeWeeklyCountdownPlaceholder = string.Empty;
        _settings.TaskbarFontFamily = string.IsNullOrWhiteSpace(FontFamilyTextBox.Text)
            ? "Segoe UI"
            : FontFamilyTextBox.Text.Trim();
        _settings.TaskbarFontSizePoints = fontSize;
        _settings.TaskbarItemSpacing = itemSpacing;
        _settings.TaskbarVerticalMargin = verticalMargin;
        _settings.TaskbarWindowOffsetTop = offsetTop;
        _settings.TaskbarLocked = LockTaskbarCheckBox.IsChecked == true;
        if (_restoreDefaultsRequested)
        {
            var defaults = new AppSettings();
            _settings.TaskbarPositionRatio = defaults.TaskbarPositionRatio;
            _settings.TaskbarMonitorDeviceName = defaults.TaskbarMonitorDeviceName;
        }
        DialogResult = true;
    }

    private bool TryReadNumber(string text, int min, int max, string name, out int value)
    {
        if (int.TryParse(text, out value) && value >= min && value <= max)
        {
            return true;
        }

        System.Windows.MessageBox.Show(
            this,
            $"{name}必须是 {min} 到 {max} 之间的整数。",
            "任务栏显示设置",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        return false;
    }
}
