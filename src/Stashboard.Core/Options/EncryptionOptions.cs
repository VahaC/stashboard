namespace Stashboard.Core.Options;

public class EncryptionOptions
{
    public const string SectionName = "Encryption";

    /// <summary>Base64-encoded 32-byte AES key. Required in production.</summary>
    public string? Key { get; set; }
}
