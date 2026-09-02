// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.IO;

namespace MagicMouseTray;

// Resolve allowlisted diagnostic scripts next to the exe or by walking up
// to scripts/ and the repo root (same depth as FindKeyboardPatchScript).
internal static class DiagnosticScripts
{
    internal const string CaptureState = "capture-state.ps1";
    internal const string DiagnoseDriver = "diagnose-driver.ps1";
    internal const string BtStackSnapshot = "mm-bt-stack-snapshot.ps1";
    internal const string DevMgrDump = "mm-devmgr-dump.ps1";

    internal const string CaptureStateLabel = "Run capture-state.ps1";
    internal const string DiagnoseDriverLabel = "Run diagnose-driver.ps1";
    internal const string BtStackSnapshotLabel = "Run mm-bt-stack-snapshot.ps1";
    internal const string DevMgrDumpLabel = "Run mm-devmgr-dump.ps1";

    internal static readonly string[] MenuScriptNames =
    [
        CaptureState,
        DiagnoseDriver,
        BtStackSnapshot,
        DevMgrDump,
    ];

    internal static string? Find(string fileName, string? startDirectory = null)
    {
        if (!IsSafeFileName(fileName))
            return null;

        var start = string.IsNullOrEmpty(startDirectory)
            ? AppContext.BaseDirectory
            : startDirectory;

        string[] immediate =
        [
            Path.Combine(start, fileName),
            Path.Combine(start, "scripts", fileName),
        ];
        foreach (var p in immediate)
        {
            if (File.Exists(p))
                return p;
        }

        var dir = new DirectoryInfo(start);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var atRoot = Path.Combine(dir.FullName, fileName);
            if (File.Exists(atRoot))
                return atRoot;
            var inScripts = Path.Combine(dir.FullName, "scripts", fileName);
            if (File.Exists(inScripts))
                return inScripts;
        }

        return null;
    }

    // One stack-dump slot: snapshot wins; devmgr only if snapshot is absent.
    internal static (string Label, string Path)? FindStackDump(string? startDirectory = null)
    {
        var snap = Find(BtStackSnapshot, startDirectory);
        if (snap is not null)
            return (BtStackSnapshotLabel, snap);
        var dump = Find(DevMgrDump, startDirectory);
        if (dump is not null)
            return (DevMgrDumpLabel, dump);
        return null;
    }

    internal static ProcessStartInfo StartInfo(string scriptPath)
    {
        var dir = Path.GetDirectoryName(scriptPath);
        return new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -NoExit -File \"{scriptPath}\"",
            UseShellExecute = true,
            WorkingDirectory = string.IsNullOrEmpty(dir) ? "" : dir,
        };
    }

    static bool IsSafeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;
        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;
        if (fileName.Contains("..", StringComparison.Ordinal))
            return false;
        return true;
    }
}
