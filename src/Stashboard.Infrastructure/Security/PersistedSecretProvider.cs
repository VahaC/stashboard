namespace Stashboard.Infrastructure.Security;

/// <summary>
/// Reads a long-lived secret (the encryption key, the JWT signing secret) from a
/// file on the persisted data volume, generating and storing a fresh one on first
/// run. This lets a self-hosted deployment start with zero manual key management:
/// the secret is created once and then preserved untouched across image updates and
/// container recreation, so credentials encrypted at rest stay decryptable.
/// </summary>
public static class PersistedSecretProvider
{
    /// <summary>
    /// Returns the secret stored at <paramref name="directory"/>/<paramref name="fileName"/>,
    /// creating it from <paramref name="generate"/> when the file is absent or empty.
    /// On Unix the directory and file are written with owner-only permissions. The
    /// <c>Created</c> flag is <c>true</c> when a new value was generated this call.
    /// </summary>
    public static (string Value, bool Created) GetOrCreate(string directory, string fileName, Func<string> generate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(generate);

        var path = Path.Combine(directory, fileName);

        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (!string.IsNullOrWhiteSpace(existing))
                return (existing, false);
            // Empty or whitespace-only file (e.g. an interrupted first write) —
            // fall through and regenerate so the app can still start.
        }

        EnsureDirectory(directory);

        var value = generate();

        // Write to a temp file in the same directory, set permissions, then move it
        // into place. The move is atomic on a single filesystem, so a crash mid-write
        // can never leave a half-written secret that would later fail to decrypt data.
        var tempPath = Path.Combine(directory, $"{fileName}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, value);
        RestrictToOwner(tempPath);
        File.Move(tempPath, path, overwrite: true);

        return (value, true);
    }

    private static void EnsureDirectory(string directory)
    {
        if (Directory.Exists(directory))
            return;

        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
            Directory.CreateDirectory(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
