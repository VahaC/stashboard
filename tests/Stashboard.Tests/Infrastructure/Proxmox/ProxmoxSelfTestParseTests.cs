using Microsoft.Extensions.Logging.Abstractions;
using Stashboard.Core.Abstractions;
using Stashboard.Infrastructure.Proxmox;

namespace Stashboard.Tests.Infrastructure.Proxmox;

/// <summary>
/// V6.8.2 — unit tests for the SMART self-test collector: the pure
/// <c>smartctl -l selftest -A</c> parser (most-recent self-test row + the badged
/// critical counters) and the no-SSH "not available" path.
/// </summary>
public class ProxmoxSelfTestParseTests
{
    private const string SmartctlOutput = """
        smartctl 7.2 2020-12-30 r5155 [x86_64-linux] (local build)

        SMART Self-test log structure revision number 1
        Num  Test_Description    Status                  Remaining  LifeTime(hours)  LBA_of_first_error
        # 1  Extended offline    Completed without error       00%     12450         -
        # 2  Short offline       Completed without error       00%     12400         -

        SMART Attributes Data Structure revision number: 16
        Vendor Specific SMART Attributes with Thresholds:
        ID# ATTRIBUTE_NAME          FLAG     VALUE WORST THRESH TYPE      UPDATED  WHEN_FAILED RAW_VALUE
          5 Reallocated_Sector_Ct   0x0033   100   100   010    Pre-fail  Always       -       8
          9 Power_On_Hours          0x0032   088   088   000    Old_age   Always       -       12450
        197 Current_Pending_Sector  0x0012   100   100   000    Old_age   Always       -       0
        198 Offline_Uncorrectable   0x0030   100   100   000    Old_age   Offline      -       0
        """;

    [Fact]
    public void ParseSelfTest_ExtractsMostRecentTestAndCriticalCounters()
    {
        var r = ProxmoxSshGuestInspector.ParseSelfTest(SmartctlOutput);

        Assert.True(r.Available);
        Assert.Equal("Extended offline", r.LastTestType);
        Assert.Equal("Completed without error", r.LastTestStatus);
        Assert.Equal(12450, r.LastTestPowerOnHours);   // most-recent row only
        Assert.Equal(12450, r.PowerOnHours);
        Assert.Equal(8, r.ReallocatedSectors);
        Assert.Equal(0, r.PendingSectors);
        Assert.Equal(0, r.UncorrectableSectors);
    }

    [Fact]
    public void ParseSelfTest_FailedTest_KeepsStatusText()
    {
        const string failed = """
            Num  Test_Description    Status                  Remaining  LifeTime(hours)  LBA_of_first_error
            # 1  Extended offline    Completed: read failure       90%     5000         123456789
            """;
        var r = ProxmoxSshGuestInspector.ParseSelfTest(failed);
        Assert.Equal("Extended offline", r.LastTestType);
        Assert.Equal("Completed: read failure", r.LastTestStatus);
        Assert.Equal(5000, r.LastTestPowerOnHours);
    }

    [Fact]
    public void ParseSelfTest_NeverRun_HasNullStatus()
    {
        const string none = """
            No self-tests have been logged.  [To run self-tests, use: smartctl -t]
            ID# ATTRIBUTE_NAME          FLAG     VALUE WORST THRESH TYPE      UPDATED  WHEN_FAILED RAW_VALUE
              9 Power_On_Hours          0x0032   099   099   000    Old_age   Always       -       42
            """;
        var r = ProxmoxSshGuestInspector.ParseSelfTest(none);
        Assert.Null(r.LastTestStatus);
        Assert.Equal(42, r.PowerOnHours);
    }

    [Fact]
    public async Task ReadNodeDiskSelfTest_NoSsh_IsNotAvailable()
    {
        var inspector = new ProxmoxSshGuestInspector(NullLogger<ProxmoxSshGuestInspector>.Instance);
        var result = await inspector.ReadNodeDiskSelfTestAsync(NoSshProfile(), "/dev/sda");
        Assert.False(result.Available);
        Assert.NotNull(result.Error);
    }

    private static ProxmoxConnectionProfile NoSshProfile() =>
        new("https://pve.lan:8006", "pve", "root@pam!t", "secret", SkipTlsVerify: true, Ssh: null);
}


