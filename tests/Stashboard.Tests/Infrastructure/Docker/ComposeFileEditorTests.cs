using Stashboard.Core.Abstractions;
using Stashboard.Infrastructure.Docker;

namespace Stashboard.Tests.Infrastructure.Docker;

/// <summary>
/// V7.1 — round-trip tests for <see cref="ComposeFileEditor"/>. These are the
/// POC evidence behind docs/adr/0001-compose-yaml-round-trip.md: every test
/// asserts the FULL output text, so any stray reformatting, comment loss or
/// key reordering outside the edited keys fails the suite immediately.
/// </summary>
public class ComposeFileEditorTests
{
    private readonly ComposeFileEditor _editor = new(new ComposeFileParser());

    /// <summary>Comment-heavy, order-sensitive fixture. The trailing comment
    /// after the ports block and the one above volumes guard the block-end
    /// span trap (block SequenceEnd marks point at the next token).</summary>
    private const string Fixture = """
        # Media stack — comments and key order must survive edits.
        name: media
        services:
          jellyfin:
            image: jellyfin/jellyfin:10.9.2 # pinned — update deliberately
            container_name: jellyfin
            restart: unless-stopped
            ports:
              - "8096:8096"
            # config lives on the SSD
            volumes:
              - ./config:/config
              - /srv/media:/media:ro
            environment:
              TZ: Europe/Kyiv
              JELLYFIN_PublishedServerUrl: http://jf.local
          db:
            image: mariadb:11
            environment:
              - MYSQL_ROOT_PASSWORD=secret
              - MYSQL_USER=jf
        networks:
          default:
            name: media-net
        """;

    /// <summary>UI flow mirror: start from what GET returned, change one thing.</summary>
    private static ComposeServiceEdit EditFrom(ComposeServiceModel s) => new(
        s.Image, s.Restart, s.Ports, s.Volumes, s.Environment, s.Labels,
        s.Command, s.Entrypoint, s.User, s.WorkingDir, s.Resources);

    private static ComposeServiceModel Service(string yaml, string name) =>
        new ComposeFileParser().Parse(yaml).Project!.Services.Single(s => s.Name == name);

    // ── scalar edits ────────────────────────────────────────────────────────

    [Fact]
    public void ImageEdit_TouchesOnlyTheValue_AndKeepsTrailingComment()
    {
        var edit = EditFrom(Service(Fixture, "jellyfin")) with { Image = "jellyfin/jellyfin:10.10.0" };

        var result = _editor.ApplyServiceEdit(Fixture, "jellyfin", edit);

        Assert.Null(result.Error);
        Assert.True(result.Changed);
        Assert.Equal(
            Fixture.Replace(
                "image: jellyfin/jellyfin:10.9.2 # pinned — update deliberately",
                "image: jellyfin/jellyfin:10.10.0 # pinned — update deliberately"),
            result.Content);
    }

    [Fact]
    public void QuotedScalar_IsReplacedIncludingQuotes()
    {
        const string yaml = """
            services:
              app:
                image: "nginx:1.27"
            """;
        var edit = EditFrom(Service(yaml, "app")) with { Image = "nginx:1.28" };

        var result = _editor.ApplyServiceEdit(yaml, "app", edit);

        Assert.Equal(yaml.Replace("\"nginx:1.27\"", "nginx:1.28"), result.Content);
    }

    [Fact]
    public void ScalarAdd_AppendsAtEndOfServiceBody()
    {
        var edit = EditFrom(Service(Fixture, "db")) with { Restart = "unless-stopped" };

        var result = _editor.ApplyServiceEdit(Fixture, "db", edit);

        Assert.Null(result.Error);
        Assert.Equal(
            Fixture.Replace(
                "      - MYSQL_USER=jf\n",
                "      - MYSQL_USER=jf\n    restart: unless-stopped\n"),
            result.Content);
    }

    [Fact]
    public void ScalarRemove_DeletesTheWholeLine()
    {
        var edit = EditFrom(Service(Fixture, "jellyfin")) with { Restart = null };

        var result = _editor.ApplyServiceEdit(Fixture, "jellyfin", edit);

        Assert.Equal(Fixture.Replace("    restart: unless-stopped\n", ""), result.Content);
    }

    [Fact]
    public void AmbiguousScalarValues_AreQuoted()
    {
        var edit = EditFrom(Service(Fixture, "db")) with { User = "1000:1000" };

        var result = _editor.ApplyServiceEdit(Fixture, "db", edit);

        Assert.Contains("    user: \"1000:1000\"", result.Content);
    }

    // ── alias / anchor safety ───────────────────────────────────────────────

    [Fact]
    public void AliasValue_IsReplacedAtTheUseSite_AnchorDefinitionUntouched()
    {
        const string yaml = """
            services:
              a:
                image: app:1
                restart: &policy unless-stopped
              b:
                image: app:2
                restart: *policy
            """;
        var edit = EditFrom(Service(yaml, "b")) with { Restart = "always" };

        var result = _editor.ApplyServiceEdit(yaml, "b", edit);

        Assert.Null(result.Error);
        Assert.Equal(yaml.Replace("restart: *policy", "restart: always"), result.Content);
    }

    [Fact]
    public void AnchoredValue_RefusesTheEdit()
    {
        const string yaml = """
            services:
              a:
                image: app:1
                restart: &policy unless-stopped
              b:
                restart: *policy
            """;
        var edit = EditFrom(Service(yaml, "a")) with { Restart = "always" };

        var result = _editor.ApplyServiceEdit(yaml, "a", edit);

        Assert.Null(result.Content);
        Assert.Contains("anchor", result.Error);
    }

    // ── block edits (ports / volumes / environment / labels) ───────────────

    [Fact]
    public void PortsEdit_RewritesOnlyThePortsBlock_CommentAfterBlockSurvives()
    {
        var edit = EditFrom(Service(Fixture, "jellyfin")) with
        {
            Ports = new[] { "8096:8096", "7359:7359/udp" },
        };

        var result = _editor.ApplyServiceEdit(Fixture, "jellyfin", edit);

        Assert.Null(result.Error);
        Assert.Equal(
            Fixture.Replace(
                "    ports:\n      - \"8096:8096\"\n",
                "    ports:\n      - \"8096:8096\"\n      - \"7359:7359/udp\"\n"),
            result.Content);
    }

    [Fact]
    public void PortsRemove_DeletesKeyAndItems()
    {
        var edit = EditFrom(Service(Fixture, "jellyfin")) with { Ports = Array.Empty<string>() };

        var result = _editor.ApplyServiceEdit(Fixture, "jellyfin", edit);

        Assert.Equal(
            Fixture.Replace("    ports:\n      - \"8096:8096\"\n", ""),
            result.Content);
    }

    [Fact]
    public void EnvironmentMappingForm_KeepsMappingStyle_AndQuotesAmbiguousValues()
    {
        var svc = Service(Fixture, "jellyfin");
        var edit = EditFrom(svc) with
        {
            Environment = svc.Environment
                .Append(new ComposeEnvVar("JELLYFIN_LOG_LEVEL", "3"))
                .ToList(),
        };

        var result = _editor.ApplyServiceEdit(Fixture, "jellyfin", edit);

        Assert.Null(result.Error);
        Assert.Equal(
            Fixture.Replace(
                "    environment:\n      TZ: Europe/Kyiv\n      JELLYFIN_PublishedServerUrl: http://jf.local\n",
                "    environment:\n      TZ: Europe/Kyiv\n      JELLYFIN_PublishedServerUrl: http://jf.local\n      JELLYFIN_LOG_LEVEL: \"3\"\n"),
            result.Content);
    }

    [Fact]
    public void EnvironmentListForm_KeepsListStyle()
    {
        var svc = Service(Fixture, "db");
        var edit = EditFrom(svc) with
        {
            Environment = svc.Environment
                .Append(new ComposeEnvVar("MYSQL_DATABASE", "jellyfin"))
                .ToList(),
        };

        var result = _editor.ApplyServiceEdit(Fixture, "db", edit);

        Assert.Null(result.Error);
        Assert.Equal(
            Fixture.Replace(
                "      - MYSQL_USER=jf\n",
                "      - MYSQL_USER=jf\n      - MYSQL_DATABASE=jellyfin\n"),
            result.Content);
    }

    [Fact]
    public void LabelsAdd_DefaultsToMappingStyle()
    {
        var edit = EditFrom(Service(Fixture, "db")) with
        {
            Labels = new[] { new ComposeEnvVar("com.example.team", "media") },
        };

        var result = _editor.ApplyServiceEdit(Fixture, "db", edit);

        Assert.Null(result.Error);
        Assert.Equal(
            Fixture.Replace(
                "      - MYSQL_USER=jf\n",
                "      - MYSQL_USER=jf\n    labels:\n      com.example.team: media\n"),
            result.Content);
    }

    [Fact]
    public void EmptyValuedKey_IsRewrittenWithoutSwallowingTheNextKey()
    {
        const string yaml = """
            services:
              app:
                labels:
                image: app:1
            """;
        var edit = EditFrom(Service(yaml, "app")) with
        {
            Labels = new[] { new ComposeEnvVar("a", "b") },
        };

        var result = _editor.ApplyServiceEdit(yaml, "app", edit);

        Assert.Null(result.Error);
        Assert.Equal("""
            services:
              app:
                labels:
                  a: b
                image: app:1
            """, result.Content);
    }

    // ── command / entrypoint dual form ──────────────────────────────────────

    [Fact]
    public void CommandStringToExecList_RewritesAsFlowSequence()
    {
        const string yaml = """
            services:
              app:
                image: app:1
                command: serve --port 8080
            """;
        var edit = EditFrom(Service(yaml, "app")) with { Command = "[\"serve\", \"--port\", \"9090\"]" };

        var result = _editor.ApplyServiceEdit(yaml, "app", edit);

        Assert.Null(result.Error);
        Assert.Equal(
            yaml.Replace("command: serve --port 8080", "command: [\"serve\", \"--port\", \"9090\"]"),
            result.Content);
    }

    [Fact]
    public void CommandBlockListToString_RewritesKeyAndValue()
    {
        const string yaml = """
            services:
              app:
                image: app:1
                command:
                  - serve
                  - --port
                  - "8080"
                restart: always
            """;
        var edit = EditFrom(Service(yaml, "app")) with { Command = "serve --port 9090" };

        var result = _editor.ApplyServiceEdit(yaml, "app", edit);

        Assert.Null(result.Error);
        Assert.Equal("""
            services:
              app:
                image: app:1
                command: serve --port 9090
                restart: always
            """, result.Content);
    }

    [Fact]
    public void CommandInvalidFlowList_IsRejected()
    {
        var edit = EditFrom(Service(Fixture, "db")) with { Command = "[\"unclosed" };

        var result = _editor.ApplyServiceEdit(Fixture, "db", edit);

        Assert.Null(result.Content);
        Assert.Contains("not valid YAML", result.Error);
    }

    // ── add to a bodyless service ───────────────────────────────────────────

    [Fact]
    public void NullBodyService_GrowsABody()
    {
        const string yaml = """
            services:
              placeholder:
              app:
                image: app:1
            """;
        var edit = EditFrom(Service(yaml, "placeholder")) with { Image = "nginx:1.28" };

        var result = _editor.ApplyServiceEdit(yaml, "placeholder", edit);

        Assert.Null(result.Error);
        Assert.Equal("""
            services:
              placeholder:
                image: nginx:1.28
              app:
                image: app:1
            """, result.Content);
    }

    // ── no-op, errors, guards ───────────────────────────────────────────────

    [Fact]
    public void IdenticalDesiredState_ReportsUnchanged()
    {
        var edit = EditFrom(Service(Fixture, "jellyfin"));

        var result = _editor.ApplyServiceEdit(Fixture, "jellyfin", edit);

        Assert.Null(result.Error);
        Assert.False(result.Changed);
        Assert.Equal(Fixture, result.Content);
    }

    [Fact]
    public void UnknownService_Errors()
    {
        var edit = EditFrom(Service(Fixture, "db"));

        var result = _editor.ApplyServiceEdit(Fixture, "ghost", edit);

        Assert.Contains("'ghost' was not found", result.Error);
    }

    [Fact]
    public void MergeKeyService_RefusesTheEdit()
    {
        const string yaml = """
            x-base: &base
              restart: always
            services:
              app:
                <<: *base
                image: app:1
            """;
        var edit = EditFrom(Service(yaml, "app")) with { Image = "app:2" };

        var result = _editor.ApplyServiceEdit(yaml, "app", edit);

        Assert.Contains("merge key", result.Error);
    }

    [Fact]
    public void FlowStyleServiceBody_RefusesTheEdit()
    {
        const string yaml = """
            services:
              app: { image: app:1, restart: always }
            """;
        var edit = EditFrom(Service(yaml, "app")) with { Image = "app:2" };

        var result = _editor.ApplyServiceEdit(yaml, "app", edit);

        Assert.Contains("flow-style", result.Error);
    }

    [Fact]
    public void EmptyPortEntry_IsRejected()
    {
        var edit = EditFrom(Service(Fixture, "jellyfin")) with { Ports = new[] { "8096:8096", " " } };

        var result = _editor.ApplyServiceEdit(Fixture, "jellyfin", edit);

        Assert.Contains("Ports", result.Error);
    }

    [Fact]
    public void EnvNameWithEquals_IsRejected()
    {
        var edit = EditFrom(Service(Fixture, "db")) with
        {
            Environment = new[] { new ComposeEnvVar("BAD=NAME", "x") },
        };

        var result = _editor.ApplyServiceEdit(Fixture, "db", edit);

        Assert.Contains("must not contain '='", result.Error);
    }

    // ── multi-field + CRLF + semantic re-parse ──────────────────────────────

    [Fact]
    public void MultiFieldEdit_AllSplicesLand_AndResultReparses()
    {
        var svc = Service(Fixture, "jellyfin");
        var edit = EditFrom(svc) with
        {
            Image = "jellyfin/jellyfin:10.10.0",
            Restart = "always",
            Ports = new[] { "8096:8096", "8920:8920" },
            WorkingDir = "/app",
        };

        var result = _editor.ApplyServiceEdit(Fixture, "jellyfin", edit);

        Assert.Null(result.Error);
        var reparsed = Service(result.Content!, "jellyfin");
        Assert.Equal("jellyfin/jellyfin:10.10.0", reparsed.Image);
        Assert.Equal("always", reparsed.Restart);
        Assert.Equal(new[] { "8096:8096", "8920:8920" }, reparsed.Ports);
        Assert.Equal("/app", reparsed.WorkingDir);
        // Unrelated parts survive byte-for-byte.
        Assert.Contains("# Media stack — comments and key order must survive edits.", result.Content);
        Assert.Contains("# config lives on the SSD", result.Content);
        Assert.Contains("    environment:\n      TZ: Europe/Kyiv\n", result.Content);
        Assert.Contains("networks:\n  default:\n    name: media-net", result.Content);
        // db service untouched.
        Assert.Contains("  db:\n    image: mariadb:11\n", result.Content);
    }

    [Fact]
    public void CrlfFile_KeepsCrlfLineEndings()
    {
        var crlf = Fixture.Replace("\n", "\r\n");
        var svc = Service(crlf, "jellyfin");
        var edit = EditFrom(svc) with { Ports = new[] { "8096:8096", "8920:8920" } };

        var result = _editor.ApplyServiceEdit(crlf, "jellyfin", edit);

        Assert.Null(result.Error);
        Assert.DoesNotContain("\n      - \"8920:8920\"", result.Content!.Replace("\r\n", "\x01"));
        Assert.Equal(
            crlf.Replace(
                "    ports:\r\n      - \"8096:8096\"\r\n",
                "    ports:\r\n      - \"8096:8096\"\r\n      - \"8920:8920\"\r\n"),
            result.Content);
    }

    // ── V7.2 resource constraints ──────────────────────────────────────────

    /// <summary>Re-parse a service from edited YAML for behavioural assertions
    /// (the resource blocks are nested enough that asserting full whitespace is
    /// brittle; comment-survival is asserted separately).</summary>
    private static ComposeResourceConstraints ResourcesOf(string yaml, string name) =>
        Service(yaml, name).Resources;

    [Fact]
    public void DeployResources_AddsBlock_WhenServiceHasNoDeploy()
    {
        var svc = Service(Fixture, "jellyfin");
        var edit = EditFrom(svc) with
        {
            Resources = svc.Resources with { CpuLimit = "2", MemLimit = "1g" },
        };

        var result = _editor.ApplyServiceEdit(Fixture, "jellyfin", edit);

        Assert.Null(result.Error);
        Assert.True(result.Changed);
        var r = ResourcesOf(result.Content!, "jellyfin");
        Assert.Equal("deploy", r.Convention);
        Assert.Equal("2", r.CpuLimit);
        Assert.Equal("1g", r.MemLimit);
        // The pinned-image comment and the db service survive.
        Assert.Contains("# pinned — update deliberately", result.Content);
        Assert.Contains("  db:\n    image: mariadb:11\n", result.Content);
    }

    [Fact]
    public void DeployResources_UpdatesLeaf_AndPreservesSiblingDeployKeys()
    {
        const string yaml = """
            services:
              app:
                image: app:1
                deploy:
                  replicas: 3
                  resources:
                    limits:
                      cpus: "1"
                      memory: 256m
            """;
        var svc = Service(yaml, "app");
        var edit = EditFrom(svc) with
        {
            Resources = svc.Resources with { CpuLimit = "4", MemLimit = "1g" },
        };

        var result = _editor.ApplyServiceEdit(yaml, "app", edit);

        Assert.Null(result.Error);
        var r = ResourcesOf(result.Content!, "app");
        Assert.Equal("4", r.CpuLimit);
        Assert.Equal("1g", r.MemLimit);
        // The sibling replicas key under deploy is untouched.
        Assert.Contains("    replicas: 3\n", result.Content);
    }

    [Fact]
    public void DeployResources_RemovesDeploy_WhenResourcesWasOnlyChild()
    {
        const string yaml = """
            services:
              app:
                image: app:1
                deploy:
                  resources:
                    limits:
                      cpus: "1"
            """;
        var svc = Service(yaml, "app");
        var edit = EditFrom(svc) with
        {
            Resources = svc.Resources with { CpuLimit = null },
        };

        var result = _editor.ApplyServiceEdit(yaml, "app", edit);

        Assert.Null(result.Error);
        Assert.DoesNotContain("deploy:", result.Content);
        Assert.Equal("services:\n  app:\n    image: app:1\n", result.Content);
    }

    [Fact]
    public void LegacyResources_EditsTopLevelKey_Unquoted()
    {
        const string yaml = """
            services:
              app:
                image: app:1
                mem_limit: 256m
                pids_limit: 100
            """;
        var svc = Service(yaml, "app");
        Assert.Equal("legacy", svc.Resources.Convention);
        var edit = EditFrom(svc) with
        {
            Resources = svc.Resources with { MemLimit = "512m", PidsLimit = "200" },
        };

        var result = _editor.ApplyServiceEdit(yaml, "app", edit);

        Assert.Null(result.Error);
        Assert.Contains("    mem_limit: 512m\n", result.Content);
        Assert.Contains("    pids_limit: 200", result.Content); // integer stays unquoted (no quotes)
        Assert.DoesNotContain("\"200\"", result.Content);
    }

    [Fact]
    public void TopLevelScalars_AddCpuSharesAndOom_RenderUnquoted()
    {
        var svc = Service(Fixture, "jellyfin");
        var edit = EditFrom(svc) with
        {
            Resources = svc.Resources with { CpuShares = 512, OomKillDisable = true, ShmSize = "64m" },
        };

        var result = _editor.ApplyServiceEdit(Fixture, "jellyfin", edit);

        Assert.Null(result.Error);
        Assert.Contains("cpu_shares: 512\n", result.Content);
        Assert.Contains("oom_kill_disable: true\n", result.Content); // bool unquoted
        Assert.Contains("shm_size: 64m\n", result.Content);
    }

    [Fact]
    public void Ulimits_AddsBlock_AsMappingForm()
    {
        var svc = Service(Fixture, "jellyfin");
        var edit = EditFrom(svc) with
        {
            Resources = svc.Resources with
            {
                Ulimits = new[] { new ComposeUlimit("nofile", 20000, 40000), new ComposeUlimit("nproc", 65535, 65535) },
            },
        };

        var result = _editor.ApplyServiceEdit(Fixture, "jellyfin", edit);

        Assert.Null(result.Error);
        var ulimits = ResourcesOf(result.Content!, "jellyfin").Ulimits;
        Assert.Collection(ulimits,
            u => { Assert.Equal("nofile", u.Name); Assert.Equal(20000, u.Soft); Assert.Equal(40000, u.Hard); },
            u => { Assert.Equal("nproc", u.Name); Assert.Equal(65535, u.Soft); Assert.Equal(65535, u.Hard); });
    }

    [Fact]
    public void Resources_Unchanged_IsZeroDiff()
    {
        const string yaml = """
            services:
              app:
                image: app:1
                oom_kill_disable: yes
                deploy:
                  resources:
                    limits:
                      cpus: "0.50"
            """;
        // Round-trip exactly what the parser returned — nothing changed.
        var edit = EditFrom(Service(yaml, "app"));

        var result = _editor.ApplyServiceEdit(yaml, "app", edit);

        Assert.Null(result.Error);
        Assert.False(result.Changed); // `yes` must not normalise to `true`, "0.50" must not reflow
        Assert.Equal(yaml, result.Content);
    }

    [Fact]
    public void AnchoredDeployResources_RefusesTheEdit()
    {
        const string yaml = """
            services:
              app:
                image: app:1
                deploy:
                  resources: &res
                    limits:
                      cpus: "1"
            """;
        var svc = Service(yaml, "app");
        var edit = EditFrom(svc) with { Resources = svc.Resources with { CpuLimit = "2" } };

        var result = _editor.ApplyServiceEdit(yaml, "app", edit);

        Assert.Null(result.Content);
        Assert.Contains("anchor", result.Error);
    }

    // ── V7.3 — top-level resource CRUD ───────────────────────────────────────

    private static ComposeResourceEdit NetworkEdit(
        string name, bool external = false, string? driver = null, string? subnet = null,
        string? gateway = null, string? nameOverride = null,
        IReadOnlyList<ComposeEnvVar>? driverOpts = null) =>
        new(ComposeResourceKind.Network, name, external, nameOverride, driver, subnet, gateway,
            File: null, DriverOpts: driverOpts ?? Array.Empty<ComposeEnvVar>());

    [Fact]
    public void AddNetwork_ToExistingSection_PreservesSiblingEntryAndComments()
    {
        var result = _editor.ApplyResourceEdit(Fixture,
            NetworkEdit("backend", driver: "bridge", subnet: "172.30.0.0/24", gateway: "172.30.0.1"));

        Assert.Null(result.Error);
        Assert.True(result.Changed);
        // The pre-existing `default:` entry and its `name:` survive untouched.
        Assert.Contains("  default:\n    name: media-net", result.Content);
        // The new entry lands under the section with its ipam block.
        Assert.Contains("  backend:\n    driver: bridge\n    ipam:\n      config:\n        - subnet: 172.30.0.0/24\n          gateway: 172.30.0.1", result.Content);
    }

    [Fact]
    public void AddSection_WhenAbsent_AppendsWholeTopLevelBlock()
    {
        const string yaml = "services:\n  web:\n    image: nginx\n";

        var result = _editor.ApplyResourceEdit(yaml,
            new ComposeResourceEdit(ComposeResourceKind.Volume, "data", false, null, "local",
                null, null, null, Array.Empty<ComposeEnvVar>()));

        Assert.Null(result.Error);
        Assert.True(result.Changed);
        Assert.Equal("services:\n  web:\n    image: nginx\nvolumes:\n  data:\n    driver: local", result.Content);
    }

    [Fact]
    public void EditNetwork_RewritesOnlyThatEntry()
    {
        const string yaml = """
            services:
              web:
                image: nginx
            networks:
              frontend:
                driver: bridge
                ipam:
                  config:
                    - subnet: 172.20.0.0/24
              backend:
                driver: bridge
            """;

        var result = _editor.ApplyResourceEdit(yaml,
            NetworkEdit("frontend", driver: "bridge", subnet: "172.21.0.0/24"));

        Assert.Null(result.Error);
        Assert.True(result.Changed);
        Assert.Contains("subnet: 172.21.0.0/24", result.Content);
        Assert.DoesNotContain("172.20.0.0/24", result.Content);
        // The sibling entry is untouched.
        Assert.Contains("  backend:\n    driver: bridge", result.Content);
    }

    [Fact]
    public void RemoveNetwork_OneOfMany_RemovesOnlyThatEntry()
    {
        const string yaml = """
            services:
              web:
                image: nginx
            networks:
              frontend:
                driver: bridge
              backend:
                driver: bridge
            """;

        var result = _editor.RemoveResource(yaml, ComposeResourceKind.Network, "frontend");

        Assert.Null(result.Error);
        Assert.True(result.Changed);
        Assert.DoesNotContain("frontend:", result.Content);
        Assert.Contains("networks:\n  backend:", result.Content);
    }

    [Fact]
    public void RemoveNetwork_LastEntry_RemovesWholeSection()
    {
        const string yaml = "services:\n  web:\n    image: nginx\nnetworks:\n  frontend:\n    driver: bridge\n";

        var result = _editor.RemoveResource(yaml, ComposeResourceKind.Network, "frontend");

        Assert.Null(result.Error);
        Assert.True(result.Changed);
        Assert.DoesNotContain("networks:", result.Content);
        Assert.Equal("services:\n  web:\n    image: nginx\n", result.Content);
    }

    [Fact]
    public void EditResource_NoChange_ReturnsUnchanged()
    {
        const string yaml = "services:\n  web:\n    image: nginx\nnetworks:\n  frontend:\n    driver: bridge\n";

        var result = _editor.ApplyResourceEdit(yaml, NetworkEdit("frontend", driver: "bridge"));

        Assert.False(result.Changed);
        Assert.Equal(yaml, result.Content);
    }

    [Fact]
    public void AddExternalVolume_WritesExternalAndName()
    {
        const string yaml = "services:\n  web:\n    image: nginx\nvolumes:\n  data:\n    driver: local\n";

        var result = _editor.ApplyResourceEdit(yaml,
            new ComposeResourceEdit(ComposeResourceKind.Volume, "shared", true, "real_shared",
                null, null, null, null, Array.Empty<ComposeEnvVar>()));

        Assert.Null(result.Error);
        Assert.Contains("  shared:\n    external: true\n    name: real_shared", result.Content);
        Assert.Contains("  data:\n    driver: local", result.Content);
    }

    [Fact]
    public void AddSecret_RequiresFileWhenNotExternal()
    {
        const string yaml = "services:\n  web:\n    image: nginx\n";

        var result = _editor.ApplyResourceEdit(yaml,
            new ComposeResourceEdit(ComposeResourceKind.Secret, "db_password", false, null,
                null, null, null, File: null, DriverOpts: Array.Empty<ComposeEnvVar>()));

        Assert.Null(result.Content);
        Assert.Contains("file path", result.Error);
    }

    [Fact]
    public void AddFileSecret_WritesFileEntry()
    {
        const string yaml = "services:\n  web:\n    image: nginx\n";

        var result = _editor.ApplyResourceEdit(yaml,
            new ComposeResourceEdit(ComposeResourceKind.Secret, "db_password", false, null,
                null, null, null, File: "./secrets/db.txt", DriverOpts: Array.Empty<ComposeEnvVar>()));

        Assert.Null(result.Error);
        Assert.Contains("secrets:\n  db_password:\n    file: ./secrets/db.txt", result.Content);
    }

    [Fact]
    public void AnchoredResourceEntry_RefusesTheEdit()
    {
        const string yaml = """
            services:
              web:
                image: nginx
            networks:
              frontend: &net
                driver: bridge
            """;

        var result = _editor.ApplyResourceEdit(yaml, NetworkEdit("frontend", driver: "overlay"));

        Assert.Null(result.Content);
        Assert.Contains("anchor", result.Error);
    }

    // ── V7.4 — AddService ─────────────────────────────────────────────────────

    /// <summary>A minimal new service (image only).</summary>
    private static ComposeServiceEdit FreshEdit(string? image) => new(
        image, null, Array.Empty<string>(), Array.Empty<string>(),
        Array.Empty<ComposeEnvVar>(), Array.Empty<ComposeEnvVar>(),
        null, null, null, null, ComposeResourceConstraints.Empty);

    [Fact]
    public void AddService_AppendsAfterLastService_PreservingEverythingElse()
    {
        var result = _editor.AddService(Fixture, "cache", FreshEdit("redis:7"));

        Assert.Null(result.Error);
        Assert.True(result.Changed);
        Assert.Equal(
            Fixture.Replace(
                "      - MYSQL_USER=jf\nnetworks:",
                "      - MYSQL_USER=jf\n  cache:\n    image: redis:7\nnetworks:"),
            result.Content);
    }

    [Fact]
    public void AddService_HonoursFourSpaceIndentation()
    {
        const string yaml = """
            services:
                web:
                    image: nginx:1.27
            """;

        var result = _editor.AddService(yaml, "api", FreshEdit("api:1"));

        Assert.Null(result.Error);
        // New entry sits at the existing services' 4-space column, 2-space body.
        Assert.Equal(yaml + "\n    api:\n      image: api:1", result.Content);
    }

    [Fact]
    public void AddService_RendersAllSetFields()
    {
        var edit = new ComposeServiceEdit(
            Image: "ghcr.io/owner/app:1.2",
            Restart: "unless-stopped",
            Ports: new[] { "8080:80" },
            Volumes: new[] { "./data:/data" },
            Environment: new[] { new ComposeEnvVar("TZ", "Europe/Kyiv") },
            Labels: new[] { new ComposeEnvVar("com.example.team", "infra") },
            Command: null, Entrypoint: null, User: "1000:1000", WorkingDir: "/app",
            Resources: ComposeResourceConstraints.Empty with { MemLimit = "512m", CpuLimit = "1.5" });

        var result = _editor.AddService(Fixture, "app", edit);

        Assert.Null(result.Error);
        Assert.Contains("  app:\n    image: ghcr.io/owner/app:1.2\n", result.Content);
        Assert.Contains("    restart: unless-stopped\n", result.Content);
        Assert.Contains("    ports:\n      - \"8080:80\"\n", result.Content);
        Assert.Contains("    volumes:\n      - ./data:/data\n", result.Content);
        Assert.Contains("    environment:\n      TZ: Europe/Kyiv\n", result.Content);
        Assert.Contains("    labels:\n      com.example.team: infra\n", result.Content);
        Assert.Contains("    user: \"1000:1000\"\n", result.Content);
        Assert.Contains("    working_dir: /app\n", result.Content);
        Assert.Contains("    deploy:\n      resources:\n        limits:\n          cpus: 1.5\n          memory: 512m", result.Content);

        // The whole result must still parse as valid Compose.
        Assert.NotNull(new ComposeFileParser().Parse(result.Content!).Project);
    }

    [Fact]
    public void AddService_DuplicateName_IsRefused()
    {
        var result = _editor.AddService(Fixture, "db", FreshEdit("redis:7"));

        Assert.Null(result.Content);
        Assert.Contains("already exists", result.Error);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("bad/name")]
    [InlineData("")]
    public void AddService_InvalidName_IsRefused(string name)
    {
        var result = _editor.AddService(Fixture, name, FreshEdit("redis:7"));

        Assert.Null(result.Content);
        Assert.Contains("must match", result.Error);
    }

    [Fact]
    public void AddService_MissingImage_IsRefused()
    {
        var result = _editor.AddService(Fixture, "cache", FreshEdit(null));

        Assert.Null(result.Content);
        Assert.Contains("image", result.Error);
    }

    // ── V7.4.1 — CreateFile ─────────────────────────────────────────────────────

    [Fact]
    public void CreateFile_BuildsNameAndServicesWithFirstService()
    {
        var result = _editor.CreateFile("myapp", "web", FreshEdit("nginx:1.27"));

        Assert.Null(result.Error);
        Assert.Equal("name: myapp\nservices:\n  web:\n    image: nginx:1.27\n", result.Content);
        // And it parses back as a valid project named after the file.
        var parsed = new ComposeFileParser().Parse(result.Content!).Project;
        Assert.NotNull(parsed);
        Assert.Equal("myapp", parsed!.ProjectName);
        Assert.Equal("web", parsed.Services.Single().Name);
    }

    [Theory]
    [InlineData("MyApp")]   // uppercase
    [InlineData("-bad")]    // leading hyphen
    [InlineData("a b")]     // space
    public void CreateFile_InvalidProjectName_IsRefused(string name)
    {
        var result = _editor.CreateFile(name, "web", FreshEdit("nginx:1.27"));

        Assert.Null(result.Content);
        Assert.Contains("must match", result.Error);
    }

    [Fact]
    public void CreateFile_MissingImage_IsRefused()
    {
        var result = _editor.CreateFile("myapp", "web", FreshEdit(null));

        Assert.Null(result.Content);
        Assert.Contains("image", result.Error);
    }
}
