namespace Stashboard.Core.Abstractions;

public interface IFaviconService
{
    /// <summary>Returns a fully-qualified URL pointing to the favicon for the given site, or null if it cannot be resolved.</summary>
    Task<string?> ResolveFaviconUrlAsync(string siteUrl, CancellationToken cancellationToken = default);

    /// <summary>Invalidates any cached favicon resolution for the given site URL.</summary>
    void InvalidateSiteFaviconCache(string siteUrl);
}
