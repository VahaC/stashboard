using Stashboard.Infrastructure.Proxmox;

namespace Stashboard.Tests.Infrastructure.Proxmox;

/// <summary>
/// V6.8 — unit tests for the <c>sensors -j</c> parser behind the node card's
/// Sensors/Temperature tab. The SSH transport can't be mocked (it opens a real
/// <c>SshClient</c>), so we test the pure parsing step that turns the JSON the
/// host emits into flat temperature / fan readings.
/// </summary>
public class ProxmoxSensorsParseTests
{
    // A representative `sensors -j` document: a CPU package + cores chip, an
    // NVMe composite temp, and a super-IO chip exposing a fan.
    private const string SensorsJson = """
        {
          "coretemp-isa-0000": {
            "Adapter": "ISA adapter",
            "Package id 0": { "temp1_input": 45.0, "temp1_max": 80.0, "temp1_crit": 100.0 },
            "Core 0": { "temp2_input": 43.0, "temp2_max": 80.0, "temp2_crit": 100.0 }
          },
          "nvme-pci-0100": {
            "Adapter": "PCI adapter",
            "Composite": { "temp1_input": 38.8, "temp1_crit": 84.8 }
          },
          "nct6779-isa-0290": {
            "Adapter": "ISA adapter",
            "fan1": { "fan1_input": 1180.0 },
            "fan2": { "fan2_input": 0.0 },
            "in0": { "in0_input": 1.20 },
            "Vcore": { "in1_input": 0.95 },
            "power1": { "power1_input": 42.5 }
          }
        }
        """;

    [Fact]
    public void ParseSensors_ExtractsTemperaturesWithThresholds()
    {
        var readings = ProxmoxSshGuestInspector.ParseSensors(SensorsJson);

        var pkg = readings.Single(r => r.Label == "Package id 0");
        Assert.Equal("coretemp-isa-0000", pkg.Chip);
        Assert.Equal(45.0, pkg.TempC);
        Assert.Equal(80.0, pkg.HighC);
        Assert.Equal(100.0, pkg.CritC);
        Assert.Null(pkg.Rpm);

        var nvme = readings.Single(r => r.Label == "Composite");
        Assert.Equal(38.8, nvme.TempC);
        Assert.Null(nvme.HighC);          // no temp1_max in the source
        Assert.Equal(84.8, nvme.CritC);
    }

    [Fact]
    public void ParseSensors_ExtractsFanRpm()
    {
        var readings = ProxmoxSshGuestInspector.ParseSensors(SensorsJson);

        var fan = readings.Single(r => r.Label == "fan1");
        Assert.Equal(1180.0, fan.Rpm);
        Assert.Null(fan.TempC);

        // A stopped fan still reports a reading (0 RPM), not a dropped row.
        Assert.Contains(readings, r => r.Label == "fan2" && r.Rpm == 0.0);
    }

    [Fact]
    public void ParseSensors_ExtractsVoltageAndPower()
    {
        var readings = ProxmoxSshGuestInspector.ParseSensors(SensorsJson);

        // `in<n>_input` → volts; `power<n>_input` → watts. Neither carries a temp.
        var vcore = readings.Single(r => r.Label == "Vcore");
        Assert.Equal(0.95, vcore.Volts);
        Assert.Null(vcore.TempC);
        Assert.Null(vcore.Rpm);

        Assert.Contains(readings, r => r.Label == "in0" && r.Volts == 1.20);

        var power = readings.Single(r => r.Label == "power1");
        Assert.Equal(42.5, power.Watts);
        Assert.Null(power.TempC);
    }

    [Fact]
    public void ParseSensors_EmptyObject_ReturnsNoReadings()
    {
        Assert.Empty(ProxmoxSshGuestInspector.ParseSensors("{}"));
    }
}



