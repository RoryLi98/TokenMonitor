using System.Text;
using System.Text.Json;
using TokenMonitor.Core.Models;
using TokenMonitor.Core.Services;

Console.OutputEncoding = Encoding.UTF8;

if (args.Contains("--window-diagnostics", StringComparer.OrdinalIgnoreCase))
{
    TaskbarWindowDiagnostics.WriteToConsole();
    return;
}

var providers = new IQuotaProvider[]
{
    new CodexQuotaProvider(),
    new ClaudeDesktopQuotaProvider(),
};

try
{
    var snapshots = await Task.WhenAll(providers.Select(ReadSafelyAsync));
    foreach (var snapshot in snapshots)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            provider = snapshot.DisplayName,
            available = snapshot.IsAvailable,
            usedPercent = snapshot.UsedPercent,
            remainingPercent = snapshot.RemainingPercent,
            resetAt = snapshot.ResetAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"),
            secondaryRemainingPercent = snapshot.SecondaryRemainingPercent,
            secondaryResetAt = snapshot.SecondaryResetAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"),
            resetEstimated = snapshot.ResetIsEstimated,
            observedAt = snapshot.ObservedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"),
            source = snapshot.Source,
            error = snapshot.Error,
        }));
    }
}
finally
{
    foreach (var provider in providers)
    {
        if (provider is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
    }
}

static async Task<QuotaSnapshot> ReadSafelyAsync(IQuotaProvider provider)
{
    try
    {
        return await provider.GetSnapshotAsync();
    }
    catch (Exception exception)
    {
        return QuotaSnapshot.Unavailable(provider.Id, provider.DisplayName, provider.SourceName, exception.Message);
    }
}
