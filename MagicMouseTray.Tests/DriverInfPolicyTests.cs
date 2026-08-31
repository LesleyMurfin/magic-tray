// SPDX-License-Identifier: MIT
using Xunit;

namespace MagicMouseTray.Tests;

// The KMDF package lives in driver/. These checks keep the tray/driver split
// and the live 0323-only bind visible in CI without building the .sys.
public class DriverInfPolicyTests
{
    static string FindRepoFile(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var parts = new[] { dir.FullName }.Concat(relativeParts).ToArray();
            var candidate = Path.Combine(parts);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(string.Join("/", relativeParts));
    }

    [Fact]
    public void KmdfProjectFiles_ArePresent()
    {
        Assert.True(File.Exists(FindRepoFile("driver", "MagicMouseDriver.vcxproj")));
        Assert.True(File.Exists(FindRepoFile("driver", "MagicMouseDriver.inf")));
        Assert.True(File.Exists(FindRepoFile("driver", "Driver.c")));
        Assert.True(File.Exists(FindRepoFile("driver", "InputHandler.c")));
        Assert.True(File.Exists(FindRepoFile("driver", "HidDescriptor.c")));
        Assert.True(File.Exists(FindRepoFile("driver", "GestureEngine.c")));
    }

    [Fact]
    public void Inf_Targets0323SoleFilter_Not030D_NotDualFilter()
    {
        var inf = File.ReadAllText(FindRepoFile("driver", "MagicMouseDriver.inf"));

        Assert.Contains("PID&0323", inf, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PID&030D", inf, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PID&0310", inf, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("\"LowerFilters\",0x00010000,\"MagicMouseDriver\"", inf);
        Assert.DoesNotContain("MagicMouseDriver,applewirelessmouse", inf, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("applewirelessmouse,MagicMouseDriver", inf, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Vcxproj_IsKmdfHome_WithoutLeftoverInstallHooks()
    {
        var proj = File.ReadAllText(FindRepoFile("driver", "MagicMouseDriver.vcxproj"));

        Assert.Contains("MagicMouseDriver", proj);
        Assert.Contains("Driver.c", proj);
        Assert.Contains("MagicMouseDriver.inf", proj);
        Assert.Contains("KMDF", proj, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mm-dev.ps1", proj, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\mm3-presign", proj, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("install-driver.ps1", proj, StringComparison.OrdinalIgnoreCase);
    }
}
