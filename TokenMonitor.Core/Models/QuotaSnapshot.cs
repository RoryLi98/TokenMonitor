namespace TokenMonitor.Core.Models;

public sealed record QuotaSnapshot(
    string ProviderId,
    string DisplayName,
    double? UsedPercent,
    TimeSpan? WindowDuration,
    DateTimeOffset? ResetAt,
    bool ResetIsEstimated,
    DateTimeOffset ObservedAt,
    string Source,
    double? SecondaryUsedPercent = null,
    DateTimeOffset? SecondaryResetAt = null,
    string? Error = null)
{
    public bool IsAvailable => UsedPercent is not null && Error is null;

    public double? RemainingPercent => UsedPercent is null
        ? null
        : Math.Clamp(100d - UsedPercent.Value, 0d, 100d);

    public double? SecondaryRemainingPercent => SecondaryUsedPercent is null
        ? null
        : Math.Clamp(100d - SecondaryUsedPercent.Value, 0d, 100d);

    public static QuotaSnapshot Unavailable(
        string providerId,
        string displayName,
        string source,
        string error) =>
        new(
            providerId,
            displayName,
            null,
            null,
            null,
            false,
            DateTimeOffset.Now,
            source,
            Error: error);
}
