using Moq;
using Stashboard.Api.Contracts;
using Stashboard.Api.Mapping;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Mapping;

/// <summary>
/// Unit tests for <see cref="ProxmoxConnectionMapper"/> — tri-state secret
/// handling for the API token + SSH key, schedule-field clamping, and the
/// decrypted profile projection. Encryption is mocked so encrypted payloads
/// stay predictable, mirroring <see cref="DockerConnectionMapperTests"/>.
/// </summary>
public class ProxmoxConnectionMapperTests
{
    private readonly Mock<IEncryptionService> _encryption = new();
    private readonly ProxmoxConnectionMapper _mapper;

    public ProxmoxConnectionMapperTests()
    {
        _encryption.Setup(e => e.Encrypt(It.IsAny<string>())).Returns<string>(v => $"enc:{v}");
        _encryption.Setup(e => e.Decrypt(It.IsAny<string>()))
            .Returns<string>(v => v.StartsWith("enc:") ? v[4..] : v);
        _mapper = new ProxmoxConnectionMapper(_encryption.Object);
    }

    [Fact]
    public void ApplyUpsert_SetSecrets_EncryptsThem()
    {
        var entity = new ProxmoxConnectionEntity();

        _mapper.ApplyUpsert(entity, Request(
            apiTokenSecret: new SecretValueUpsert(SecretValueAction.Set, "tok-secret"),
            sshPrivateKey: new SecretValueUpsert(SecretValueAction.Set, "PEMKEY")));

        Assert.Equal("enc:tok-secret", entity.ApiTokenSecretEncrypted);
        Assert.Equal("enc:PEMKEY", entity.SshPrivateKeyEncrypted);
        Assert.Equal("pve", entity.NodeName);
        Assert.Equal("root@pam!stash", entity.ApiTokenId);
    }

    [Fact]
    public void ApplyUpsert_KeepSecret_PreservesExistingEncryptedValue()
    {
        var entity = new ProxmoxConnectionEntity { ApiTokenSecretEncrypted = "enc:original" };

        _mapper.ApplyUpsert(entity, Request(apiTokenSecret: new SecretValueUpsert(SecretValueAction.Keep, null)));

        Assert.Equal("enc:original", entity.ApiTokenSecretEncrypted);
    }

    [Fact]
    public void ApplyUpsert_ClearSecret_NullsTheColumn()
    {
        var entity = new ProxmoxConnectionEntity { SshPrivateKeyEncrypted = "enc:key" };

        _mapper.ApplyUpsert(entity, Request(sshPrivateKey: new SecretValueUpsert(SecretValueAction.Clear, null)));

        Assert.Null(entity.SshPrivateKeyEncrypted);
    }

    [Fact]
    public void ServerType_RoundTrips_ThroughUpsertResponseAndProfile()
    {
        var entity = new ProxmoxConnectionEntity { ApiTokenSecretEncrypted = "enc:secret" };

        _mapper.ApplyUpsert(entity, Request() with { ServerType = ProxmoxServerType.Pbs });
        Assert.Equal(ProxmoxServerType.Pbs, entity.ServerType);

        Assert.Equal(ProxmoxServerType.Pbs, _mapper.ToResponse(entity, []).ServerType);
        Assert.Equal(ProxmoxServerType.Pbs, _mapper.BuildProfile(entity).ServerType);
    }

    [Fact]
    public void ServerType_DefaultsToPve()
    {
        var entity = new ProxmoxConnectionEntity();
        _mapper.ApplyUpsert(entity, Request());
        Assert.Equal(ProxmoxServerType.Pve, entity.ServerType);
    }

    [Fact]
    public void ApplyUpsert_InvalidHourlyValue_FallsBackTo24()
    {
        var entity = new ProxmoxConnectionEntity();

        _mapper.ApplyUpsert(entity, Request(checkEveryHours: 5));

        Assert.Equal(24, entity.CheckEveryHours);
    }

    [Fact]
    public void ApplyUpsert_HourlySchedule_ClearsDailyWeeklyFields()
    {
        var entity = new ProxmoxConnectionEntity
        {
            CheckAtTime = new TimeOnly(3, 0),
            CheckOnDayOfWeek = DayOfWeek.Monday,
        };

        _mapper.ApplyUpsert(entity, Request(scheduleType: CheckScheduleType.Hourly));

        Assert.Null(entity.CheckAtTime);
        Assert.Null(entity.CheckOnDayOfWeek);
    }

    [Fact]
    public void BuildProfile_FromEntity_DecryptsSecretsAndBuildsSsh()
    {
        var entity = new ProxmoxConnectionEntity
        {
            ApiBaseUrl = "https://pve.lan:8006",
            NodeName = "pve",
            ApiTokenId = "root@pam!stash",
            ApiTokenSecretEncrypted = "enc:tok",
            SkipTlsVerify = true,
            SshHost = "pve.lan",
            SshPort = 2200,
            SshUsername = "root",
            SshPrivateKeyEncrypted = "enc:PEM",
        };

        var profile = _mapper.BuildProfile(entity);

        Assert.Equal("tok", profile.ApiTokenSecret);
        Assert.NotNull(profile.Ssh);
        Assert.Equal("pve.lan", profile.Ssh!.Host);
        Assert.Equal(2200, profile.Ssh.Port);
        Assert.Equal("PEM", profile.Ssh.PrivateKeyPem);
    }

    [Fact]
    public void BuildProfile_FromEntity_NoSshKey_YieldsNullSsh()
    {
        var entity = new ProxmoxConnectionEntity
        {
            ApiBaseUrl = "https://pve.lan:8006",
            NodeName = "pve",
            ApiTokenId = "root@pam!stash",
            SshHost = "pve.lan",
            SshUsername = "root",
            // no private key
        };

        var profile = _mapper.BuildProfile(entity);

        Assert.Null(profile.Ssh);
    }

    [Fact]
    public void ApplyUpsert_RoundTripsAllowConsoleFlag()
    {
        var entity = new ProxmoxConnectionEntity();

        _mapper.ApplyUpsert(entity, Request(allowConsole: true));
        Assert.True(entity.AllowConsole);

        _mapper.ApplyUpsert(entity, Request(allowConsole: false));
        Assert.False(entity.AllowConsole);
    }

    [Fact]
    public void ToResponse_SurfacesAllowConsole()
    {
        var entity = new ProxmoxConnectionEntity
        {
            Name = "home-pve",
            ApiBaseUrl = "https://pve.lan:8006",
            NodeName = "pve",
            ApiTokenId = "root@pam!stash",
            AllowConsole = true,
        };

        var response = _mapper.ToResponse(entity, []);

        Assert.True(response.AllowConsole);
    }

    [Fact]
    public void ToResponse_NeverLeaksSecrets_OnlyPresenceFlags()
    {
        var entity = new ProxmoxConnectionEntity
        {
            Name = "home-pve",
            ApiBaseUrl = "https://pve.lan:8006",
            NodeName = "pve",
            ApiTokenId = "root@pam!stash",
            ApiTokenSecretEncrypted = "enc:tok",
            SshPrivateKeyEncrypted = "enc:PEM",
            SshPrivateKeyPassphraseEncrypted = "enc:pass",
        };
        var guests = new List<ProxmoxGuestEntity>
        {
            new() { VmId = 105, Name = "pihole", GuestType = ProxmoxGuestType.Lxc, IsRunning = true, PendingUpdates = 7 },
            new() { VmId = 0, Name = "pve", GuestType = ProxmoxGuestType.Node, IsRunning = true, PendingUpdates = 3 },
        };

        var response = _mapper.ToResponse(entity, guests);

        Assert.True(response.HasApiTokenSecret);
        Assert.True(response.HasSshPrivateKey);
        Assert.True(response.HasSshPrivateKeyPassphrase);
        // Node ordered before LXC.
        Assert.Equal(ProxmoxGuestType.Node, response.Guests[0].GuestType);
        Assert.Equal(105, response.Guests[1].VmId);
    }

    [Theory]
    [InlineData(20, 20)]     // in-range value kept
    [InlineData(3, 5)]       // below the floor → clamped up to 5
    [InlineData(600, 300)]   // above the ceiling → clamped down to 300
    public void ApplyUpsert_TelemetryPoll_IsClamped(int input, int expected)
    {
        var entity = new ProxmoxConnectionEntity();
        _mapper.ApplyUpsert(entity, Request(telemetryPollSeconds: input));
        Assert.Equal(expected, entity.TelemetryPollSeconds);
    }

    [Fact]
    public void ApplyUpsert_TelemetryPoll_NullResetsToDefault()
    {
        var entity = new ProxmoxConnectionEntity { TelemetryPollSeconds = 45 };
        _mapper.ApplyUpsert(entity, Request(telemetryPollSeconds: null));
        Assert.Null(entity.TelemetryPollSeconds);
    }

    [Fact]
    public void ToResponse_SurfacesTelemetryPoll()
    {
        var entity = new ProxmoxConnectionEntity
        {
            Name = "home-pve",
            ApiBaseUrl = "https://pve.lan:8006",
            NodeName = "pve",
            ApiTokenId = "root@pam!stash",
            TelemetryPollSeconds = 30,
        };
        Assert.Equal(30, _mapper.ToResponse(entity, []).TelemetryPollSeconds);
    }

    private static ProxmoxConnectionUpsertRequest Request(
        SecretValueUpsert? apiTokenSecret = null,
        SecretValueUpsert? sshPrivateKey = null,
        int checkEveryHours = 24,
        CheckScheduleType scheduleType = CheckScheduleType.Hourly,
        bool allowConsole = false,
        int? telemetryPollSeconds = null) =>
        new(
            Name: "home-pve",
            ApiBaseUrl: "https://pve.lan:8006",
            NodeName: "pve",
            ApiTokenId: "root@pam!stash",
            ApiTokenSecret: apiTokenSecret,
            SshHost: "pve.lan",
            SshUsername: "root",
            SshPrivateKey: sshPrivateKey,
            AllowConsole: allowConsole,
            ScheduleType: scheduleType,
            CheckEveryHours: checkEveryHours,
            TelemetryPollSeconds: telemetryPollSeconds);
}


