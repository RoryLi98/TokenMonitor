using System.Text.Json;
using TokenMonitor.Core.Models;

namespace TokenMonitor.Core.Services;

public sealed class ClaudeDesktopQuotaProvider : IQuotaProvider
{
    private const string OverrideEnvironmentVariable = "TOKENMONITOR_CLAUDE_USAGE_PATH";
    private static readonly TimeSpan FiveHourWindow = TimeSpan.FromHours(5);
    private static readonly byte[] ResetFieldName = "resetsAt"u8.ToArray();
    private static readonly byte[] FiveHourMarker = "five_hour"u8.ToArray();
    private const byte V8DoubleTag = 0x4e;
    private const long MaximumIndexedDbFileSize = 32 * 1024 * 1024;
    private const int ResetContextRadius = 512;
    private const double MinimumResetDropPercent = 20;
    private const double MaximumPostResetUsedPercent = 25;

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
            var exactReset = FindExactResetFromClaudeCache(DateTimeOffset.Now);
            var reset = exactReset ?? EstimateReset(samples);
            return new QuotaSnapshot(
                Id,
                DisplayName,
                latest.UsedPercent,
                FiveHourWindow,
                reset,
                exactReset is null,
                latest.ObservedAt,
                exactReset is null ? SourceName : "Claude Desktop 本地缓存");
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
            var drop = samples[index - 1].UsedPercent - samples[index].UsedPercent;
            if (drop >= MinimumResetDropPercent &&
                samples[index].UsedPercent <= MaximumPostResetUsedPercent)
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

        // The first low-usage sample after a clear drop is the observed reset boundary.
        // Do not subtract the polling interval: doing so shifts every estimate early by
        // roughly five minutes even when Desktop recorded the reset itself.
        return samples[segmentStart].ObservedAt + FiveHourWindow;
    }

    private static DateTimeOffset? FindExactResetFromClaudeCache(DateTimeOffset now)
    {
        // Claude Desktop stores the server-provided five-hour window in Chromium's
        // IndexedDB structured-clone data. A numeric value is encoded as the field
        // name, the V8 double tag (0x4e), then an IEEE-754 little-endian double.
        // This is internal app storage, so keep the history-based estimate as a
        // fallback in case Claude changes the format.
        var earliestPlausibleReset = now - TimeSpan.FromMinutes(10);
        var latestPlausibleReset = now + FiveHourWindow + TimeSpan.FromMinutes(15);
        var candidates = new List<ResetCandidate>();

        foreach (var dataDirectory in FindClaudeDataDirectories())
        {
            var indexedDbDirectory = Path.Combine(dataDirectory, "IndexedDB");
            if (!Directory.Exists(indexedDbDirectory))
            {
                continue;
            }

            foreach (var file in EnumerateIndexedDbDataFiles(indexedDbDirectory)
                         .OrderByDescending(file => file.LastWriteTimeUtc)
                         .Take(32))
            {
                if (file.Length <= 0 || file.Length > MaximumIndexedDbFileSize)
                {
                    continue;
                }

                try
                {
                    using var stream = new FileStream(
                        file.FullName,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    var bytes = new byte[checked((int)stream.Length)];
                    stream.ReadExactly(bytes);

                    FindResetCandidates(
                        bytes,
                        file.LastWriteTimeUtc,
                        earliestPlausibleReset,
                        latestPlausibleReset,
                        candidates);
                }
                catch (IOException)
                {
                    // Claude may rotate IndexedDB files while a refresh is in progress.
                }
                catch (UnauthorizedAccessException)
                {
                    // Another data directory may still be readable.
                }
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.FileModifiedAt)
            .ThenBy(candidate => candidate.ResetAt < now)
            .ThenBy(candidate => Math.Abs((candidate.ResetAt - now).TotalSeconds))
            .Select(candidate => (DateTimeOffset?)candidate.ResetAt)
            .FirstOrDefault();
    }

    private static void FindResetCandidates(
        byte[] bytes,
        DateTime fileModifiedAtUtc,
        DateTimeOffset earliestPlausibleReset,
        DateTimeOffset latestPlausibleReset,
        ICollection<ResetCandidate> candidates)
    {
        var searchOffset = 0;
        while (searchOffset <= bytes.Length - ResetFieldName.Length - 9)
        {
            var relativeOffset = bytes.AsSpan(searchOffset).IndexOf(ResetFieldName);
            if (relativeOffset < 0)
            {
                break;
            }

            var fieldOffset = searchOffset + relativeOffset;
            var tagOffset = fieldOffset + ResetFieldName.Length;
            searchOffset = fieldOffset + ResetFieldName.Length;

            if (bytes[tagOffset] != V8DoubleTag)
            {
                continue;
            }

            var contextStart = Math.Max(0, fieldOffset - ResetContextRadius);
            var contextEnd = Math.Min(bytes.Length, tagOffset + 9 + ResetContextRadius);
            if (bytes.AsSpan(contextStart, contextEnd - contextStart).IndexOf(FiveHourMarker) < 0)
            {
                continue;
            }

            var unixSeconds = BitConverter.ToDouble(bytes, tagOffset + 1);
            if (!double.IsFinite(unixSeconds) ||
                unixSeconds < earliestPlausibleReset.ToUnixTimeSeconds() - 1 ||
                unixSeconds > latestPlausibleReset.ToUnixTimeSeconds() + 1)
            {
                continue;
            }

            DateTimeOffset resetAt;
            try
            {
                resetAt = DateTimeOffset.FromUnixTimeSeconds((long)Math.Round(unixSeconds));
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }

            if (resetAt >= earliestPlausibleReset && resetAt <= latestPlausibleReset)
            {
                candidates.Add(new ResetCandidate(resetAt, fileModifiedAtUtc));
            }
        }
    }

    private static IEnumerable<FileInfo> EnumerateIndexedDbDataFiles(string indexedDbDirectory)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(indexedDbDirectory, "*", SearchOption.AllDirectories)
                .Where(path =>
                    path.Contains("indexeddb.blob", StringComparison.OrdinalIgnoreCase) ||
                    (path.Contains("indexeddb.leveldb", StringComparison.OrdinalIgnoreCase) &&
                     (path.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                      path.EndsWith(".ldb", StringComparison.OrdinalIgnoreCase))))
                .ToArray();
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (var path in files)
        {
            FileInfo file;
            try
            {
                file = new FileInfo(path);
                _ = file.Length;
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            yield return file;
        }
    }

    private static string? FindUsageHistoryPath()
    {
        var overridePath = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        foreach (var dataDirectory in FindClaudeDataDirectories())
        {
            var candidate = Path.Combine(dataDirectory, "plan-usage-history.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> FindClaudeDataDirectories()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packagesRoot = Path.Combine(localAppData, "Packages");
        if (Directory.Exists(packagesRoot))
        {
            IEnumerable<string> packages;
            try
            {
                packages = Directory.EnumerateDirectories(packagesRoot, "Claude_*").ToArray();
            }
            catch (UnauthorizedAccessException)
            {
                packages = [];
            }
            catch (IOException)
            {
                packages = [];
            }

            foreach (var package in packages)
            {
                var dataDirectory = Path.Combine(package, "LocalCache", "Roaming", "Claude");
                if (Directory.Exists(dataDirectory))
                {
                    yield return dataDirectory;
                }
            }
        }

        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var unpackagedDirectory = Path.Combine(roamingAppData, "Claude");
        if (Directory.Exists(unpackagedDirectory))
        {
            yield return unpackagedDirectory;
        }
    }

    private sealed record UsageSample(DateTimeOffset ObservedAt, double UsedPercent);
    private sealed record ResetCandidate(DateTimeOffset ResetAt, DateTime FileModifiedAt);
}
