using Stashboard.Core.Abstractions;
using Stashboard.Infrastructure.Docker;

namespace Stashboard.Tests.Infrastructure.Docker;

/// <summary>
/// V7.7 — unit tests for <see cref="ComposeFileLinter"/>: the six rules
/// (port-collision, depends-on-cycle, missing-healthcheck, bind-mount-escape,
/// deprecated-key, latest-tag), their severities and the service each finding
/// renders on, plus the no-false-positive cases the rules deliberately tolerate.
/// </summary>
public class ComposeFileLinterTests
{
    private readonly ComposeFileLinter _linter = new();

    private IReadOnlyList<ComposeLintFinding> Lint(string yaml, string? path = null) => _linter.Lint(yaml, path);

    [Fact]
    public void CleanProject_HasNoFindings()
    {
        const string yaml = """
            services:
              web:
                image: nginx:1.27
                ports:
                  - "8080:80"
                depends_on:
                  db:
                    condition: service_healthy
                volumes:
                  - ./conf:/etc/nginx:ro
              db:
                image: postgres:16
                healthcheck:
                  test: ["CMD", "pg_isready"]
            """;

        Assert.Empty(Lint(yaml));
    }

    // ── port-collision ─────────────────────────────────────────────────────────

    [Fact]
    public void PortCollision_SameHostPort_FlagsBothServices()
    {
        const string yaml = """
            services:
              a:
                image: nginx:1.27
                ports: ["8080:80"]
              b:
                image: httpd:2.4
                ports: ["8080:8080"]
            """;

        var findings = Lint(yaml);
        var clashes = findings.Where(f => f.Rule == "port-collision").ToList();
        Assert.Equal(2, clashes.Count);
        Assert.All(clashes, f => Assert.Equal(ComposeLintSeverity.Error, f.Severity));
        Assert.Contains(clashes, f => f.Service == "a");
        Assert.Contains(clashes, f => f.Service == "b");
    }

    [Fact]
    public void PortCollision_DifferentProtocol_DoesNotClash()
    {
        const string yaml = """
            services:
              a:
                image: x
                ports: ["53:53/tcp"]
              b:
                image: y
                ports: ["53:53/udp"]
            """;

        Assert.DoesNotContain(Lint(yaml), f => f.Rule == "port-collision");
    }

    [Fact]
    public void PortCollision_DistinctSpecificHostIps_DoNotClash()
    {
        const string yaml = """
            services:
              a:
                image: x
                ports: ["127.0.0.1:8080:80"]
              b:
                image: y
                ports: ["192.168.1.10:8080:80"]
            """;

        Assert.DoesNotContain(Lint(yaml), f => f.Rule == "port-collision");
    }

    [Fact]
    public void PortCollision_ContainerOnlyPort_IsIgnored()
    {
        const string yaml = """
            services:
              a:
                image: x
                ports: ["80"]
              b:
                image: y
                ports: ["80"]
            """;

        Assert.DoesNotContain(Lint(yaml), f => f.Rule == "port-collision");
    }

    [Fact]
    public void PortCollision_LongFormPublished_Clashes()
    {
        const string yaml = """
            services:
              a:
                image: x
                ports:
                  - target: 80
                    published: 8080
              b:
                image: y
                ports: ["8080:9000"]
            """;

        Assert.Equal(2, Lint(yaml).Count(f => f.Rule == "port-collision"));
    }

    // ── depends-on-cycle ───────────────────────────────────────────────────────

    [Fact]
    public void DependsOnCycle_TwoNodes_ReportedOncePerNode()
    {
        const string yaml = """
            services:
              a:
                image: x
                depends_on: [b]
              b:
                image: y
                depends_on: [a]
            """;

        var cycles = Lint(yaml).Where(f => f.Rule == "depends-on-cycle").ToList();
        Assert.Equal(2, cycles.Count);
        Assert.All(cycles, f => Assert.Equal(ComposeLintSeverity.Error, f.Severity));
        Assert.Contains(cycles, f => f.Service == "a");
        Assert.Contains(cycles, f => f.Service == "b");
    }

    [Fact]
    public void DependsOnCycle_Acyclic_NoFinding()
    {
        const string yaml = """
            services:
              a:
                image: x
                depends_on: [b]
              b:
                image: y
                depends_on: [c]
              c:
                image: z
            """;

        Assert.DoesNotContain(Lint(yaml), f => f.Rule == "depends-on-cycle");
    }

    [Fact]
    public void DependsOnCycle_SelfLoop_IsReported()
    {
        const string yaml = """
            services:
              a:
                image: x
                depends_on: [a]
            """;

        Assert.Contains(Lint(yaml), f => f.Rule == "depends-on-cycle" && f.Service == "a");
    }

    // ── missing-healthcheck ────────────────────────────────────────────────────

    [Fact]
    public void MissingHealthcheck_ServiceHealthyWithoutHealthcheck_FlagsTarget()
    {
        const string yaml = """
            services:
              web:
                image: nginx
                depends_on:
                  db:
                    condition: service_healthy
              db:
                image: postgres:16
            """;

        var finding = Assert.Single(Lint(yaml), f => f.Rule == "missing-healthcheck");
        Assert.Equal(ComposeLintSeverity.Error, finding.Severity);
        Assert.Equal("db", finding.Service);
    }

    [Fact]
    public void MissingHealthcheck_WithHealthcheck_NoFinding()
    {
        const string yaml = """
            services:
              web:
                image: nginx
                depends_on:
                  db:
                    condition: service_healthy
              db:
                image: postgres:16
                healthcheck:
                  test: ["CMD", "pg_isready"]
            """;

        Assert.DoesNotContain(Lint(yaml), f => f.Rule == "missing-healthcheck");
    }

    [Fact]
    public void MissingHealthcheck_DisabledHealthcheck_IsFlagged()
    {
        const string yaml = """
            services:
              web:
                image: nginx
                depends_on:
                  db:
                    condition: service_healthy
              db:
                image: postgres:16
                healthcheck:
                  disable: true
            """;

        Assert.Contains(Lint(yaml), f => f.Rule == "missing-healthcheck" && f.Service == "db");
    }

    [Fact]
    public void MissingHealthcheck_ConditionStarted_NotFlagged()
    {
        const string yaml = """
            services:
              web:
                image: nginx
                depends_on:
                  db:
                    condition: service_started
              db:
                image: postgres:16
            """;

        Assert.DoesNotContain(Lint(yaml), f => f.Rule == "missing-healthcheck");
    }

    // ── bind-mount-escape ──────────────────────────────────────────────────────

    [Fact]
    public void BindMountEscape_RelativeParent_IsFlagged()
    {
        const string yaml = """
            services:
              a:
                image: x
                volumes:
                  - ../shared:/data
            """;

        var finding = Assert.Single(Lint(yaml), f => f.Rule == "bind-mount-escape");
        Assert.Equal(ComposeLintSeverity.Warning, finding.Severity);
        Assert.Equal("a", finding.Service);
    }

    [Fact]
    public void BindMountEscape_InsideRoot_NotFlagged()
    {
        const string yaml = """
            services:
              a:
                image: x
                volumes:
                  - ./conf:/etc/nginx
                  - ./a/../b:/data
            """;

        Assert.DoesNotContain(Lint(yaml), f => f.Rule == "bind-mount-escape");
    }

    [Fact]
    public void BindMountEscape_AbsolutePath_NotFlagged()
    {
        const string yaml = """
            services:
              a:
                image: x
                volumes:
                  - /var/run/docker.sock:/var/run/docker.sock
                  - /etc/localtime:/etc/localtime:ro
            """;

        Assert.DoesNotContain(Lint(yaml), f => f.Rule == "bind-mount-escape");
    }

    [Fact]
    public void BindMountEscape_NamedVolume_NotFlagged()
    {
        const string yaml = """
            services:
              a:
                image: x
                volumes:
                  - data:/var/lib/data
            volumes:
              data:
            """;

        Assert.DoesNotContain(Lint(yaml), f => f.Rule == "bind-mount-escape");
    }

    [Fact]
    public void BindMountEscape_HomePath_IsFlagged()
    {
        const string yaml = """
            services:
              a:
                image: x
                volumes:
                  - ~/config:/config
            """;

        Assert.Contains(Lint(yaml), f => f.Rule == "bind-mount-escape" && f.Service == "a");
    }

    // ── deprecated-key ─────────────────────────────────────────────────────────

    [Fact]
    public void DeprecatedKey_TopLevelVersion_ProjectLevelWarning()
    {
        const string yaml = """
            version: "3.8"
            services:
              a:
                image: x:1
            """;

        var finding = Assert.Single(Lint(yaml), f => f.Rule == "deprecated-key");
        Assert.Equal(ComposeLintSeverity.Warning, finding.Severity);
        Assert.Null(finding.Service);
    }

    [Fact]
    public void DeprecatedKey_LinksAndVolumesFrom_FlagPerService()
    {
        const string yaml = """
            services:
              a:
                image: x:1
                links:
                  - b
              b:
                image: y:1
                volumes_from:
                  - a
            """;

        var findings = Lint(yaml).Where(f => f.Rule == "deprecated-key").ToList();
        Assert.Contains(findings, f => f.Service == "a" && f.Message.Contains("links"));
        Assert.Contains(findings, f => f.Service == "b" && f.Message.Contains("volumes_from"));
    }

    // ── latest-tag ─────────────────────────────────────────────────────────────

    [Fact]
    public void LatestTag_ExplicitLatest_IsWarning()
    {
        const string yaml = """
            services:
              a:
                image: nginx:latest
            """;

        var finding = Assert.Single(Lint(yaml), f => f.Rule == "latest-tag");
        Assert.Equal(ComposeLintSeverity.Warning, finding.Severity);
        Assert.Equal("a", finding.Service);
    }

    [Fact]
    public void LatestTag_NoTag_IsWarning()
    {
        const string yaml = """
            services:
              a:
                image: nginx
            """;

        Assert.Single(Lint(yaml), f => f.Rule == "latest-tag" && f.Message.Contains("no tag"));
    }

    [Fact]
    public void LatestTag_PinnedVersion_NoFinding()
    {
        const string yaml = """
            services:
              a:
                image: nginx:1.27
            """;

        Assert.DoesNotContain(Lint(yaml), f => f.Rule == "latest-tag");
    }

    [Fact]
    public void LatestTag_DigestPinned_NoFinding()
    {
        const string yaml = """
            services:
              a:
                image: nginx@sha256:0123456789abcdef
            """;

        Assert.DoesNotContain(Lint(yaml), f => f.Rule == "latest-tag");
    }

    [Fact]
    public void LatestTag_RegistryWithPortNoTag_IsFlagged()
    {
        const string yaml = """
            services:
              a:
                image: registry.example.com:5000/team/app
            """;

        // The registry-host colon is not a tag; this image has no tag → flagged.
        Assert.Single(Lint(yaml), f => f.Rule == "latest-tag");
    }

    [Fact]
    public void LatestTag_BareVariableTag_NotFlagged()
    {
        const string yaml = """
            services:
              a:
                image: nginx:${NGINX_VERSION}
            """;

        // No default to inspect → genuinely unknown → not flagged.
        Assert.DoesNotContain(Lint(yaml), f => f.Rule == "latest-tag");
    }

    [Fact]
    public void LatestTag_VariableDefaultingToLatest_IsFlagged()
    {
        const string yaml = """
            services:
              a:
                image: vahac/stashboard:${STASHBOARD_TAG:-latest}
            """;

        var finding = Assert.Single(Lint(yaml), f => f.Rule == "latest-tag");
        Assert.Equal(ComposeLintSeverity.Warning, finding.Severity);
        Assert.Contains("default", finding.Message);
    }

    [Fact]
    public void LatestTag_VariableDefaultingToPinnedVersion_NotFlagged()
    {
        const string yaml = """
            services:
              a:
                image: nginx:${NGINX_VERSION:-1.27}
            """;

        Assert.DoesNotContain(Lint(yaml), f => f.Rule == "latest-tag");
    }

    [Fact]
    public void LatestTag_WholeImageFromVariable_NotFlagged()
    {
        const string yaml = """
            services:
              a:
                image: ${APP_IMAGE}
            """;

        // The whole reference is a variable — unknown, not "no tag".
        Assert.DoesNotContain(Lint(yaml), f => f.Rule == "latest-tag");
    }

    // ── robustness ─────────────────────────────────────────────────────────────

    [Fact]
    public void InvalidYaml_ReturnsNoFindings()
    {
        Assert.Empty(Lint("::: not : valid : yaml"));
    }

    [Fact]
    public void NoServices_ReturnsNoFindings()
    {
        Assert.Empty(Lint("name: empty"));
    }
}



