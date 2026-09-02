// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

public class DiagnosticScriptsTests
{
    [Fact]
    public void Find_CaptureState_FromNestedBin_WalksUpToScripts()
    {
        using var tree = FakeTree.Create();
        var cap = tree.WriteScripts("capture-state.ps1");
        tree.WriteScripts("mm-state-flip.ps1");
        tree.WriteScripts("mm-rev-eng.ps1");

        Assert.Equal(cap, DiagnosticScripts.Find(DiagnosticScripts.CaptureState, tree.Start));
        Assert.Equal(cap, DiagnosticScripts.Find("capture-state.ps1", tree.Start));
    }

    [Fact]
    public void Find_DiagnoseDriver_AtRepoRoot_FromNestedStart()
    {
        using var tree = FakeTree.Create();
        var diag = tree.WriteRoot("diagnose-driver.ps1");

        Assert.Equal(diag, DiagnosticScripts.Find(DiagnosticScripts.DiagnoseDriver, tree.Start));
    }

    [Fact]
    public void Find_CaptureState_NextToStart_WithoutScriptsFolder()
    {
        using var tree = FakeTree.Create(createScripts: false);
        var cap = Path.Combine(tree.Start, "capture-state.ps1");
        File.WriteAllText(cap, "# next to exe");

        Assert.Equal(cap, DiagnosticScripts.Find(DiagnosticScripts.CaptureState, tree.Start));
    }

    [Fact]
    public void FindStackDump_PrefersSnapshotOverDevMgr()
    {
        using var tree = FakeTree.Create();
        var snap = tree.WriteScripts("mm-bt-stack-snapshot.ps1");
        tree.WriteScripts("mm-devmgr-dump.ps1");

        var found = DiagnosticScripts.FindStackDump(tree.Start);
        Assert.NotNull(found);
        Assert.Equal(DiagnosticScripts.BtStackSnapshotLabel, found.Value.Label);
        Assert.Equal(snap, found.Value.Path);
        Assert.NotEqual(
            DiagnosticScripts.Find(DiagnosticScripts.DevMgrDump, tree.Start),
            found.Value.Path);
    }

    [Fact]
    public void FindStackDump_UsesDevMgrWhenSnapshotMissing()
    {
        using var tree = FakeTree.Create();
        var dump = tree.WriteScripts("mm-devmgr-dump.ps1");

        var found = DiagnosticScripts.FindStackDump(tree.Start);
        Assert.NotNull(found);
        Assert.Equal(DiagnosticScripts.DevMgrDumpLabel, found.Value.Label);
        Assert.Equal(dump, found.Value.Path);
    }

    [Fact]
    public void Find_MissingName_IsNull()
    {
        using var tree = FakeTree.Create();
        Assert.Null(DiagnosticScripts.Find("capture-state.ps1", tree.Start));
        Assert.Null(DiagnosticScripts.Find("missing.ps1", tree.Start));
        Assert.Null(DiagnosticScripts.FindStackDump(tree.Start));
    }

    [Fact]
    public void Allowlist_IsExistingScripts_NotDumpOrSkippedLabs()
    {
        Assert.Equal(
            new[]
            {
                "capture-state.ps1",
                "diagnose-driver.ps1",
                "mm-bt-stack-snapshot.ps1",
                "mm-devmgr-dump.ps1",
            },
            DiagnosticScripts.MenuScriptNames);

        foreach (var name in DiagnosticScripts.MenuScriptNames)
        {
            Assert.DoesNotContain("mm-rev-eng", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("mm-magicutilities-capture", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("mm-state-flip", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("etw", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("driver-state", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PATH-A", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void StartInfo_IsPowershellFile_NotDumpFormat()
    {
        var path = @"C:\repo\scripts\capture-state.ps1";
        var psi = DiagnosticScripts.StartInfo(path);
        Assert.Equal("powershell.exe", psi.FileName);
        Assert.Contains("-NoProfile", psi.Arguments, StringComparison.Ordinal);
        Assert.Contains("-ExecutionPolicy Bypass", psi.Arguments, StringComparison.Ordinal);
        Assert.Contains("-NoExit", psi.Arguments, StringComparison.Ordinal);
        Assert.Contains("-File", psi.Arguments, StringComparison.Ordinal);
        Assert.Contains("capture-state.ps1", psi.Arguments, StringComparison.Ordinal);
        Assert.Equal("-NoProfile -ExecutionPolicy Bypass -NoExit -File \"C:\\repo\\scripts\\capture-state.ps1\"", psi.Arguments);
        Assert.DoesNotContain("driver-state.txt", psi.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Format", psi.Arguments, StringComparison.Ordinal);
        Assert.True(psi.UseShellExecute);
    }

    [Fact]
    public void Labels_AreScriptNames_NoPathA()
    {
        Assert.Equal("Run capture-state.ps1", DiagnosticScripts.CaptureStateLabel);
        Assert.Equal("Run diagnose-driver.ps1", DiagnosticScripts.DiagnoseDriverLabel);
        Assert.Equal("Run mm-bt-stack-snapshot.ps1", DiagnosticScripts.BtStackSnapshotLabel);
        Assert.Equal("Run mm-devmgr-dump.ps1", DiagnosticScripts.DevMgrDumpLabel);

        Assert.DoesNotContain("PATH-A", DiagnosticScripts.CaptureStateLabel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PATH-A", DiagnosticScripts.DiagnoseDriverLabel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PATH-A", DiagnosticScripts.BtStackSnapshotLabel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PATH-A", DiagnosticScripts.DevMgrDumpLabel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Show driver state", DiagnosticScripts.CaptureStateLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Find_RejectsPathTraversal()
    {
        using var tree = FakeTree.Create();
        tree.WriteScripts("capture-state.ps1");
        Assert.Null(DiagnosticScripts.Find(@"..\capture-state.ps1", tree.Start));
        Assert.Null(DiagnosticScripts.Find("scripts/capture-state.ps1", tree.Start));
        Assert.Null(DiagnosticScripts.Find("", tree.Start));
    }

    sealed class FakeTree : IDisposable
    {
        public string Root { get; }
        public string Start { get; }
        readonly string _scripts;

        FakeTree(string root, string start, string scripts)
        {
            Root = root;
            Start = start;
            _scripts = scripts;
        }

        public static FakeTree Create(bool createScripts = true)
        {
            var root = Path.Combine(Path.GetTempPath(), "mm-diag-" + Guid.NewGuid().ToString("N"));
            var start = Path.Combine(root, "MagicMouseTray", "bin", "Release", "net8", "win-x64");
            var scripts = Path.Combine(root, "scripts");
            Directory.CreateDirectory(start);
            if (createScripts)
                Directory.CreateDirectory(scripts);
            return new FakeTree(root, start, scripts);
        }

        public string WriteScripts(string name)
        {
            Directory.CreateDirectory(_scripts);
            var path = Path.Combine(_scripts, name);
            File.WriteAllText(path, "# " + name);
            return path;
        }

        public string WriteRoot(string name)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllText(path, "# " + name);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch { }
        }
    }
}
