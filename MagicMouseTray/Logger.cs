// SPDX-License-Identifier: MIT
using System.IO;
// GetCustomAttribute<T>() on Assembly is an extension method in
// System.Reflection.CustomAttributeExtensions — fully-qualifying the attribute
// type does NOT bring it into scope, so this using is load-bearing (CS1061).
using System.Reflection;
using System.Text.RegularExpressions;

namespace MagicMouseTray;

// File logger for diagnosing battery read failures in the headless tray app.
// Output: %APPDATA%\MagicMouseTray\debug.log  (rotates at 1MB → debug.log.1)
// Never throws — all I/O errors are silently swallowed.
internal static class Logger
{
    internal static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MagicMouseTray", "debug.log");

    internal static string LogDir => Path.GetDirectoryName(LogPath)!;

    const long MaxBytes = 1024 * 1024; // 1 MB rotation threshold
    static readonly object Lock = new();
    static string _appStartBanner = string.Empty;

    internal static void LogAppStart()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var ver = asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? asm.GetName().Version?.ToString() ?? "unknown";
        var fx = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        var os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        var pid = Environment.ProcessId;
        _appStartBanner = $"INFO APP_START version={ver} framework={fx} os=\"{os}\" pid={pid}";
        Log(_appStartBanner);
    }

    internal static void Log(string message)
    {
        message = new string(message.Where(c => !char.IsControl(c) || c == '\r' || c == '\n').ToArray());
        message = Regex.Replace(message, @"[^\x00-\x7F]", string.Empty);
        // Redact after the strips, so the token this writes is the token
        // BugReport.ReadLogTail later reads back out of the file.
        message = RedactMacs(message);
        try
        {
            lock (Lock)
            {
                var dir = Path.GetDirectoryName(LogPath)!;
                Directory.CreateDirectory(dir);

                if (File.Exists(LogPath) && new FileInfo(LogPath).Length >= MaxBytes)
                {
                    File.Move(LogPath, LogPath + ".1", overwrite: true);
                    if (!string.IsNullOrEmpty(_appStartBanner))
                    {
                        File.AppendAllText(LogPath,
                            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {_appStartBanner}{Environment.NewLine}", new System.Text.UTF8Encoding(true));
                    }
                }

                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}", new System.Text.UTF8Encoding(true));
            }
        }
        catch { }
    }

    // Canonical Bluetooth-address redaction, shared with BugReport.Redact so an
    // address redacted at this sink and one redacted for a PUBLIC issue are the
    // same token: two spellings of a redacted MAC would stop the same device
    // correlating across the two paths.
    //
    // Redaction sits at the sink, not at the emitting call sites (DRIVER_SDP in
    // DriverInstaller, SDP_PATCH and SDP_PATCH_FAILED in SdpPatchReader), for
    // two reasons: debug.log is handed to strangers directly, not only through
    // the BugReport path that already redacts; and a policy at the sink is the
    // only one the next call site cannot forget to apply.
    //
    // BugReport's third rule - bare 12 hex with non-hex neighbours - is
    // deliberately NOT reused here. The Bluetooth base UUID tail 00805f9b34fb
    // (DriverInstaller.HidUuidPrefix, DeviceDiagReader.BtTransportGuid) is 12
    // hex preceded by '-', which that rule's lookbehind does not exclude, so it
    // rewrites
    //   BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&000205ac_PID&0239
    // to ...-8000-<mac>}..., i.e. it corrupts the primary diagnostic identifier
    // on every line carrying an instance ID (DEVICE_DIAG_POINTER path=,
    // DISCOVER_SKIP_DUPLICATE path=, STOCK_DRIVER_PROBE_FAILED id=). Harmless
    // in BugReport's one-way scrub for a public issue; a real regression in the
    // log that docs/TEST-PLAN.md rows are read off.
    //
    // So every rule below is anchored on a delimiter that only a real address
    // carries, and each yields exactly what BugReport.Redact yields for the
    // same input (its rules 1 and 2 verbatim; for the '&'/'_' and 'mac=' forms
    // both neighbours are non-hex, so its rule 3 also produces "<mac>"):
    //   MacPairs  aa:bb:cc:dd:ee:ff, aa-bb-cc-dd-ee-ff
    //   MacDevId  Dev_AABBCCDDEEFF
    //   MacTail   &E806884B0741_C00000000 - the BTHENUM instance tail
    //             documented at DriverInstaller.TryDiscoverKeyboardMac and
    //             parsed by DriverInstaller.ParseMacFromInstance
    //   MacKey    mac=e806884b0741 - the bare 12-hex lowercase form
    //             SdpPatchReader.NormalizeMac returns, written by
    //             SDP_PATCH_FAILED, SDP_PATCH and DRIVER_SDP. Anchored on the
    //             mac= key, so "mac=none" and "mac=invalid" stay readable and
    //             no unanchored hex run is touched.
    internal static string RedactMacs(string text)
    {
        if (string.IsNullOrEmpty(text) || !MayContainMac(text))
            return text;
        text = MacPairs.Replace(text, "<mac>");
        text = MacDevId.Replace(text, "Dev_<mac>");
        text = MacTail.Replace(text, "<mac>");
        return MacKey.Replace(text, "<mac>");
    }

    // Compiled and static: Log is the write path for every line the app emits,
    // so neither the Regex nor its match plan is rebuilt per call.
    static readonly Regex MacPairs = new(
        @"\b([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b", RegexOptions.Compiled);
    static readonly Regex MacDevId = new(
        @"\bDev_[0-9A-Fa-f]{12}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex MacTail = new(
        @"(?<=&)[0-9A-Fa-f]{12}(?=_)", RegexOptions.Compiled);
    static readonly Regex MacKey = new(
        @"(?<=\bmac=)[0-9A-Fa-f]{12}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Allocation-free gate so the common address-free line pays one char scan
    // and no Replace. Every rule above needs 12 hex digits in a run that
    // crosses at most single ':'/'-' separators, so a run reaching 12 is a
    // necessary condition for any of them to match: the scan may over-trigger
    // (the base UUID's 24 hyphen-joined hex digits do) but never under-trigger.
    // Timestamps stay under the bar - "2025-09-16 14:08:31" breaks its run at
    // the space, reaching 8.
    static bool MayContainMac(string text)
    {
        var run = 0;
        foreach (var c in text)
        {
            if (Uri.IsHexDigit(c))
            {
                if (++run == 12)
                    return true;
            }
            else if (c != ':' && c != '-')
            {
                run = 0;
            }
        }
        return false;
    }
}
