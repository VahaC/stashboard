using Moq;
using Stashboard.Api.Contracts;
using Stashboard.Api.Mapping;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Infrastructure.Docker;

namespace Stashboard.Tests.Mapping;

/// <summary>
/// Unit tests for <see cref="DockerWatchMapper"/>. Host transport (TLS, host
/// URL) lives on the sibling <see cref="DockerConnectionEntity"/> and is
/// covered by <see cref="DockerConnectionMapperTests"/> — this file only
/// exercises the per-container concerns: image-reference parsing, registry
/// credential tri-state, and the join with a connection when building a
/// profile.
/// </summary>
public class DockerWatchMapperTests
{
    private readonly Mock<IEncryptionService> _encryption = new();
    private readonly DockerWatchMapper _mapper;

    public DockerWatchMapperTests()
    {
        _encryption.Setup(e => e.Encrypt(It.IsAny<string>())).Returns<string>(v => $"enc:{v}");
        _encryption.Setup(e => e.Decrypt(It.IsAny<string>()))
            .Returns<string>(v => v.StartsWith("enc:") ? v[4..] : v);
        _mapper = new DockerWatchMapper(_encryption.Object, new ImageReferenceParser());
    }

    // ── ToResponse ───────────────────────────────────────────────────────────

    [Fact]
    public void ToResponse_FullyPopulatedEntity_SurfacesAllFieldsAndPresenceFlags()
    {
        var entity = SampleEntity(withRegistryCreds: true);

        var response = _mapper.ToResponse(entity);

        Assert.Equal(entity.Id, response.Id);
        Assert.Equal(entity.WebResourceId, response.WebResourceId);
        Assert.Equal("ghcr.io/owner/repo:v1", response.ImageReference);
        Assert.Equal("ghcr.io", response.RegistryHost);
        Assert.Equal("owner/repo", response.Repository);
        Assert.Equal("v1", response.Tag);
        Assert.Equal("svc", response.ContainerName);
        Assert.True(response.HasRegistryCredentials);
        Assert.Equal(CheckScheduleType.Hourly, response.ScheduleType);
        Assert.Equal(24, response.CheckEveryHours);
    }

    [Fact]
    public void ToResponse_EntityWithoutSecrets_FlagsAreFalse()
    {
        var entity = SampleEntity(withRegistryCreds: false);

        var response = _mapper.ToResponse(entity);

        Assert.False(response.HasRegistryCredentials);
    }

    [Fact]
    public void ToResponse_NeverIncludesDecryptedSecretValues()
    {
        var entity = SampleEntity(withRegistryCreds: true);
        var serialized = System.Text.Json.JsonSerializer.Serialize(_mapper.ToResponse(entity));
        Assert.DoesNotContain("registry-user", serialized);
        Assert.DoesNotContain("registry-pass", serialized);
    }

    [Fact]
    public void ToResponse_NullWebhookToken_SurfacesAsNull()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        // No WebhookToken set on the entity.
        var response = _mapper.ToResponse(entity);

        Assert.Null(response.WebhookToken);
        Assert.Null(response.LastWebhookReceivedUtc);
    }

    [Fact]
    public void ToResponse_PopulatedWebhookFields_RoundTripVerbatim()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        var receivedAt = new DateTime(2026, 5, 18, 9, 0, 0, DateTimeKind.Utc);
        entity.WebhookToken = new string('c', 64);
        entity.LastWebhookReceivedUtc = receivedAt;

        var response = _mapper.ToResponse(entity);

        Assert.Equal(new string('c', 64), response.WebhookToken);
        Assert.Equal(receivedAt, response.LastWebhookReceivedUtc);
    }

    // ── ApplyUpsert (image-reference parsing + plain fields) ─────────────────

    [Fact]
    public void ApplyUpsert_ParsesImageReferenceIntoRegistryRepoTag()
    {
        var entity = new DockerWatchEntity { ContainerName = "x", ImageReference = "x", RegistryHost = "x", Repository = "x", Tag = "x" };
        var request = UpsertRequest(imageReference: "linuxserver/sonarr:4.0");

        _mapper.ApplyUpsert(entity, request);

        Assert.Equal("docker.io", entity.RegistryHost);
        Assert.Equal("linuxserver/sonarr", entity.Repository);
        Assert.Equal("4.0", entity.Tag);
        Assert.Equal("linuxserver/sonarr:4.0", entity.ImageReference);
    }

    [Fact]
    public void ApplyUpsert_MalformedImageReference_Throws()
    {
        var entity = new DockerWatchEntity { ContainerName = "x", ImageReference = "x", RegistryHost = "x", Repository = "x", Tag = "x" };
        var request = UpsertRequest(imageReference: "@@@bad");

        Assert.Throws<FormatException>(() => _mapper.ApplyUpsert(entity, request));
    }

    [Fact]
    public void ApplyUpsert_TrimsImageReferenceAndContainerName()
    {
        var entity = new DockerWatchEntity { ContainerName = "x", ImageReference = "x", RegistryHost = "x", Repository = "x", Tag = "x" };
        var request = UpsertRequest(imageReference: "  nginx:1.27  ", containerName: "  web  ");

        _mapper.ApplyUpsert(entity, request);

        Assert.Equal("nginx:1.27", entity.ImageReference);
        Assert.Equal("web", entity.ContainerName);
    }

    [Fact]
    public void ApplyUpsert_UpdatesUpdatedUtc()
    {
        var entity = new DockerWatchEntity
        {
            ContainerName = "x", ImageReference = "x", RegistryHost = "x", Repository = "x", Tag = "x",
            UpdatedUtc = DateTime.UtcNow.AddDays(-7),
        };
        var before = entity.UpdatedUtc;

        _mapper.ApplyUpsert(entity, UpsertRequest());

        Assert.True(entity.UpdatedUtc > before);
    }

    // ── ApplyUpsert (notification channel toggles) ───────────────────────────

    [Fact]
    public void ApplyUpsert_TelegramNotificationsEnabledTrue_AppliedToEntity()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        var request = UpsertRequest(telegramNotificationsEnabled: true);

        _mapper.ApplyUpsert(entity, request);

        Assert.True(entity.TelegramNotificationsEnabled);
    }

    [Fact]
    public void ApplyUpsert_TelegramNotificationsEnabledFalse_AppliedToEntity()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        entity.TelegramNotificationsEnabled = true;
        var request = UpsertRequest(telegramNotificationsEnabled: false);

        _mapper.ApplyUpsert(entity, request);

        Assert.False(entity.TelegramNotificationsEnabled);
    }

    [Fact]
    public void ToResponse_SurfacesTelegramNotificationsEnabled()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        entity.TelegramNotificationsEnabled = true;

        var response = _mapper.ToResponse(entity);

        Assert.True(response.TelegramNotificationsEnabled);
    }

    // ── ApplyUpsert (tri-state registry credential handling) ────────────────

    [Fact]
    public void ApplyUpsert_KeepAction_LeavesExistingEncryptedValueAlone()
    {
        var entity = SampleEntity(withRegistryCreds: true);
        var existingUser = entity.RegistryUsernameEncrypted;

        var request = UpsertRequest(
            registryUsername: new SecretValueUpsert(SecretValueAction.Keep, null));

        _mapper.ApplyUpsert(entity, request);

        Assert.Equal(existingUser, entity.RegistryUsernameEncrypted);
    }

    [Fact]
    public void ApplyUpsert_NullSecretUpsert_IsTreatedAsKeep()
    {
        var entity = SampleEntity(withRegistryCreds: true);

        _mapper.ApplyUpsert(entity, UpsertRequest());

        Assert.Equal("enc:registry-user", entity.RegistryUsernameEncrypted);
        Assert.Equal("enc:registry-pass", entity.RegistryPasswordEncrypted);
    }

    [Fact]
    public void ApplyUpsert_SetAction_EncryptsAndStoresNewValue()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        var request = UpsertRequest(
            registryUsername: new SecretValueUpsert(SecretValueAction.Set, "new-user"),
            registryPassword: new SecretValueUpsert(SecretValueAction.Set, "new-pass"));

        _mapper.ApplyUpsert(entity, request);

        Assert.Equal("enc:new-user", entity.RegistryUsernameEncrypted);
        Assert.Equal("enc:new-pass", entity.RegistryPasswordEncrypted);
    }

    [Fact]
    public void ApplyUpsert_SetActionWithEmptyValue_StoresNull()
    {
        var entity = SampleEntity(withRegistryCreds: true);
        var request = UpsertRequest(
            registryUsername: new SecretValueUpsert(SecretValueAction.Set, ""));

        _mapper.ApplyUpsert(entity, request);

        Assert.Null(entity.RegistryUsernameEncrypted);
    }

    [Fact]
    public void ApplyUpsert_ClearAction_DropsTheStoredValue()
    {
        var entity = SampleEntity(withRegistryCreds: true);
        var request = UpsertRequest(
            registryUsername: new SecretValueUpsert(SecretValueAction.Clear, null),
            registryPassword: new SecretValueUpsert(SecretValueAction.Clear, null));

        _mapper.ApplyUpsert(entity, request);

        Assert.Null(entity.RegistryUsernameEncrypted);
        Assert.Null(entity.RegistryPasswordEncrypted);
    }

    // ── BuildProfileFromEntity (joins watch + connection) ───────────────────

    [Fact]
    public void BuildProfileFromEntity_DecryptsTlsFromConnectionAndCredentialsFromWatch()
    {
        var entity = SampleEntity(withRegistryCreds: true);
        var connection = SampleConnection(withTls: true);

        var profile = _mapper.BuildProfileFromEntity(entity, connection);

        Assert.Equal("ghcr.io", profile.RegistryHost);
        Assert.Equal("owner/repo", profile.Repository);
        Assert.Equal("v1", profile.Tag);
        Assert.Equal(DockerHostType.TcpTls, profile.HostType);
        Assert.Equal("tcp://docker.local:2376", profile.HostUrl);
        Assert.NotNull(profile.Tls);
        Assert.Equal("ca-pem", profile.Tls!.CaCert);
        Assert.Equal("cert-pem", profile.Tls.ClientCert);
        Assert.Equal("key-pem", profile.Tls.ClientKey);
        Assert.NotNull(profile.RegistryCredentials);
        Assert.Equal("registry-user", profile.RegistryCredentials!.Username);
        Assert.Equal("registry-pass", profile.RegistryCredentials.Password);
    }

    [Fact]
    public void BuildProfileFromEntity_NoSecrets_ProfileHasNulls()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        var connection = SampleConnection(withTls: false);

        var profile = _mapper.BuildProfileFromEntity(entity, connection);

        Assert.Null(profile.Tls);
        Assert.Null(profile.RegistryCredentials);
    }

    [Fact]
    public void BuildProfileFromEntity_PartialCredentials_ReturnsNull()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        entity.RegistryUsernameEncrypted = "enc:user";
        var connection = SampleConnection(withTls: false);

        var profile = _mapper.BuildProfileFromEntity(entity, connection);

        Assert.Null(profile.RegistryCredentials);
    }

    // ── V2.7 BuildUpdateProfile ──────────────────────────────────────────────

    [Fact]
    public void BuildUpdateProfile_PullsContainerAndImageFromWatch_HostTransportFromConnection()
    {
        var entity = SampleEntity(withRegistryCreds: true);
        var connection = SampleConnection(withTls: true);

        var profile = _mapper.BuildUpdateProfile(entity, connection);

        Assert.Equal("ghcr.io/owner/repo:v1", profile.ImageReference);
        Assert.Equal("ghcr.io", profile.RegistryHost);
        Assert.Equal("owner/repo", profile.Repository);
        Assert.Equal("v1", profile.Tag);
        Assert.Equal("svc", profile.ContainerName);
        Assert.Equal(DockerHostType.TcpTls, profile.HostTransport.HostType);
        Assert.Equal("tcp://docker.local:2376", profile.HostTransport.HostUrl);
        Assert.NotNull(profile.HostTransport.Tls);
        Assert.NotNull(profile.RegistryCredentials);
        Assert.Equal("registry-user", profile.RegistryCredentials!.Username);
    }

    [Fact]
    public void BuildUpdateProfile_AwsEcrEntity_DecryptsAwsKeysAndPassesRegion()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        entity.RegistryAuthType = RegistryAuthType.AwsEcr;
        entity.AwsAccessKeyIdEncrypted = "enc:akid";
        entity.AwsSecretAccessKeyEncrypted = "enc:secret";
        entity.AwsRegion = "eu-central-1";
        var connection = SampleConnection(withTls: false);

        var profile = _mapper.BuildUpdateProfile(entity, connection);

        Assert.Equal(RegistryAuthType.AwsEcr, profile.RegistryAuthType);
        Assert.Equal("akid", profile.AwsAccessKeyId);
        Assert.Equal("secret", profile.AwsSecretAccessKey);
        Assert.Equal("eu-central-1", profile.AwsRegion);
    }

    [Fact]
    public void ToResponse_AttemptEntity_RoundTripsAllFields()
    {
        var entity = new DockerUpdateAttemptEntity
        {
            Id = Guid.NewGuid(),
            DockerWatchId = Guid.NewGuid(),
            WebResourceId = Guid.NewGuid(),
            InitiatedByUserId = Guid.NewGuid(),
            Status = DockerUpdateAttemptStatus.Success,
            ImageReference = "ghcr.io/owner/repo:v1",
            ContainerName = "svc",
            PreviousDigest = "sha256:aaaa",
            NewDigest = "sha256:bbbb",
            CompletedUtc = new DateTime(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc),
            CreatedUtc = new DateTime(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc),
        };

        var response = _mapper.ToResponse(entity);

        Assert.Equal(entity.Id, response.Id);
        Assert.Equal(entity.DockerWatchId, response.DockerWatchId);
        Assert.Equal(entity.WebResourceId, response.WebResourceId);
        Assert.Equal(DockerUpdateAttemptStatus.Success, response.Status);
        Assert.Equal("ghcr.io/owner/repo:v1", response.ImageReference);
        Assert.Equal("svc", response.ContainerName);
        Assert.Equal("sha256:aaaa", response.PreviousDigest);
        Assert.Equal("sha256:bbbb", response.NewDigest);
        Assert.Equal(entity.CompletedUtc, response.CompletedUtc);
    }

    // ── BuildProfileFromTestRequest ──────────────────────────────────────────

    [Fact]
    public void BuildProfileFromTestRequest_KeepResolvesAgainstExistingWatchCreds()
    {
        var existing = SampleEntity(withRegistryCreds: true);
        var connection = SampleConnection(withTls: true);
        var request = TestRequest(
            registryUsername: new SecretValueUpsert(SecretValueAction.Keep, null),
            registryPassword: new SecretValueUpsert(SecretValueAction.Keep, null));

        var profile = _mapper.BuildProfileFromTestRequest(request, connection, existing);

        Assert.NotNull(profile.RegistryCredentials);
        Assert.Equal("registry-user", profile.RegistryCredentials!.Username);
        Assert.Equal("registry-pass", profile.RegistryCredentials.Password);
        // TLS comes off the connection regardless of the request.
        Assert.NotNull(profile.Tls);
        Assert.Equal("ca-pem", profile.Tls!.CaCert);
    }

    [Fact]
    public void BuildProfileFromTestRequest_NoExistingWatch_KeepProducesNoCreds()
    {
        var connection = SampleConnection(withTls: false);
        var request = TestRequest(
            registryUsername: new SecretValueUpsert(SecretValueAction.Keep, null),
            registryPassword: new SecretValueUpsert(SecretValueAction.Keep, null));

        var profile = _mapper.BuildProfileFromTestRequest(request, connection, existing: null);

        Assert.Null(profile.RegistryCredentials);
    }

    [Fact]
    public void BuildProfileFromTestRequest_ParsesImageReference()
    {
        var connection = SampleConnection(withTls: false);
        var request = TestRequest(imageReference: "ghcr.io/owner/repo:v2");

        var profile = _mapper.BuildProfileFromTestRequest(request, connection, existing: null);

        Assert.Equal("ghcr.io", profile.RegistryHost);
        Assert.Equal("owner/repo", profile.Repository);
        Assert.Equal("v2", profile.Tag);
    }

    [Fact]
    public void BuildProfileFromTestRequest_BothCredentialsProvided_BuildsCredentials()
    {
        var connection = SampleConnection(withTls: false);
        var request = TestRequest(
            registryUsername: new SecretValueUpsert(SecretValueAction.Set, "u"),
            registryPassword: new SecretValueUpsert(SecretValueAction.Set, "p"));

        var profile = _mapper.BuildProfileFromTestRequest(request, connection, existing: null);

        Assert.NotNull(profile.RegistryCredentials);
        Assert.Equal("u", profile.RegistryCredentials!.Username);
        Assert.Equal("p", profile.RegistryCredentials.Password);
    }

    // ── ApplyUpsert (V2.1 tag-pattern filter) ────────────────────────────────

    [Fact]
    public void ApplyUpsert_TagPatternFilter_TrimsAndPersists()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        var request = UpsertRequest(tagPatternFilter: "  ^v\\d+\\.\\d+\\.\\d+$  ");

        _mapper.ApplyUpsert(entity, request);

        Assert.Equal("^v\\d+\\.\\d+\\.\\d+$", entity.TagPatternFilter);
    }

    [Fact]
    public void ApplyUpsert_TagPatternFilter_BlankNormalizesToNull()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        entity.TagPatternFilter = "^old$";
        var request = UpsertRequest(tagPatternFilter: "   ");

        _mapper.ApplyUpsert(entity, request);

        Assert.Null(entity.TagPatternFilter);
    }

    [Fact]
    public void ApplyUpsert_TagPatternFilter_InvalidRegex_Throws()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        var request = UpsertRequest(tagPatternFilter: "[unclosed");

        Assert.Throws<FormatException>(() => _mapper.ApplyUpsert(entity, request));
    }

    [Fact]
    public void ToResponse_SurfacesTagPatternFilter()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        entity.TagPatternFilter = "^stable$";

        var response = _mapper.ToResponse(entity);

        Assert.Equal("^stable$", response.TagPatternFilter);
    }

    [Fact]
    public void BuildProfileFromEntity_PropagatesTagPatternFilter()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        entity.TagPatternFilter = "^v\\d+\\.\\d+\\.\\d+$";
        var connection = SampleConnection(withTls: false);

        var profile = _mapper.BuildProfileFromEntity(entity, connection);

        Assert.Equal("^v\\d+\\.\\d+\\.\\d+$", profile.TagPatternFilter);
    }

    [Fact]
    public void BuildProfileFromTestRequest_PropagatesTagPatternFilter()
    {
        var connection = SampleConnection(withTls: false);
        var request = TestRequest(tagPatternFilter: "^stable$");

        var profile = _mapper.BuildProfileFromTestRequest(request, connection, existing: null);

        Assert.Equal("^stable$", profile.TagPatternFilter);
    }

    // ── ApplyUpsert (V2.2 schedule) ──────────────────────────────────────────

    [Fact]
    public void ApplyUpsert_HourlySchedule_PersistsHoursAndClearsTimeOfDay()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        entity.CheckAtTime = new TimeOnly(8, 0);
        entity.CheckOnDayOfWeek = DayOfWeek.Monday;
        var request = UpsertRequest(scheduleType: CheckScheduleType.Hourly, checkEveryHours: 6);

        _mapper.ApplyUpsert(entity, request);

        Assert.Equal(CheckScheduleType.Hourly, entity.ScheduleType);
        Assert.Equal(6, entity.CheckEveryHours);
        Assert.Null(entity.CheckAtTime);
        Assert.Null(entity.CheckOnDayOfWeek);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(0)]
    [InlineData(48)]
    public void ApplyUpsert_HourlyScheduleWithDisallowedHours_Throws(int hours)
    {
        var entity = SampleEntity(withRegistryCreds: false);
        var request = UpsertRequest(scheduleType: CheckScheduleType.Hourly, checkEveryHours: hours);

        Assert.Throws<FormatException>(() => _mapper.ApplyUpsert(entity, request));
    }

    [Fact]
    public void ApplyUpsert_DailySchedule_PersistsTimeOfDayAndForcesEvery24h()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        var request = UpsertRequest(
            scheduleType: CheckScheduleType.Daily,
            checkAtTime: new TimeOnly(8, 0));

        _mapper.ApplyUpsert(entity, request);

        Assert.Equal(CheckScheduleType.Daily, entity.ScheduleType);
        Assert.Equal(new TimeOnly(8, 0), entity.CheckAtTime);
        Assert.Equal(24, entity.CheckEveryHours);
        Assert.Null(entity.CheckOnDayOfWeek);
    }

    [Fact]
    public void ApplyUpsert_DailyWithoutTime_Throws()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        var request = UpsertRequest(scheduleType: CheckScheduleType.Daily, checkAtTime: null);

        var ex = Assert.Throws<FormatException>(() => _mapper.ApplyUpsert(entity, request));
        Assert.Contains("CheckAtTime", ex.Message);
    }

    [Fact]
    public void ApplyUpsert_WeeklySchedule_PersistsDayAndTime()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        var request = UpsertRequest(
            scheduleType: CheckScheduleType.Weekly,
            checkAtTime: new TimeOnly(7, 15),
            checkOnDayOfWeek: DayOfWeek.Sunday);

        _mapper.ApplyUpsert(entity, request);

        Assert.Equal(CheckScheduleType.Weekly, entity.ScheduleType);
        Assert.Equal(new TimeOnly(7, 15), entity.CheckAtTime);
        Assert.Equal(DayOfWeek.Sunday, entity.CheckOnDayOfWeek);
    }

    [Fact]
    public void ApplyUpsert_WeeklyWithoutDayOfWeek_Throws()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        var request = UpsertRequest(
            scheduleType: CheckScheduleType.Weekly,
            checkAtTime: new TimeOnly(7, 15),
            checkOnDayOfWeek: null);

        Assert.Throws<FormatException>(() => _mapper.ApplyUpsert(entity, request));
    }

    [Fact]
    public void ToResponse_PopulatesNextCheckUtcFromSchedule()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        entity.ScheduleType = CheckScheduleType.Hourly;
        entity.CheckEveryHours = 4;
        entity.LastCheckedUtc = DateTime.UtcNow.AddHours(-1);

        var response = _mapper.ToResponse(entity);

        Assert.NotNull(response.NextCheckUtc);
        // Next check should be ~3 h ahead of "now" (last check was 1 h ago,
        // every 4 h).
        var expected = entity.LastCheckedUtc.Value.AddHours(4);
        Assert.True(Math.Abs((expected - response.NextCheckUtc!.Value).TotalSeconds) < 2,
            $"NextCheckUtc {response.NextCheckUtc} should equal {expected}");
    }

    // ── ApplyUpsert (V2.3 GitHub PAT) ────────────────────────────────────────

    [Fact]
    public void ApplyUpsert_GitHubPatSetAction_EncryptsAndStoresValue()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        var request = UpsertRequest(gitHubPat: new SecretValueUpsert(SecretValueAction.Set, "ghp_secret"));

        _mapper.ApplyUpsert(entity, request);

        Assert.Equal("enc:ghp_secret", entity.GitHubPatEncrypted);
    }

    [Fact]
    public void ApplyUpsert_GitHubPatKeepAction_PreservesExistingValue()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        entity.GitHubPatEncrypted = "enc:existing-pat";
        var request = UpsertRequest(gitHubPat: new SecretValueUpsert(SecretValueAction.Keep, null));

        _mapper.ApplyUpsert(entity, request);

        Assert.Equal("enc:existing-pat", entity.GitHubPatEncrypted);
    }

    [Fact]
    public void ApplyUpsert_GitHubPatClearAction_DropsStoredValue()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        entity.GitHubPatEncrypted = "enc:existing-pat";
        var request = UpsertRequest(gitHubPat: new SecretValueUpsert(SecretValueAction.Clear, null));

        _mapper.ApplyUpsert(entity, request);

        Assert.Null(entity.GitHubPatEncrypted);
    }

    [Fact]
    public void ToResponse_SurfacesHasGitHubPatFlagButNeverThePat()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        entity.GitHubPatEncrypted = "enc:ghp_supersecret";

        var response = _mapper.ToResponse(entity);
        var serialized = System.Text.Json.JsonSerializer.Serialize(response);

        Assert.True(response.HasGitHubPat);
        Assert.DoesNotContain("ghp_supersecret", serialized);
    }

    [Fact]
    public void ToResponse_SurfacesLatestReleaseUrlAndBody()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        entity.LatestReleaseUrl = "https://github.com/owner/repo/releases/tag/v1";
        entity.LatestReleaseBody = "## v1\n- first release";

        var response = _mapper.ToResponse(entity);

        Assert.Equal("https://github.com/owner/repo/releases/tag/v1", response.LatestReleaseUrl);
        Assert.Contains("first release", response.LatestReleaseBody);
    }

    [Fact]
    public void BuildProfileFromEntity_PropagatesDecryptedPat()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        entity.GitHubPatEncrypted = "enc:ghp_value";
        var connection = SampleConnection(withTls: false);

        var profile = _mapper.BuildProfileFromEntity(entity, connection);

        Assert.Equal("ghp_value", profile.GitHubPat);
    }

    [Fact]
    public void BuildProfileFromTestRequest_KeepResolvesPatAgainstExistingWatch()
    {
        var existing = SampleEntity(withRegistryCreds: false);
        existing.GitHubPatEncrypted = "enc:ghp_existing";
        var connection = SampleConnection(withTls: false);
        var request = TestRequest(gitHubPat: new SecretValueUpsert(SecretValueAction.Keep, null));

        var profile = _mapper.BuildProfileFromTestRequest(request, connection, existing);

        Assert.Equal("ghp_existing", profile.GitHubPat);
    }

    // ── ApplyUpsert (V2.4 registry auth) ─────────────────────────────────────

    [Fact]
    public void ApplyUpsert_NonEcrHostWithAutoStrategy_StaysAuto()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        var request = UpsertRequest(imageReference: "nginx:1.27");

        _mapper.ApplyUpsert(entity, request);

        Assert.Equal(RegistryAuthType.Auto, entity.RegistryAuthType);
        Assert.Null(entity.AwsAccessKeyIdEncrypted);
        Assert.Null(entity.AwsRegion);
    }

    [Fact]
    public void ApplyUpsert_EcrHostWithAutoStrategy_PromotedToAwsEcrAndRegionInferred()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        var request = UpsertRequest(
            imageReference: "123456789012.dkr.ecr.eu-central-1.amazonaws.com/my-app:1.0.0",
            awsAccessKeyId: new SecretValueUpsert(SecretValueAction.Set, "AKIA"),
            awsSecretAccessKey: new SecretValueUpsert(SecretValueAction.Set, "secret"));

        _mapper.ApplyUpsert(entity, request);

        Assert.Equal(RegistryAuthType.AwsEcr, entity.RegistryAuthType);
        Assert.Equal("enc:AKIA", entity.AwsAccessKeyIdEncrypted);
        Assert.Equal("enc:secret", entity.AwsSecretAccessKeyEncrypted);
        Assert.Equal("eu-central-1", entity.AwsRegion);
    }

    [Fact]
    public void ApplyUpsert_EcrHostWithExplicitBasicStrategy_RespectsTheOverride()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        var request = UpsertRequest(
            imageReference: "123456789012.dkr.ecr.eu-central-1.amazonaws.com/my-app:1.0.0",
            registryAuthType: RegistryAuthType.Basic,
            registryUsername: new SecretValueUpsert(SecretValueAction.Set, "u"),
            registryPassword: new SecretValueUpsert(SecretValueAction.Set, "p"));

        _mapper.ApplyUpsert(entity, request);

        Assert.Equal(RegistryAuthType.Basic, entity.RegistryAuthType);
        // AWS columns must be wiped when the strategy isn't AwsEcr so a stale
        // secret can't leak across registry switches.
        Assert.Null(entity.AwsAccessKeyIdEncrypted);
        Assert.Null(entity.AwsRegion);
    }

    [Fact]
    public void ApplyUpsert_SwitchingFromAwsEcrToAuto_ClearsAwsColumns()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        entity.RegistryAuthType = RegistryAuthType.AwsEcr;
        entity.AwsAccessKeyIdEncrypted = "enc:OLD";
        entity.AwsSecretAccessKeyEncrypted = "enc:OLD_SECRET";
        entity.AwsRegion = "us-east-1";

        var request = UpsertRequest(
            imageReference: "ghcr.io/owner/repo:latest",
            registryAuthType: RegistryAuthType.Auto);
        _mapper.ApplyUpsert(entity, request);

        Assert.Equal(RegistryAuthType.Auto, entity.RegistryAuthType);
        Assert.Null(entity.AwsAccessKeyIdEncrypted);
        Assert.Null(entity.AwsSecretAccessKeyEncrypted);
        Assert.Null(entity.AwsRegion);
    }

    [Fact]
    public void ApplyUpsert_AwsRegionProvidedExplicitly_OverridesHostInference()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        var request = UpsertRequest(
            imageReference: "123456789012.dkr.ecr.eu-central-1.amazonaws.com/my-app:1.0.0",
            registryAuthType: RegistryAuthType.AwsEcr,
            awsAccessKeyId: new SecretValueUpsert(SecretValueAction.Set, "AKIA"),
            awsSecretAccessKey: new SecretValueUpsert(SecretValueAction.Set, "secret"),
            awsRegion: "ap-southeast-2");

        _mapper.ApplyUpsert(entity, request);

        Assert.Equal("ap-southeast-2", entity.AwsRegion);
    }

    [Fact]
    public void ToResponse_SurfacesRegistryAuthAndHasAwsFlag_ButNeverTheSecrets()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        entity.RegistryAuthType = RegistryAuthType.AwsEcr;
        entity.AwsAccessKeyIdEncrypted = "enc:AKIAEXAMPLE0000000000";
        entity.AwsSecretAccessKeyEncrypted = "enc:wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
        entity.AwsRegion = "eu-central-1";

        var response = _mapper.ToResponse(entity);
        var serialized = System.Text.Json.JsonSerializer.Serialize(response);

        Assert.Equal(RegistryAuthType.AwsEcr, response.RegistryAuthType);
        Assert.True(response.HasAwsCredentials);
        Assert.Equal("eu-central-1", response.AwsRegion);
        Assert.DoesNotContain("AKIAEXAMPLE", serialized);
        Assert.DoesNotContain("wJalrXUtnFEMI", serialized);
    }

    [Fact]
    public void BuildProfileFromEntity_AwsEcr_PropagatesDecryptedKeyPairAndRegion()
    {
        var entity = SampleEntity(withRegistryCreds: false);
        entity.RegistryAuthType = RegistryAuthType.AwsEcr;
        entity.AwsAccessKeyIdEncrypted = "enc:AKIA";
        entity.AwsSecretAccessKeyEncrypted = "enc:secret";
        entity.AwsRegion = "eu-central-1";
        var connection = SampleConnection(withTls: false);

        var profile = _mapper.BuildProfileFromEntity(entity, connection);

        Assert.Equal(RegistryAuthType.AwsEcr, profile.RegistryAuthType);
        Assert.Equal("AKIA", profile.AwsAccessKeyId);
        Assert.Equal("secret", profile.AwsSecretAccessKey);
        Assert.Equal("eu-central-1", profile.AwsRegion);
    }

    [Fact]
    public void BuildProfileFromTestRequest_EcrHostNoExplicitAuth_PromotedAndRegionInferred()
    {
        var connection = SampleConnection(withTls: false);
        var request = TestRequest(
            imageReference: "123456789012.dkr.ecr.eu-central-1.amazonaws.com/my-app:1.0.0",
            awsAccessKeyId: new SecretValueUpsert(SecretValueAction.Set, "AKIA"),
            awsSecretAccessKey: new SecretValueUpsert(SecretValueAction.Set, "secret"));

        var profile = _mapper.BuildProfileFromTestRequest(request, connection, existing: null);

        Assert.Equal(RegistryAuthType.AwsEcr, profile.RegistryAuthType);
        Assert.Equal("AKIA", profile.AwsAccessKeyId);
        Assert.Equal("eu-central-1", profile.AwsRegion);
    }

    // ── factories ────────────────────────────────────────────────────────────

    private static DockerWatchEntity SampleEntity(bool withRegistryCreds) => new()
    {
        Id = Guid.NewGuid(),
        WebResourceId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Label = "app",
        Enabled = true,
        ImageReference = "ghcr.io/owner/repo:v1",
        RegistryHost = "ghcr.io",
        Repository = "owner/repo",
        Tag = "v1",
        ContainerName = "svc",
        UpdateNotificationsEnabled = true,
        ScheduleType = CheckScheduleType.Hourly,
        CheckEveryHours = 24,
        RegistryUsernameEncrypted = withRegistryCreds ? "enc:registry-user" : null,
        RegistryPasswordEncrypted = withRegistryCreds ? "enc:registry-pass" : null,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    private static DockerConnectionEntity SampleConnection(bool withTls) => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Name = "test-host",
        HostType = DockerHostType.TcpTls,
        HostUrl = "tcp://docker.local:2376",
        TlsCaCertEncrypted = withTls ? "enc:ca-pem" : null,
        TlsClientCertEncrypted = withTls ? "enc:cert-pem" : null,
        TlsClientKeyEncrypted = withTls ? "enc:key-pem" : null,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    private static DockerWatchUpsertRequest UpsertRequest(
        string label = "app",
        string imageReference = "nginx:1.27",
        string containerName = "web",
        SecretValueUpsert? registryUsername = null,
        SecretValueUpsert? registryPassword = null,
        bool telegramNotificationsEnabled = false,
        CheckScheduleType scheduleType = CheckScheduleType.Hourly,
        int checkEveryHours = 24,
        TimeOnly? checkAtTime = null,
        DayOfWeek? checkOnDayOfWeek = null,
        string? tagPatternFilter = null,
        SecretValueUpsert? gitHubPat = null,
        RegistryAuthType registryAuthType = RegistryAuthType.Auto,
        SecretValueUpsert? awsAccessKeyId = null,
        SecretValueUpsert? awsSecretAccessKey = null,
        string? awsRegion = null) =>
        new(
            Label: label,
            Enabled: true,
            ImageReference: imageReference,
            ContainerName: containerName,
            RegistryUsername: registryUsername,
            RegistryPassword: registryPassword,
            RegistryAuthType: registryAuthType,
            AwsAccessKeyId: awsAccessKeyId,
            AwsSecretAccessKey: awsSecretAccessKey,
            AwsRegion: awsRegion,
            UpdateNotificationsEnabled: true,
            TelegramNotificationsEnabled: telegramNotificationsEnabled,
            ScheduleType: scheduleType,
            CheckEveryHours: checkEveryHours,
            CheckAtTime: checkAtTime,
            CheckOnDayOfWeek: checkOnDayOfWeek,
            TagPatternFilter: tagPatternFilter,
            GitHubPat: gitHubPat);

    private static DockerWatchTestRequest TestRequest(
        string imageReference = "nginx:1.27",
        string containerName = "web",
        SecretValueUpsert? registryUsername = null,
        SecretValueUpsert? registryPassword = null,
        string? tagPatternFilter = null,
        SecretValueUpsert? gitHubPat = null,
        RegistryAuthType registryAuthType = RegistryAuthType.Auto,
        SecretValueUpsert? awsAccessKeyId = null,
        SecretValueUpsert? awsSecretAccessKey = null,
        string? awsRegion = null) =>
        new(
            ImageReference: imageReference,
            ContainerName: containerName,
            RegistryUsername: registryUsername,
            RegistryPassword: registryPassword,
            TagPatternFilter: tagPatternFilter,
            GitHubPat: gitHubPat,
            RegistryAuthType: registryAuthType,
            AwsAccessKeyId: awsAccessKeyId,
            AwsSecretAccessKey: awsSecretAccessKey,
            AwsRegion: awsRegion);
}
