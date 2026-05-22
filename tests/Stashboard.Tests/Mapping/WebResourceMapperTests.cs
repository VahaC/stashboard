using Moq;
using Stashboard.Api.Mapping;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Mapping;

public class WebResourceMapperTests
{
    private readonly Mock<IEncryptionService> _encryption = new();
    private readonly Mock<IFaviconService> _favicon = new();
    private readonly WebResourceMapper _sut;

    public WebResourceMapperTests()
    {
        _encryption.Setup(e => e.Decrypt(It.IsAny<string>()))
            .Returns<string>(v => v.StartsWith("enc:") ? v[4..] : v);
        _favicon
            .Setup(f => f.ResolveFaviconUrlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://favicon");
        _sut = new WebResourceMapper(_encryption.Object, _favicon.Object);
    }

    private static WebResourceEntity NewEntity(LogoSource logoSource = LogoSource.AutoFavicon, string? customLogo = null) =>
        new()
        {
            Name = "svc",
            MainUrl = "https://svc.example.com",
            HealthCheckMethod = HealthCheckMethod.Head,
            CurrentStatus = ServiceStatus.Up,
            LogoSource = logoSource,
            CustomLogoPath = customLogo,
            UserId = Guid.NewGuid(),
        };

    [Fact]
    public async Task MapAsync_AutoLogo_ResolvesFaviconUrl()
    {
        var entity = NewEntity();

        var dto = await _sut.MapAsync(entity, CancellationToken.None);

        Assert.Equal("https://favicon", dto.FaviconUrl);
        _favicon.Verify(f => f.ResolveFaviconUrlAsync(entity.MainUrl, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MapAsync_CustomLogoSource_SkipsFaviconResolution()
    {
        var entity = NewEntity(LogoSource.Custom, "/uploads/logos/x.png");

        var dto = await _sut.MapAsync(entity, CancellationToken.None);

        Assert.Null(dto.FaviconUrl);
        Assert.Equal("/uploads/logos/x.png", dto.CustomLogoPath);
        _favicon.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MapAsync_AutoLogoButCustomPathSet_SkipsFaviconResolution()
    {
        var entity = NewEntity(LogoSource.AutoFavicon, "/uploads/logos/preset.png");

        var dto = await _sut.MapAsync(entity, CancellationToken.None);

        Assert.Null(dto.FaviconUrl);
        _favicon.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MapAsync_DecryptsCredentialValues()
    {
        var entity = NewEntity();
        entity.Credentials.Add(new CredentialEntity
        {
            Key = "user", EncryptedValue = "enc:alice", IsSecret = false
        });
        entity.Credentials.Add(new CredentialEntity
        {
            Key = "pwd", EncryptedValue = "enc:s3cret", IsSecret = true
        });

        var dto = await _sut.MapAsync(entity, CancellationToken.None);

        Assert.Equal(2, dto.Credentials.Count);
        Assert.Contains(dto.Credentials, c => c.Key == "user" && c.Value == "alice" && !c.IsSecret);
        Assert.Contains(dto.Credentials, c => c.Key == "pwd" && c.Value == "s3cret" && c.IsSecret);
    }

    [Fact]
    public async Task MapAsync_CredentialDecryptionThrows_ReturnsEmptyValueInsteadOfPropagating()
    {
        _encryption.Setup(e => e.Decrypt("bad")).Throws(new InvalidOperationException("corrupt"));
        var entity = NewEntity();
        entity.Credentials.Add(new CredentialEntity { Key = "k", EncryptedValue = "bad", IsSecret = true });

        var dto = await _sut.MapAsync(entity, CancellationToken.None);

        var c = Assert.Single(dto.Credentials);
        Assert.Equal(string.Empty, c.Value);
    }

    [Fact]
    public async Task MapAsync_TagsAreOrderedAlphabetically()
    {
        var entity = NewEntity();
        entity.WebResourceTags.Add(new WebResourceTagEntity { Tag = new TagEntity { Name = "zeta" } });
        entity.WebResourceTags.Add(new WebResourceTagEntity { Tag = new TagEntity { Name = "alpha" } });
        entity.WebResourceTags.Add(new WebResourceTagEntity { Tag = new TagEntity { Name = "mu" } });

        var dto = await _sut.MapAsync(entity, CancellationToken.None);

        Assert.Equal(new[] { "alpha", "mu", "zeta" }, dto.Tags);
    }

    [Fact]
    public async Task MapAsync_PropagatesCategoryNameAndColor()
    {
        var entity = NewEntity();
        var categoryId = Guid.NewGuid();
        entity.CategoryId = categoryId;
        entity.Category = new CategoryEntity { Id = categoryId, Name = "Productivity", Color = "#abc" };

        var dto = await _sut.MapAsync(entity, CancellationToken.None);

        Assert.Equal(categoryId, dto.CategoryId);
        Assert.Equal("Productivity", dto.CategoryName);
        Assert.Equal("#abc", dto.CategoryColor);
    }

    // ── Docker watch aggregation drives the dashboard "Update" badge ────────

    [Fact]
    public async Task MapAsync_NoDockerWatches_DockerUpdateStatusIsNull()
    {
        var entity = NewEntity();

        var dto = await _sut.MapAsync(entity, CancellationToken.None);

        Assert.Null(dto.DockerUpdateStatus);
    }

    [Fact]
    public async Task MapAsync_AnyWatchUpdateAvailable_AggregatesAsUpdateAvailable()
    {
        // The dashboard badge fires if even ONE container in a composite
        // service has an update — the others' status doesn't matter.
        var entity = NewEntity();
        entity.DockerWatches.Add(BuildWatch("app", DockerUpdateStatus.UpToDate));
        entity.DockerWatches.Add(BuildWatch("db", DockerUpdateStatus.UpdateAvailable));

        var dto = await _sut.MapAsync(entity, CancellationToken.None);

        Assert.Equal(DockerUpdateStatus.UpdateAvailable, dto.DockerUpdateStatus);
    }

    [Fact]
    public async Task MapAsync_AnyWatchError_AggregatesAsError_WhenNoUpdateAvailable()
    {
        var entity = NewEntity();
        entity.DockerWatches.Add(BuildWatch("app", DockerUpdateStatus.UpToDate));
        entity.DockerWatches.Add(BuildWatch("db", DockerUpdateStatus.Error));

        var dto = await _sut.MapAsync(entity, CancellationToken.None);

        Assert.Equal(DockerUpdateStatus.Error, dto.DockerUpdateStatus);
    }

    [Fact]
    public async Task MapAsync_AllWatchesUpToDate_AggregatesAsUpToDate()
    {
        var entity = NewEntity();
        entity.DockerWatches.Add(BuildWatch("app", DockerUpdateStatus.UpToDate));
        entity.DockerWatches.Add(BuildWatch("db", DockerUpdateStatus.UpToDate));

        var dto = await _sut.MapAsync(entity, CancellationToken.None);

        Assert.Equal(DockerUpdateStatus.UpToDate, dto.DockerUpdateStatus);
    }

    [Fact]
    public async Task MapAsync_AllWatchesDisabled_AggregatesAsDisabled()
    {
        var entity = NewEntity();
        entity.DockerWatches.Add(BuildWatch("app", DockerUpdateStatus.Disabled));
        entity.DockerWatches.Add(BuildWatch("db", DockerUpdateStatus.Disabled));

        var dto = await _sut.MapAsync(entity, CancellationToken.None);

        Assert.Equal(DockerUpdateStatus.Disabled, dto.DockerUpdateStatus);
    }

    [Fact]
    public async Task MapAsync_MixedUnknownAndUpToDate_FallsBackToUnknown()
    {
        var entity = NewEntity();
        entity.DockerWatches.Add(BuildWatch("app", DockerUpdateStatus.UpToDate));
        entity.DockerWatches.Add(BuildWatch("db", DockerUpdateStatus.Unknown));

        var dto = await _sut.MapAsync(entity, CancellationToken.None);

        Assert.Equal(DockerUpdateStatus.Unknown, dto.DockerUpdateStatus);
    }

    [Fact]
    public async Task MapAsync_UpdateAvailableBeatsErrorBeatsUpToDate()
    {
        // Precedence test: UpdateAvailable must win over Error.
        var entity = NewEntity();
        entity.DockerWatches.Add(BuildWatch("app", DockerUpdateStatus.UpdateAvailable));
        entity.DockerWatches.Add(BuildWatch("db", DockerUpdateStatus.Error));
        entity.DockerWatches.Add(BuildWatch("cache", DockerUpdateStatus.UpToDate));

        var dto = await _sut.MapAsync(entity, CancellationToken.None);

        Assert.Equal(DockerUpdateStatus.UpdateAvailable, dto.DockerUpdateStatus);
    }

    private static DockerWatchEntity BuildWatch(string label, DockerUpdateStatus status) => new()
    {
        Label = label,
        ImageReference = $"example/{label}:latest",
        RegistryHost = "docker.io",
        Repository = $"example/{label}",
        Tag = "latest",
        ContainerName = label,
        UpdateStatus = status,
    };
}
