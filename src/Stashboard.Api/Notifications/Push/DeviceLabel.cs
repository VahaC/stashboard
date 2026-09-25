namespace Stashboard.Api.Notifications.Push;

/// <summary>
/// V10.6 — derives a short, human-readable device label ("Chrome on Android") from a
/// browser User-Agent so the Settings device list is legible. Best-effort and non-exhaustive;
/// falls back to a generic label when the UA can't be classified.
/// </summary>
public static class DeviceLabel
{
    public static string? FromUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return null;

        var browser = Browser(userAgent);
        var os = Os(userAgent);
        return (browser, os) switch
        {
            (not null, not null) => $"{browser} on {os}",
            (not null, null) => browser,
            (null, not null) => os,
            _ => "Browser",
        };
    }

    private static string? Browser(string ua)
    {
        // Order matters: Edge/Opera/Chrome all contain "Chrome"; Chrome contains "Safari".
        if (ua.Contains("Edg", StringComparison.OrdinalIgnoreCase)) return "Edge";
        if (ua.Contains("OPR", StringComparison.OrdinalIgnoreCase) || ua.Contains("Opera", StringComparison.OrdinalIgnoreCase)) return "Opera";
        if (ua.Contains("Firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        if (ua.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (ua.Contains("Safari", StringComparison.OrdinalIgnoreCase)) return "Safari";
        return null;
    }

    private static string? Os(string ua)
    {
        if (ua.Contains("Android", StringComparison.OrdinalIgnoreCase)) return "Android";
        if (ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) || ua.Contains("iPad", StringComparison.OrdinalIgnoreCase)) return "iOS";
        if (ua.Contains("Windows", StringComparison.OrdinalIgnoreCase)) return "Windows";
        if (ua.Contains("Mac OS", StringComparison.OrdinalIgnoreCase) || ua.Contains("Macintosh", StringComparison.OrdinalIgnoreCase)) return "macOS";
        if (ua.Contains("Linux", StringComparison.OrdinalIgnoreCase)) return "Linux";
        return null;
    }
}
