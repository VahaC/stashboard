using System.Net.Http.Json;

namespace Stashboard.Api.Notifications;

/// <inheritdoc cref="IAppriseSender"/>
public sealed class AppriseSender(HttpClient httpClient) : IAppriseSender
{
    public async Task SendAsync(
        string baseUrl,
        IReadOnlyList<string> urls,
        string title,
        string body,
        AppriseNotificationType type,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        if (urls.Count == 0) throw new ArgumentException("At least one Apprise URL is required.", nameof(urls));

        using var response = await httpClient.PostAsJsonAsync(
            NotifyEndpoint(baseUrl),
            new
            {
                // Apprise accepts a comma-delimited list and fans out to every target.
                urls = string.Join(",", urls),
                title,
                body,
                type = TypeString(type),
            },
            cancellationToken);

        // Apprise returns 2xx only when every target accepted the payload (424 when
        // one fails). EnsureSuccess => caller leaves its throttle key unstamped and
        // retries on the next tick.
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Normalises the configured base URL to the Apprise stateless notify endpoint:
    /// strips a trailing slash and appends <c>/notify</c> unless the URL already
    /// targets it (so both <c>http://apprise:8000</c> and
    /// <c>http://apprise:8000/notify</c> work).
    /// </summary>
    internal static string NotifyEndpoint(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        var path = trimmed.Contains("://", StringComparison.Ordinal)
            ? trimmed[(trimmed.IndexOf("://", StringComparison.Ordinal) + 3)..]
            : trimmed;
        var segment = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : "";
        return segment.Equals("notify", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{trimmed}/notify";
    }

    private static string TypeString(AppriseNotificationType type) => type switch
    {
        AppriseNotificationType.Success => "success",
        AppriseNotificationType.Warning => "warning",
        AppriseNotificationType.Failure => "failure",
        _ => "info",
    };
}
