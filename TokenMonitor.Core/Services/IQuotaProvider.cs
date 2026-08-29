using TokenMonitor.Core.Models;

namespace TokenMonitor.Core.Services;

public interface IQuotaProvider
{
    string Id { get; }
    string DisplayName { get; }
    string SourceName { get; }

    Task<QuotaSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}
