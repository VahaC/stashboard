using Stashboard.Infrastructure.Docker;

namespace Stashboard.Tests.Infrastructure.Docker;

/// <summary>
/// V7.0 — unit tests for <see cref="ComposeFileParser"/>: the viewer subset
/// (services, top-level resources, deploy.resources), short/long syntax
/// normalisation, simple-alias resolution, and the unsupported-feature
/// detection (<c>x-*</c> / <c>extends</c> / merge keys) that powers the
/// read-only banner.
/// </summary>
public class ComposeFileParserTests
{
    private readonly ComposeFileParser _parser = new();

    [Fact]
    public void Parse_FullProject_MapsServicesAndTopLevelResources()
    {
        const string yaml = """
            name: homelab
            services:
              web:
                image: nginx:1.27
                container_name: my-nginx
                restart: unless-stopped
                ports:
                  - "8080:80"
                  - "443:443/tcp"
                volumes:
                  - ./conf:/etc/nginx:ro
                  - data:/var/lib/nginx
                environment:
                  TZ: Europe/Kyiv
                  DEBUG: "1"
                env_file: .env
                depends_on:
                  - db
                networks:
                  - frontend
                deploy:
                  resources:
                    limits:
                      cpus: "0.50"
                      memory: 512M
                    reservations:
                      cpus: "0.25"
                      memory: 128M
              db:
                image: mariadb:11
            networks:
              frontend:
              backend:
                driver: bridge
            volumes:
              data:
            secrets:
              db_password:
                file: ./db_password.txt
            configs:
              site_conf:
                file: ./site.conf
            """;

        var result = _parser.Parse(yaml);

        Assert.Null(result.Error);
        Assert.NotNull(result.Project);
        var project = result.Project!;
        Assert.Equal("homelab", project.ProjectName);
        Assert.Empty(project.UnsupportedFeatures);

        Assert.Equal(2, project.Services.Count);
        var web = project.Services[0];
        Assert.Equal("web", web.Name);
        Assert.Equal("nginx:1.27", web.Image);
        Assert.Equal("my-nginx", web.ContainerName);
        Assert.Equal("unless-stopped", web.Restart);
        Assert.Equal(new[] { "8080:80", "443:443/tcp" }, web.Ports);
        Assert.Equal(new[] { "./conf:/etc/nginx:ro", "data:/var/lib/nginx" }, web.Volumes);
        Assert.Collection(web.Environment,
            e => { Assert.Equal("TZ", e.Name); Assert.Equal("Europe/Kyiv", e.Value); },
            e => { Assert.Equal("DEBUG", e.Name); Assert.Equal("1", e.Value); });
        Assert.Equal(new[] { ".env" }, web.EnvFiles);
        Assert.Equal(new[] { "db" }, web.DependsOn);
        Assert.Equal(new[] { "frontend" }, web.Networks);
        Assert.Equal("deploy", web.Resources.Convention);
        Assert.Equal("0.50", web.Resources.CpuLimit);
        Assert.Equal("512M", web.Resources.MemLimit);
        Assert.Equal("0.25", web.Resources.CpuReservation);
        Assert.Equal("128M", web.Resources.MemReservation);

        var db = project.Services[1];
        Assert.Equal("mariadb:11", db.Image);
        Assert.Null(db.Resources.CpuLimit);
        Assert.Equal("deploy", db.Resources.Convention);

        Assert.Equal(new[] { "frontend", "backend" }, project.Networks.Select(n => n.Name));
        Assert.Equal(new[] { "data" }, project.Volumes.Select(v => v.Name));
        Assert.Equal(new[] { "db_password" }, project.Secrets.Select(s => s.Name));
        Assert.Equal(new[] { "site_conf" }, project.Configs.Select(c => c.Name));
    }

    [Fact]
    public void Parse_LegacyResources_DetectsLegacyConventionAndTopLevelKeys()
    {
        const string yaml = """
            services:
              app:
                image: app:1
                cpus: 1.5
                mem_limit: 512m
                mem_reservation: 256m
                pids_limit: 200
                cpu_shares: 512
                oom_kill_disable: true
                oom_score_adj: -500
                shm_size: 64m
                ulimits:
                  nproc: 65535
                  nofile:
                    soft: 20000
                    hard: 40000
            """;

        var r = new ComposeFileParser().Parse(yaml).Project!.Services.Single().Resources;

        Assert.Equal("legacy", r.Convention);
        Assert.Equal("1.5", r.CpuLimit);
        Assert.Equal("512m", r.MemLimit);
        Assert.Equal("256m", r.MemReservation);
        Assert.Equal("200", r.PidsLimit);
        Assert.Null(r.CpuReservation);
        Assert.Equal(512, r.CpuShares);
        Assert.True(r.OomKillDisable);
        Assert.Equal(-500, r.OomScoreAdj);
        Assert.Equal("64m", r.ShmSize);
        Assert.Collection(r.Ulimits,
            u => { Assert.Equal("nproc", u.Name); Assert.Equal(65535, u.Soft); Assert.Equal(65535, u.Hard); },
            u => { Assert.Equal("nofile", u.Name); Assert.Equal(20000, u.Soft); Assert.Equal(40000, u.Hard); });
    }

    [Fact]
    public void Parse_DeployPids_AndNoResources_Defaults()
    {
        const string yaml = """
            services:
              a:
                image: a:1
                deploy:
                  resources:
                    limits:
                      cpus: "2"
                      pids: 100
              b:
                image: b:1
            """;

        var project = new ComposeFileParser().Parse(yaml).Project!;
        var a = project.Services.Single(s => s.Name == "a").Resources;
        Assert.Equal("deploy", a.Convention);
        Assert.Equal("2", a.CpuLimit);
        Assert.Equal("100", a.PidsLimit);
        // A service declaring nothing defaults to the modern form for a first edit.
        var b = project.Services.Single(s => s.Name == "b").Resources;
        Assert.Equal("deploy", b.Convention);
        Assert.Null(b.CpuLimit);
    }

    [Fact]
    public void Parse_GpuDeviceReservations_ReportedUnsupported()
    {
        const string yaml = """
            services:
              ml:
                image: ml:1
                deploy:
                  resources:
                    reservations:
                      devices:
                        - capabilities: [gpu]
            """;

        var result = new ComposeFileParser().Parse(yaml);
        Assert.Contains(result.Project!.UnsupportedFeatures, f => f.Contains("device reservations"));
    }

    [Fact]
    public void Parse_LongSyntax_NormalisesPortsVolumesDependsOnAndEnvList()
    {
        const string yaml = """
            services:
              app:
                image: app:1
                ports:
                  - target: 80
                    published: 8080
                    protocol: tcp
                  - target: 9000
                volumes:
                  - type: bind
                    source: ./data
                    target: /data
                    read_only: true
                  - type: tmpfs
                    target: /tmp/cache
                environment:
                  - KEY=value
                  - PASSTHROUGH
                env_file:
                  - .env
                  - .env.local
                depends_on:
                  db:
                    condition: service_healthy
            """;

        var result = _parser.Parse(yaml);

        Assert.NotNull(result.Project);
        var app = result.Project!.Services.Single();
        Assert.Equal(new[] { "8080:80/tcp", "9000" }, app.Ports);
        Assert.Equal(new[] { "./data:/data:ro", "tmpfs:/tmp/cache" }, app.Volumes);
        Assert.Collection(app.Environment,
            e => { Assert.Equal("KEY", e.Name); Assert.Equal("value", e.Value); },
            e => { Assert.Equal("PASSTHROUGH", e.Name); Assert.Null(e.Value); });
        Assert.Equal(new[] { ".env", ".env.local" }, app.EnvFiles);
        Assert.Equal(new[] { "db" }, app.DependsOn);
    }

    [Fact]
    public void Parse_SimpleAlias_ResolvesWithoutWarning()
    {
        // A plain anchor/alias pair on a scalar is resolved by the YAML loader
        // and must NOT trigger the read-only banner.
        const string yaml = """
            services:
              a:
                image: app:1
                restart: &policy unless-stopped
              b:
                image: app:2
                restart: *policy
            """;

        var result = _parser.Parse(yaml);

        Assert.NotNull(result.Project);
        var project = result.Project!;
        Assert.Empty(project.UnsupportedFeatures);
        Assert.Equal("unless-stopped", project.Services[1].Restart);
    }

    [Fact]
    public void Parse_MergeKeyExtendsAndExtensions_AreReportedAsUnsupported()
    {
        const string yaml = """
            x-defaults: &defaults
              restart: unless-stopped
            services:
              web:
                <<: *defaults
                image: nginx:1.27
                x-custom: 42
              worker:
                extends:
                  service: web
            """;

        var result = _parser.Parse(yaml);

        Assert.NotNull(result.Project);
        var project = result.Project!;
        Assert.Contains("extension field 'x-defaults'", project.UnsupportedFeatures);
        Assert.Contains("YAML merge key (<<) in service 'web'", project.UnsupportedFeatures);
        Assert.Contains("extension field 'x-custom' in service 'web'", project.UnsupportedFeatures);
        Assert.Contains("'extends' in service 'worker'", project.UnsupportedFeatures);
        // The data that IS parseable still comes through.
        Assert.Equal("nginx:1.27", project.Services[0].Image);
    }

    [Fact]
    public void Parse_V71EditableFields_MapsLabelsCommandEntrypointUserWorkingDir()
    {
        const string yaml = """
            services:
              app:
                image: app:1
                user: "1000:1000"
                working_dir: /srv/app
                command: serve --port 8080
                entrypoint:
                  - /entry.sh
                  - --flag
                labels:
                  com.example.team: media
                  com.example.tier: "2"
              worker:
                image: app:1
                labels:
                  - com.example.team=batch
                  - com.example.flagonly
            """;

        var result = _parser.Parse(yaml);

        Assert.NotNull(result.Project);
        var app = result.Project!.Services[0];
        Assert.Equal("1000:1000", app.User);
        Assert.Equal("/srv/app", app.WorkingDir);
        Assert.Equal("serve --port 8080", app.Command);
        // Exec (list) form folds into a JSON-style flow string.
        Assert.Equal("[\"/entry.sh\", \"--flag\"]", app.Entrypoint);
        Assert.Collection(app.Labels,
            l => { Assert.Equal("com.example.team", l.Name); Assert.Equal("media", l.Value); },
            l => { Assert.Equal("com.example.tier", l.Name); Assert.Equal("2", l.Value); });

        var worker = result.Project.Services[1];
        Assert.Collection(worker.Labels,
            l => { Assert.Equal("com.example.team", l.Name); Assert.Equal("batch", l.Value); },
            l => { Assert.Equal("com.example.flagonly", l.Name); Assert.Null(l.Value); });
        Assert.Null(worker.Command);
        Assert.Null(worker.User);
    }

    [Fact]
    public void Parse_ServiceWithNoBody_YieldsEmptyService()
    {
        const string yaml = """
            services:
              placeholder:
            """;

        var result = _parser.Parse(yaml);

        Assert.NotNull(result.Project);
        var svc = result.Project!.Services.Single();
        Assert.Equal("placeholder", svc.Name);
        Assert.Null(svc.Image);
        Assert.Empty(svc.Ports);
    }

    [Fact]
    public void Parse_InvalidYaml_ReturnsError()
    {
        var result = _parser.Parse("services:\n  web:\n   image: [unclosed");

        Assert.Null(result.Project);
        Assert.StartsWith("YAML parse error:", result.Error);
    }

    [Fact]
    public void Parse_EmptyDocument_ReturnsError()
    {
        var result = _parser.Parse("");

        Assert.Null(result.Project);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Parse_NoServicesMap_ReturnsError()
    {
        var result = _parser.Parse("version: \"3.8\"\nvolumes:\n  data:\n");

        Assert.Null(result.Project);
        Assert.Equal("The Compose file has no 'services' map.", result.Error);
    }

    // ── V7.3 — top-level resource options ────────────────────────────────────

    [Fact]
    public void Parse_TopLevelResources_MapsNetworkVolumeSecretConfigOptions()
    {
        const string yaml = """
            services:
              web:
                image: nginx
            networks:
              frontend:
                driver: bridge
                driver_opts:
                  com.docker.network.bridge.name: br-front
                ipam:
                  config:
                    - subnet: 172.20.0.0/24
                      gateway: 172.20.0.1
              edge:
                external: true
                name: real_edge
            volumes:
              db_data:
                driver: local
                name: pg
              cache:
                external: true
            secrets:
              db_password:
                file: ./secrets/db.txt
              api_key:
                external: true
                name: prod_api_key
            configs:
              site_conf:
                file: ./nginx.conf
            """;

        var project = _parser.Parse(yaml).Project!;

        var frontend = project.Networks.Single(n => n.Name == "frontend");
        Assert.False(frontend.External);
        Assert.Equal("bridge", frontend.Driver);
        Assert.Equal("172.20.0.0/24", frontend.Subnet);
        Assert.Equal("172.20.0.1", frontend.Gateway);
        Assert.Equal("br-front", Assert.Single(frontend.DriverOpts).Value);

        var edge = project.Networks.Single(n => n.Name == "edge");
        Assert.True(edge.External);
        Assert.Equal("real_edge", edge.NameOverride);

        var dbData = project.Volumes.Single(v => v.Name == "db_data");
        Assert.Equal("local", dbData.Driver);
        Assert.Equal("pg", dbData.NameOverride);
        Assert.True(project.Volumes.Single(v => v.Name == "cache").External);

        Assert.Equal("./secrets/db.txt", project.Secrets.Single(s => s.Name == "db_password").File);
        var apiKey = project.Secrets.Single(s => s.Name == "api_key");
        Assert.True(apiKey.External);
        Assert.Equal("prod_api_key", apiKey.NameOverride);

        Assert.Equal("./nginx.conf", project.Configs.Single(c => c.Name == "site_conf").File);
        Assert.Empty(project.UnsupportedFeatures);
    }

    [Fact]
    public void Parse_MultipleIpamConfigs_FlaggedUnsupported()
    {
        const string yaml = """
            services:
              web:
                image: nginx
            networks:
              multi:
                ipam:
                  config:
                    - subnet: 10.0.0.0/24
                    - subnet: 10.0.1.0/24
            """;

        var project = _parser.Parse(yaml).Project!;

        Assert.Contains(project.UnsupportedFeatures, f => f.Contains("multiple ipam.config"));
        // First subnet is still surfaced.
        Assert.Equal("10.0.0.0/24", project.Networks.Single().Subnet);
    }
}

