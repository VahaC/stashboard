using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Infrastructure.Docker;

/// <summary>
/// V7.1 — unit tests for <see cref="ComposeProjectPaths.Resolve"/>: the
/// per-transport rules and the prefix-mapping boundary semantics that the
/// viewer, editor and updaters all share.
/// </summary>
public class ComposeProjectPathsTests
{
    private static readonly ComposePathMapping Mapping = new("/opt/stacks", "/compose");

    [Fact]
    public void Resolve_Ssh_UsesTheLabelPathAsIs()
    {
        Assert.Equal("/opt/stacks/media",
            ComposeProjectPaths.Resolve(DockerHostType.Ssh, Mapping, "/opt/stacks/media"));
    }

    [Fact]
    public void Resolve_LocalSocketWithoutMapping_UsesTheLabelPathAsIs()
    {
        Assert.Equal("/opt/stacks/media",
            ComposeProjectPaths.Resolve(DockerHostType.LocalSocket, null, "/opt/stacks/media"));
    }

    [Fact]
    public void Resolve_LocalSocketWithMapping_TranslatesThePrefix()
    {
        Assert.Equal("/compose/media",
            ComposeProjectPaths.Resolve(DockerHostType.LocalSocket, Mapping, "/opt/stacks/media"));
    }

    [Fact]
    public void Resolve_PrefixMatchesOnlyOnPathBoundary()
    {
        // `/opt/stacks` must not swallow `/opt/stacksize`.
        Assert.Equal("/opt/stacksize/media",
            ComposeProjectPaths.Resolve(DockerHostType.LocalSocket, Mapping, "/opt/stacksize/media"));
    }

    [Fact]
    public void Resolve_ExactPrefixMatch_MapsToContainerPrefix()
    {
        Assert.Equal("/compose",
            ComposeProjectPaths.Resolve(DockerHostType.LocalSocket, Mapping, "/opt/stacks"));
    }

    [Fact]
    public void Resolve_TrailingSlashesOnPrefixes_AreTolerated()
    {
        var sloppy = new ComposePathMapping("/opt/stacks/", "/compose/");
        Assert.Equal("/compose/media",
            ComposeProjectPaths.Resolve(DockerHostType.LocalSocket, sloppy, "/opt/stacks/media"));
    }

    [Fact]
    public void Resolve_NonMatchingPrefix_FallsBackToTheLabelPath()
    {
        Assert.Equal("/srv/other/media",
            ComposeProjectPaths.Resolve(DockerHostType.LocalSocket, Mapping, "/srv/other/media"));
    }

    [Fact]
    public void Resolve_TcpTls_HasNoFileAccess()
    {
        Assert.Null(ComposeProjectPaths.Resolve(DockerHostType.TcpTls, Mapping, "/opt/stacks/media"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_MissingWorkingDir_YieldsNull(string? workingDir)
    {
        Assert.Null(ComposeProjectPaths.Resolve(DockerHostType.LocalSocket, Mapping, workingDir));
        Assert.Null(ComposeProjectPaths.Resolve(DockerHostType.Ssh, null, workingDir));
    }
}



