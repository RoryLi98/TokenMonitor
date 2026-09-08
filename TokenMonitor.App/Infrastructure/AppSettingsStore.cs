using System.IO;
using System.Text.Json;

namespace TokenMonitor.App.Infrastructure;

internal sealed class AppSettings
{
    public bool ShowTaskbarBar { get; set; } = true;
    public double? TaskbarPositionRatio { get; set; }
    public int RefreshIntervalSeconds { get; set; } = 60;
    public string TaskbarCodexLabel { get; set; } = "Codex";
    public string TaskbarClaudeLabel { get; set; } = "Claude";
    public string TaskbarCodexCountdownPrefix { get; set; } = string.Empty;
    public string TaskbarClaudeCountdownPrefix { get; set; } = "~";
    public bool TaskbarShowPercent { get; set; } = true;
    public bool TaskbarShowCountdown { get; set; } = true;
    public bool TaskbarShowWeeklyQuota { get; set; } = true;
    public string TaskbarCodexWeeklyRemainingText { get; set; } = null!;
    public string TaskbarCodexWeeklyResetText { get; set; } = null!;
    public string TaskbarClaudeWeeklyRemainingText { get; set; } = null!;
    public string TaskbarClaudeWeeklyResetText { get; set; } = null!;
    // Kept for loading settings written by older builds.
    public string TaskbarWeeklyLabel { get; set; } = "周 ";
    public string TaskbarClaudeWeeklyPercentPlaceholder { get; set; } = "--";
    public string TaskbarClaudeWeeklyCountdownPlaceholder { get; set; } = "--:--";
    public string TaskbarFontFamily { get; set; } = "Segoe UI";
    public int TaskbarFontSizePoints { get; set; } = 9;
    public int TaskbarItemSpacing { get; set; }
    public int TaskbarVerticalMargin { get; set; }
    public int TaskbarWindowOffsetTop { get; set; }
}

internal sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _settingsPath;

    public AppSettingsStore()
    {
        // Keep one portable settings file beside the executable. LocalAppData can be
        // transparently redirected when TokenMonitor is launched by a packaged app
        // (for example Codex Desktop), causing Explorer launches to see another file.
        _settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
        TryMigrateLegacySettings();
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                var defaults = new AppSettings();
                defaults.TaskbarCodexWeeklyRemainingText = defaults.TaskbarWeeklyLabel;
                defaults.TaskbarCodexWeeklyResetText = string.Empty;
                defaults.TaskbarClaudeWeeklyRemainingText = defaults.TaskbarWeeklyLabel;
                defaults.TaskbarClaudeWeeklyResetText = string.Empty;
                return defaults;
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath))
                ?? new AppSettings();
            if (settings.TaskbarPositionRatio is < 0 or > 1)
            {
                settings.TaskbarPositionRatio = null;
            }

            if (settings.RefreshIntervalSeconds is not (30 or 60 or 120 or 300))
            {
                settings.RefreshIntervalSeconds = 60;
            }

            settings.TaskbarCodexLabel ??= "Codex";
            settings.TaskbarClaudeLabel ??= "Claude";
            settings.TaskbarCodexCountdownPrefix ??= string.Empty;
            settings.TaskbarClaudeCountdownPrefix ??= "~";
            settings.TaskbarWeeklyLabel ??= "周 ";
            settings.TaskbarCodexWeeklyRemainingText ??= settings.TaskbarWeeklyLabel;
            settings.TaskbarCodexWeeklyResetText ??= string.Empty;
            settings.TaskbarClaudeWeeklyRemainingText ??= settings.TaskbarWeeklyLabel;
            settings.TaskbarClaudeWeeklyResetText ??= string.Empty;
            settings.TaskbarClaudeWeeklyPercentPlaceholder ??= "--";
            settings.TaskbarClaudeWeeklyCountdownPlaceholder ??= "--:--";
            if (string.IsNullOrWhiteSpace(settings.TaskbarFontFamily))
            {
                settings.TaskbarFontFamily = "Segoe UI";
            }

            settings.TaskbarFontSizePoints = Math.Clamp(settings.TaskbarFontSizePoints, 5, 72);
            settings.TaskbarItemSpacing = Math.Clamp(settings.TaskbarItemSpacing, 0, 32);
            settings.TaskbarVerticalMargin = Math.Clamp(settings.TaskbarVerticalMargin, -10, 10);
            settings.TaskbarWindowOffsetTop = Math.Clamp(settings.TaskbarWindowOffsetTop, -20, 20);

            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // Settings persistence must never prevent the monitor from running.
        }
    }

    private void TryMigrateLegacySettings()
    {
        if (File.Exists(_settingsPath))
        {
            return;
        }

        try
        {
            var candidates = new List<string>();
            var localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(Path.Combine(localAppData, "TokenMonitor", "settings.json"));

            var packagesDirectory = Path.Combine(localAppData, "Packages");
            if (Directory.Exists(packagesDirectory))
            {
                foreach (var codexPackage in Directory.EnumerateDirectories(
                    packagesDirectory,
                    "OpenAI.Codex_*",
                    SearchOption.TopDirectoryOnly))
                {
                    candidates.Add(Path.Combine(
                        codexPackage,
                        "LocalCache",
                        "Local",
                        "TokenMonitor",
                        "settings.json"));
                }
            }

            var source = candidates
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (source is null)
            {
                return;
            }

            var directory = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(directory);
            File.Copy(source, _settingsPath, overwrite: false);
        }
        catch
        {
            // If migration is unavailable, Load will use defaults and Save can retry later.
        }
    }
}
