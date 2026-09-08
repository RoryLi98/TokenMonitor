using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TokenMonitor.Core.Models;

namespace TokenMonitor.Core.Services;

public sealed class CodexQuotaProvider : IQuotaProvider, IAsyncDisposable
{
    private const string OverrideEnvironmentVariable = "TOKENMONITOR_CODEX_PATH";
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private Process? _process;
    private StreamWriter? _input;
    private StreamReader? _output;
    private long _nextRequestId;
    private string? _lastStandardError;

    public string Id => "codex";
    public string DisplayName => "Codex";
    public string SourceName => "Codex app-server";

    public async Task<QuotaSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureStartedAsync(cancellationToken);
            var result = await SendRequestAsync("account/rateLimits/read", new { }, cancellationToken);
            var rateLimits = SelectRateLimitSnapshot(result);
            if (rateLimits is null)
            {
                return QuotaSnapshot.Unavailable(Id, DisplayName, SourceName, "Codex 没有返回可用的额度窗口");
            }

            var primary = ReadWindow(rateLimits.Value, "primary");
            if (primary is null)
            {
                return QuotaSnapshot.Unavailable(Id, DisplayName, SourceName, "Codex 没有返回5小时额度");
            }

            var secondary = ReadWindow(rateLimits.Value, "secondary");
            return new QuotaSnapshot(
                Id,
                DisplayName,
                primary.Value.UsedPercent,
                primary.Value.Duration,
                primary.Value.ResetAt,
                false,
                DateTimeOffset.Now,
                SourceName,
                secondary?.UsedPercent,
                secondary?.ResetAt);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await StopProcessAsync();
            return QuotaSnapshot.Unavailable(Id, DisplayName, SourceName, "Codex 响应超时");
        }
        catch (Exception exception)
        {
            var details = string.IsNullOrWhiteSpace(_lastStandardError)
                ? exception.Message
                : $"{exception.Message} ({_lastStandardError})";
            await StopProcessAsync();
            return QuotaSnapshot.Unavailable(Id, DisplayName, SourceName, details);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false } && _input is not null && _output is not null)
        {
            return;
        }

        await StopProcessAsync();
        var startInfo = new ProcessStartInfo
        {
            FileName = FindCodexExecutable(),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                _lastStandardError = args.Data.Length > 180 ? args.Data[..180] : args.Data;
            }
        };

        if (!_process.Start())
        {
            throw new InvalidOperationException("无法启动 codex app-server");
        }

        _process.BeginErrorReadLine();
        _input = _process.StandardInput;
        _output = _process.StandardOutput;

        await SendRequestAsync(
            "initialize",
            new
            {
                clientInfo = new
                {
                    name = "token-monitor",
                    title = "TokenMonitor",
                    version = "0.1.0",
                },
                capabilities = new
                {
                    optOutNotificationMethods = new[] { "thread/started", "thread/status/changed" },
                },
            },
            cancellationToken);
        await SendNotificationAsync("initialized", cancellationToken);
    }

    private async Task<JsonElement> SendRequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        if (_input is null || _output is null)
        {
            throw new InvalidOperationException("Codex app-server 尚未初始化");
        }

        var requestId = Interlocked.Increment(ref _nextRequestId);
        var payload = JsonSerializer.Serialize(new { id = requestId, method, @params = parameters });
        await _input.WriteLineAsync(payload.AsMemory(), cancellationToken);
        await _input.FlushAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        while (true)
        {
            var line = await _output.ReadLineAsync(timeout.Token);
            if (line is null)
            {
                throw new IOException("Codex app-server 已结束输出");
            }

            using var message = JsonDocument.Parse(line);
            var root = message.RootElement;
            if (!root.TryGetProperty("id", out var idElement) ||
                idElement.ValueKind != JsonValueKind.Number ||
                !idElement.TryGetInt64(out var responseId) ||
                responseId != requestId)
            {
                continue;
            }

            if (root.TryGetProperty("error", out var error))
            {
                var errorMessage = error.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : error.GetRawText();
                throw new InvalidOperationException(errorMessage ?? "Codex 返回未知错误");
            }

            if (!root.TryGetProperty("result", out var result))
            {
                throw new InvalidOperationException("Codex 响应缺少 result");
            }

            return result.Clone();
        }
    }

    private async Task SendNotificationAsync(string method, CancellationToken cancellationToken)
    {
        if (_input is null)
        {
            throw new InvalidOperationException("Codex app-server 尚未初始化");
        }

        var payload = JsonSerializer.Serialize(new { method });
        await _input.WriteLineAsync(payload.AsMemory(), cancellationToken);
        await _input.FlushAsync(cancellationToken);
    }

    private static JsonElement? SelectRateLimitSnapshot(JsonElement result)
    {
        if (result.TryGetProperty("rateLimits", out var rateLimits) && rateLimits.ValueKind == JsonValueKind.Object)
        {
            return rateLimits;
        }

        if (result.TryGetProperty("rateLimitsByLimitId", out var buckets) && buckets.ValueKind == JsonValueKind.Object)
        {
            foreach (var bucket in buckets.EnumerateObject())
            {
                if (bucket.Value.ValueKind == JsonValueKind.Object)
                {
                    return bucket.Value;
                }
            }
        }

        return null;
    }

    private static RateWindow? ReadWindow(JsonElement rateLimits, string propertyName)
    {
        if (!rateLimits.TryGetProperty(propertyName, out var window) || window.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!window.TryGetProperty("usedPercent", out var usedElement) || !usedElement.TryGetDouble(out var usedPercent))
        {
            return null;
        }

        DateTimeOffset? resetAt = null;
        if (window.TryGetProperty("resetsAt", out var resetElement) && resetElement.TryGetInt64(out var resetTimestamp))
        {
            resetAt = resetTimestamp > 9_999_999_999
                ? DateTimeOffset.FromUnixTimeMilliseconds(resetTimestamp)
                : DateTimeOffset.FromUnixTimeSeconds(resetTimestamp);
        }

        TimeSpan? duration = null;
        if (window.TryGetProperty("windowDurationMins", out var durationElement) &&
            durationElement.TryGetInt64(out var durationMinutes))
        {
            duration = TimeSpan.FromMinutes(durationMinutes);
        }

        return new RateWindow(Math.Clamp(usedPercent, 0d, 100d), duration, resetAt);
    }

    private static string FindCodexExecutable()
    {
        var overridePath = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var bundledCandidate = Path.Combine(localAppData, "OpenAI", "Codex", "bin", "codex.exe");
        if (File.Exists(bundledCandidate))
        {
            return bundledCandidate;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(folder, "codex.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "codex.exe";
    }

    private async Task StopProcessAsync()
    {
        var process = _process;
        _process = null;

        try
        {
            _input?.Dispose();
            _output?.Dispose();
        }
        catch
        {
            // Best-effort cleanup.
        }
        finally
        {
            _input = null;
            _output = null;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(timeout.Token);
            }
        }
        catch
        {
            // The child may already have exited.
        }
        finally
        {
            process.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _requestGate.WaitAsync();
        try
        {
            await StopProcessAsync();
        }
        finally
        {
            _requestGate.Release();
            _requestGate.Dispose();
        }
    }

    private readonly record struct RateWindow(double UsedPercent, TimeSpan? Duration, DateTimeOffset? ResetAt);
}
