namespace Stashboard.Core.Abstractions;

public interface IBackupService
{
    /// <summary>Exports the user's services, categories, tags, and credentials as a JSON byte array.</summary>
    Task<byte[]> ExportAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Imports services from a JSON stream. Returns the number of services imported.</summary>
    Task<int> ImportAsync(Guid userId, Stream jsonStream, CancellationToken cancellationToken = default);
}
