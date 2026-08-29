using System.Text.Json;
using TokenMonitor.Core.Models;

namespace TokenMonitor.Core.Services;

public sealed class ClaudeDesktopQuotaProvider : IQuotaProvider
{
    private const string OverrideEnvironmentVariable = "TOKENMONITOR_CLAUDE_USAGE_PATH";
    private static readonly TimeSpan FiveHourWindow = TimeSpan.FromHours(5);
    private static readonly TimeSpan DefaultSampleInterval = TimeSpan.FromMinutes(5);

    public string Id => "claude";
    public string DisplayName => "Claude";
    public string SourceName => "Claude Desktop 历史";

    public async Task<QuotaSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var path = FindUsageHistoryPath();
        if (path is null)
        {
            return QuotaSnapshot.Unavailable(
                Id,
                DisplayName,
                SourceName,
                "未找到 Claude Desktop 的额度历史文件");
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var samples = ParseSamples(document.RootElement);
            if (samples.Count == 0)
            {
                return QuotaSnapshot.Unavailable(Id, DisplayName, SourceName, "额度历史文件中没有有效样本");
            }

            var latest = samples[^1];
            var estimatedReset = EstimateReset(samples);
            return new QuotaSnapshot(
                Id,
                DisplayName,
                latest.UsedPercent,
                FiveHourWindow,
                estimatedReset,
                true,
                latest.ObservedAt,
                SourceName);
        }
        catch (UnauthorizedAccessException)
        {
            return QuotaSnapshot.Unavailable(Id, DisplayName, SourceName, "没有权限读取 Claude Desktop 数据目录");
        }
        catch (IOException exception)
        {
            return QuotaSnapshot.Unavailable(Id, DisplayName, SourceName, $"读取失败：{exception.Message}");
        }
        catch (JsonException)
        {
            return QuotaSnapshot.Unavailable(Id, DisplayName, SourceName, "Claude 额度历史文件格式暂时无法识别");
        }
    }

    private static List<UsageSample> ParseSamples(JsonElement root)
    {
        var result = new List<UsageSample>();
        if (!root.TryGetProperty("samples", out var samples) || samples.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var sample in samples.EnumerateArray())
        {
            if (!sample.TryGetProperty("t", out var timestampElement) ||
                !timestampElement.TryGetInt64(out var timestamp) ||
                !sample.TryGetProperty("u", out var usage) ||
                !usage.TryGetProperty("fh", out var usedElement) ||
                !usedElement.TryGetDouble(out var usedPercent))
            {
                continue;
            }

            var observedAt = timestamp > 9_999_999_999
                ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
                : DateTimeOffset.FromUnixTimeSeconds(timestamp);
            result.Add(new UsageSample(observedAt, Math.Clamp(usedPercent, 0d, 100d)));
        }

        result.Sort((left, right) => left.ObservedAt.CompareTo(right.ObservedAt));
        return result;
    }

    private static DateTimeOffset? EstimateReset(IReadOnlyList<UsageSample> samples)
    {
        if (samples.Count == 0)
        {
            return null;
        }

        var segmentStart = 0;
        for (var index = samples.Count - 1; index > 0; index--)
        {
            if (samples[index].UsedPercent + 0.01 < samples[index - 1].UsedPercent)
            {
                segmentStart = index;
                break;
            }
        }

        if (segmentStart == 0)
        {
            var cutoff = samples[^1].ObservedAt - FiveHourWindow;
            segmentStart = samples.ToList().FindIndex(sample => sample.ObservedAt >= cutoff);
            segmentStart = Math.Max(0, segmentStart);
        }

        var intervals = new List<TimeSpan>();
        for (var index = segmentStart + 1; index < samples.Count; index++)
        {
            var gap = samples[index].ObservedAt - samples[index - 1].ObservedAt;
            if (gap > TimeSpan.Zero && gap <= TimeSpan.FromMinutes(15))
            {
                intervals.Add(gap);
            }
        }

        var sampleInterval = intervals.Count == 0
            ? DefaultSampleInterval
            : intervals.OrderBy(interval => interval).ElementAt(intervals.Count / 2);
        sampleInterval = sampleInterval > DefaultSampleInterval ? DefaultSampleInterval : sampleInterval;

        return samples[segmentStart].ObservedAt - sampleInterval + FiveHourWindow;
    }

    private static string? FindUsageHistoryPath()
    {
        var overridePath = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packagesRoot = Path.Combine(localAppData, "Packages");
        if (Directory.Exists(packagesRoot))
        {
            try
            {
                foreach (var package in Directory.EnumerateDirectories(packagesRoot, "Claude_*"))
                {
                    var candidate = Path.Combine(
                        package,
                        "LocalCache",
                        "Roaming",
                        "Claude",
                        "plan-usage-history.json");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // The unpackaged fallback below may still be available.
            }
        }

        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var unpackagedCandidate = Path.Combine(roamingAppData, "Claude", "plan-usage-history.json");
        return File.Exists(unpackagedCandidate) ? unpackagedCandidate : null;
    }

    private sealed record UsageSample(DateTimeOffset ObservedAt, double UsedPercent);
}
