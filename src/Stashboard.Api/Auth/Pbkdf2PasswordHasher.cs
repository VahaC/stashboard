using System.Security.Cryptography;

namespace Stashboard.Api.Auth;

/// <summary>
/// PBKDF2-HMAC-SHA256 password hasher. Produces a self-describing string in the form
/// <c>pbkdf2-sha256$&lt;iterations&gt;$&lt;saltBase64&gt;$&lt;hashBase64&gt;</c>.
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    /// <summary>OWASP-recommended (2023+) iteration count for PBKDF2-SHA256.</summary>
    public const int Iterations = 600_000;
    public const int SaltBytes = 16;
    public const int HashBytes = 32;
    public const string Algorithm = "pbkdf2-sha256";

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

        return $"{Algorithm}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string hash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(hash)) return false;

        var parts = hash.Split('$');
        if (parts.Length != 4) return false;
        if (!string.Equals(parts[0], Algorithm, StringComparison.Ordinal)) return false;
        if (!int.TryParse(parts[1], out var iter) || iter < 1) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException) { return false; }

        if (expected.Length == 0) return false;

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iter, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
