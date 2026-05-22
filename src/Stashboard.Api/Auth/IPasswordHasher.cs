namespace Stashboard.Api.Auth;

/// <summary>
/// Hashes and verifies passwords. Implementations must store their own algorithm/parameter
/// metadata inside the resulting hash string so that it is fully self-describing and can
/// be migrated forward in the future without re-prompting users.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>Hashes a plain-text password. Returns a self-describing string.</summary>
    string Hash(string password);

    /// <summary>Verifies a plain-text password against a previously produced hash.</summary>
    bool Verify(string password, string hash);
}
