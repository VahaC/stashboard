using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Stashboard.Api;

namespace Stashboard.Tests.Infrastructure.Security;

/// <summary>
/// V10.6 — the VAPID key pair is auto-generated once on first run and then reused on
/// every restart, exactly like the encryption key and JWT secret. Both keys are
/// generated together (a matched pair) and never regenerated independently.
/// </summary>
public sealed class VapidProvisioningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"stashboard-vapid-{Guid.NewGuid():N}");

    [Fact]
    public void AddPersistedSecrets_GeneratesVapidPairOnce_AndReusesOnRestart()
    {
        var (pub1, priv1) = Provision();

        Assert.False(string.IsNullOrWhiteSpace(pub1));
        Assert.False(string.IsNullOrWhiteSpace(priv1));
        Assert.NotEqual(pub1, priv1);
        // Stored as one file holding the matched pair (two lines), never split apart.
        Assert.Equal(2, File.ReadAllLines(Path.Combine(_dir, "vapid.keys")).Length);

        // A "restart" against the same secrets directory reuses the very same pair.
        var (pub2, priv2) = Provision();
        Assert.Equal(pub1, pub2);
        Assert.Equal(priv1, priv2);
    }

    private (string? Public, string? Private) Provision()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Stashboard:SecretsPath"] = _dir,
        });
        SecretProvisioning.AddPersistedSecrets(builder);
        return (builder.Configuration["Vapid:PublicKey"], builder.Configuration["Vapid:PrivateKey"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
