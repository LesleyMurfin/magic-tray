// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace MagicMouseTray.Tests;

// The honesty rules the C# readers follow - never report "absent" for a value
// nobody could read, never claim an artefact that was not written - also bind
// the two PowerShell scripts the tray's Diagnostics menu launches. Those have no
// C# seam: the report write lives in scripts/diagnose-and-recover.ps1 and the
// stack-attachment tri-state lives in scripts/capture-state.ps1, so the only
// executable specification available here is a structural one over the script
// source: which branch a statement sits in, what its catch does, what a field is
// seeded with. Nothing below asserts wording or prose - every assertion names a
// statement by what it DOES (Set-Content of the report path, the
// DEVPKEY_Device_Stack read) and then pins where it sits.
public class ScriptHonestyTests
{
    const string DiagnoseAndRecover = "scripts/diagnose-and-recover.ps1";
    const string CaptureState = "scripts/capture-state.ps1";

    // $ErrorActionPreference = 'Continue' means a Set-Content that cannot write
    // (read-only OutDir, full disk, locked file) is NON-terminating: the script
    // runs straight on into "Saved: <path>" and the user hands support a path to
    // a file that does not exist. The write must be error-trapped and the path
    // may only be announced where the write is proven.
    [Fact]
    public void DiagnoseAndRecover_AnnouncesTheReportPathOnlyInsideAGuardedBranch()
    {
        var script = PsScript.Load(DiagnoseAndRecover);
        var write = script.SingleLineWhere(l =>
            l.Contains("Set-Content", StringComparison.Ordinal) &&
            l.Contains("$txt", StringComparison.Ordinal));

        Assert.Contains("-ErrorAction Stop", script.ContinuedStatement(write), StringComparison.Ordinal);

        var tryOpener = script.EnclosingTryOpener(write);
        Assert.True(
            tryOpener >= 0,
            $"{DiagnoseAndRecover}:{write + 1} writes the report outside any try block, "
                + "so a failed write cannot be distinguished from a successful one.");

        var mentions = script.LinesWhere(l =>
            PsScript.IsVisibleOutput(l) && l.Contains("$txt", StringComparison.Ordinal));
        Assert.NotEmpty(mentions);
        var tryEnd = script.BlockEndLine(tryOpener);
        foreach (var line in mentions)
        {
            Assert.True(
                script.IsGuardedBy(line, tryOpener),
                $"{DiagnoseAndRecover}:{line + 1} tells the user about the report path outside the try/catch "
                    + "that guards the write, i.e. whether or not the write happened.");
        }

        // ...and the success announcement sits in the try body itself, after
        // the write it reports, where the write is proven to have returned.
        Assert.Contains(mentions, line => line > write && line < tryEnd);
    }

    // The failure path must reach the user on the same channel as everything
    // else the script prints. A write that failed into silence is the same lie
    // in a quieter voice.
    [Fact]
    public void DiagnoseAndRecover_ReportWriteFailure_ReachesTheUserNotOnlyTheVerboseStream()
    {
        var script = PsScript.Load(DiagnoseAndRecover);
        var write = script.SingleLineWhere(l =>
            l.Contains("Set-Content", StringComparison.Ordinal) &&
            l.Contains("$txt", StringComparison.Ordinal));
        var tryOpener = script.EnclosingTryOpener(write);
        Assert.True(tryOpener >= 0, $"{DiagnoseAndRecover}: the report write is not inside a try block.");

        var handler = script.CatchTextFor(tryOpener);
        Assert.True(
            PsScript.IsVisibleOutput(handler),
            $"{DiagnoseAndRecover}: the catch around the report write produces no user-visible output: {Trim(handler)}");
    }

    // Same rule for the %APPDATA% copy support actually asks for: today it is a
    // catch { Write-Verbose }, invisible without -Verbose, so the only copy a
    // helper is told to fetch can fail without a word.
    [Fact]
    public void DiagnoseAndRecover_AppDataCopyFailure_ReachesTheUserNotOnlyTheVerboseStream()
    {
        var script = PsScript.Load(DiagnoseAndRecover);
        var copy = script.SingleLineWhere(l => l.Contains("Copy-Item", StringComparison.Ordinal));
        var tryOpener = script.EnclosingTryOpener(copy);
        Assert.True(
            tryOpener >= 0,
            $"{DiagnoseAndRecover}:{copy + 1} copies the report outside any try block.");

        var handler = script.CatchTextFor(tryOpener);
        Assert.True(
            PsScript.IsVisibleOutput(handler),
            $"{DiagnoseAndRecover}: the %APPDATA% copy swallows its failure into the verbose stream: {Trim(handler)}");
    }

    // capture-state.ps1 exists to answer one question across a reboot: is the
    // bound filter ATTACHED to the live stack? Seeding the answer $false makes
    // "could not read" indistinguishable from "measured absent" - the exact
    // conflation diagnose-and-recover.ps1 avoids with its tri-state, and the
    // same conflation this script's own comment says once hid the fault.
    [Fact]
    public void CaptureState_SeedsStackAttachmentAsUnknown_NotAbsent()
    {
        var script = PsScript.Load(CaptureState);
        var seed = script.SingleLineWhere(l => Regex.IsMatch(l, @"^\s*filterInStack\s*="));

        // Code, not Raw: the blanked view drops the trailing comment, so this
        // reads the seeded value and only the seeded value.
        Assert.Contains("$null", script.Code[seed], StringComparison.Ordinal);
        Assert.DoesNotContain("$false", script.Code[seed], StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureState_UnreadableDeviceStack_IsNotRecordedAsAbsent()
    {
        var script = PsScript.Load(CaptureState);
        // The call is split over a backtick continuation; -KeyName carries the
        // property name, so that is the line that identifies the read.
        var read = script.SingleLineWhere(l =>
            l.Contains("DEVPKEY_Device_Stack", StringComparison.Ordinal) &&
            l.Contains("-KeyName", StringComparison.Ordinal));
        var statement = script.ContinuedStatement(read);

        // SilentlyContinue returns nothing instead of throwing, so the read
        // failure never reaches the catch - it just falls past the `if` and
        // leaves whatever the field was seeded with.
        Assert.Contains("-ErrorAction Stop", statement, StringComparison.Ordinal);
        Assert.DoesNotContain("SilentlyContinue", statement, StringComparison.Ordinal);

        var tryOpener = script.EnclosingTryOpener(read);
        Assert.True(tryOpener >= 0, $"{CaptureState}:{read + 1} reads the device stack outside any try block.");

        foreach (var line in script.LinesWhere(l => Regex.IsMatch(l, @"filterInStack\s*=\s*\$false")))
        {
            Assert.Fail(
                $"{CaptureState}:{line + 1} assigns the literal absent value to filterInStack; "
                    + "only a completed Test-StackHasFilter measurement may say absent.");
        }
    }

    // The tri-state has to survive to the screen: $($state.filterInStack) prints
    // "False" for an unread value just as it does for a measured absence.
    [Fact]
    public void CaptureState_Summary_RendersUnknownStackStateDistinctlyFromAbsent()
    {
        var script = PsScript.Load(CaptureState);
        var summary = script.SingleLineWhere(l =>
            l.Contains("Write-Host", StringComparison.Ordinal) &&
            l.Contains("state.filterInStack", StringComparison.Ordinal));

        Assert.True(
            Regex.IsMatch(script.Uncommented[summary], @"\$null|Format-\w+"),
            $"{CaptureState}:{summary + 1} interpolates filterInStack raw, so an unread stack prints exactly "
                + "what a measured-absent filter prints.");
    }

    // ...and to the compare, which is the whole point of the pre/post-reboot
    // capture. Compare-Field drops any pair containing $null into its equality
    // branch, so "unknown" vs "absent" reads as agreement.
    [Fact]
    public void CaptureState_Compare_DoesNotCallAnUnreadValueAgreement()
    {
        var script = PsScript.Load(CaptureState);
        var fn = script.SingleLineWhere(l => Regex.IsMatch(l, @"^\s*function\s+Compare-Field\b"));
        var end = script.BlockEndLine(fn);
        Assert.True(end > fn, $"{CaptureState}: Compare-Field body not found.");

        var hasUnknownBranch = Enumerable.Range(fn, end - fn + 1).Any(line =>
            Regex.IsMatch(script.Code[line], @"\b(if|elseif)\s*\([^)]*\$null") &&
            PsScript.IsVisibleOutput(script.BlockText(line)));

        Assert.True(
            hasUnknownBranch,
            $"{CaptureState}: Compare-Field has no branch of its own for a value that was never read, "
                + "so unknown-vs-measured reports as 'same'.");
    }

    // F25's documentation half: the design doc is the menu's contract, and it
    // currently lists a script that is in no commit of this repo.
    [Fact]
    public void DiagnosticsMenuDesign_NamesNoScriptTheRepoDoesNotShip()
    {
        var doc = Path.Combine(RepoTree.Root, "design", "DESIGN-diagnostics-menu.md");
        Assert.True(File.Exists(doc), $"missing {doc}");

        foreach (Match m in Regex.Matches(File.ReadAllText(doc), @"[A-Za-z0-9._-]+\.ps1"))
        {
            Assert.True(
                RepoTree.ScriptExists(m.Value),
                $"design/DESIGN-diagnostics-menu.md documents {m.Value}, which this repo does not contain.");
        }
    }

    static string Trim(string text) =>
        Regex.Replace(text, @"\s+", " ").Trim();
}

// Locates the checkout the tests were built from. Tests run from
// MagicMouseTray.Tests/bin/<cfg>/<tfm>/, four levels under the repo root.
internal static class RepoTree
{
    static readonly Lazy<string> Located = new(Locate);

    public static string Root => Located.Value;

    public static bool ScriptExists(string fileName) =>
        File.Exists(Path.Combine(Root, "scripts", fileName)) ||
        File.Exists(Path.Combine(Root, fileName));

    static string Locate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MagicMouseTray", "MagicMouseTray.csproj")) &&
                Directory.Exists(Path.Combine(dir.FullName, "scripts")))
                return dir.FullName;
        }

        throw new InvalidOperationException(
            $"repo root (MagicMouseTray/MagicMouseTray.csproj + scripts/) not found above {AppContext.BaseDirectory}");
    }
}

// Minimal structural view of a PowerShell file in two layers: Code (string
// literals and comments blanked, so brace nesting can be counted) and
// Uncommented (comments blanked, literals kept, which is what every locator
// and every statement-text assertion reads, so no comment can add a phantom
// match or break one). That is all these tests need to say "this statement
// sits inside that branch". Not a parser, and never used to assert the text of
// a message.
internal sealed class PsScript
{
    static readonly Regex VisibleOutput = new(@"\bWrite-(Log|Host|Warning|Error)\b", RegexOptions.Compiled);

    readonly string _name;
    readonly string[] _code;
    readonly string[] _uncommented;
    readonly int[] _opener;

    PsScript(string name, string[] code, string[] uncommented)
    {
        _name = name;
        _code = code;
        _uncommented = uncommented;
        _opener = new int[code.Length];

        var stack = new Stack<int>();
        for (var i = 0; i < code.Length; i++)
        {
            _opener[i] = stack.Count > 0 ? stack.Peek() : -1;
            foreach (var c in code[i])
            {
                if (c == '{')
                    stack.Push(i);
                else if (c == '}' && stack.Count > 0)
                    stack.Pop();
            }
        }
    }

    public IReadOnlyList<string> Code => _code;

    // Comments blanked, string literals kept: the view for assertions that must
    // read what a statement actually emits, where Code blanks the interpolation.
    public IReadOnlyList<string> Uncommented => _uncommented;

    public static PsScript Load(string repoRelativePath)
    {
        var path = Path.Combine(RepoTree.Root, repoRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"missing {repoRelativePath} (looked at {path})");
        var raw = File.ReadAllLines(path);
        return new PsScript(
            repoRelativePath, Blank(raw, blankLiterals: true), Blank(raw, blankLiterals: false));
    }

    public static bool IsVisibleOutput(string text) => VisibleOutput.IsMatch(text);

    // Locating a statement never sees comment text: a comment can neither add a
    // phantom match nor break SingleLineWhere's exactly-one contract.
    public IReadOnlyList<int> LinesWhere(Func<string, bool> predicate) =>
        Enumerable.Range(0, _uncommented.Length).Where(i => predicate(_uncommented[i])).ToList();

    public int SingleLineWhere(Func<string, bool> predicate)
    {
        var hits = LinesWhere(predicate);
        Assert.True(hits.Count == 1, $"{_name}: expected exactly one matching statement, found {hits.Count}");
        return hits[0];
    }

    // Joins a backtick-continued statement so argument checks see the whole call.
    public string ContinuedStatement(int line)
    {
        var first = line;
        while (first > 0 && _code[first - 1].TrimEnd().EndsWith("`", StringComparison.Ordinal))
            first--;
        var last = line;
        while (last < _code.Length - 1 && _code[last].TrimEnd().EndsWith("`", StringComparison.Ordinal))
            last++;
        return string.Join(" ", _uncommented.Skip(first).Take(last - first + 1).Select(l => l.Trim()));
    }

    // Innermost enclosing block whose opening line is a try.
    public int EnclosingTryOpener(int line)
    {
        for (var at = _opener[line]; at >= 0; at = _opener[at])
        {
            if (Regex.IsMatch(_code[at], @"\btry\b"))
                return at;
        }

        return -1;
    }

    public int BlockEndLine(int openerLine) => BlockEndLine(openerLine, 0);

    public string BlockText(int openerLine)
    {
        var end = BlockEndLine(openerLine);
        if (end < openerLine)
            return _uncommented[openerLine];
        return string.Join("\n", _uncommented.Skip(openerLine).Take(end - openerLine + 1));
    }

    // Opening line of the catch clause attached to a try block, or -1.
    public int CatchOpenerFor(int tryOpenerLine)
    {
        var end = BlockEndLine(tryOpenerLine);
        if (end < 0)
            return -1;

        for (var line = end; line < _code.Length && line <= end + 2; line++)
        {
            if (Regex.IsMatch(_code[line], @"\bcatch\b"))
                return line;
        }

        return -1;
    }

    // Text of the catch clause (or clauses) attached to a try block.
    public string CatchTextFor(int tryOpenerLine)
    {
        var line = CatchOpenerFor(tryOpenerLine);
        if (line < 0)
            return "";

        var at = Regex.Match(_code[line], @"\bcatch\b");
        var bodyEnd = BlockEndLine(line, at.Index);
        if (bodyEnd < line)
            return _uncommented[line];
        return string.Join("\n", _uncommented.Skip(line).Take(bodyEnd - line + 1));
    }

    // True when `line` sits inside the try block opened at `tryOpenerLine`, or
    // inside the catch clause attached to it: the two regions where the
    // outcome of the guarded statement is known. Any other nesting - a
    // neighbouring if, an unrelated loop - is not guarded by this try.
    public bool IsGuardedBy(int line, int tryOpenerLine)
    {
        var catchOpener = CatchOpenerFor(tryOpenerLine);
        for (var at = _opener[line]; at >= 0; at = _opener[at])
        {
            if (at == tryOpenerLine || at == catchOpener)
                return true;
        }

        return false;
    }

    int BlockEndLine(int line, int startColumn)
    {
        var depth = 0;
        var opened = false;
        for (var i = line; i < _code.Length; i++)
        {
            var from = i == line ? startColumn : 0;
            for (var j = from; j < _code[i].Length; j++)
            {
                var c = _code[i][j];
                if (c == '{')
                {
                    depth++;
                    opened = true;
                }
                else if (c == '}' && opened)
                {
                    depth--;
                    if (depth == 0)
                        return i;
                }
            }
        }

        return -1;
    }

    // Replaces comment content - and, when blankLiterals, string-literal
    // content - with spaces, keeping line and column structure, so brace
    // counting sees code braces only. Backticks are kept in code context
    // because a trailing backtick is a line continuation.
    static string[] Blank(string[] raw, bool blankLiterals)
    {
        const int Code = 0, Single = 1, Double = 2, Block = 3;
        var mode = Code;
        var result = new string[raw.Length];

        for (var i = 0; i < raw.Length; i++)
        {
            var line = raw[i];
            var sb = new StringBuilder(line.Length);
            for (var j = 0; j < line.Length; j++)
            {
                var c = line[j];
                var next = j + 1 < line.Length ? line[j + 1] : '\0';
                switch (mode)
                {
                    case Code:
                        if (c == '<' && next == '#')
                        {
                            mode = Block;
                            sb.Append("  ");
                            j++;
                        }
                        else if (c == '#')
                        {
                            sb.Append(' ', line.Length - j);
                            j = line.Length;
                        }
                        else if (c == '\'')
                        {
                            mode = Single;
                            sb.Append(blankLiterals ? ' ' : c);
                        }
                        else if (c == '"')
                        {
                            mode = Double;
                            sb.Append(blankLiterals ? ' ' : c);
                        }
                        else if (c == '`')
                        {
                            sb.Append('`');
                            if (next != '\0')
                            {
                                sb.Append(' ');
                                j++;
                            }
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;

                    case Single:
                        if (c == '\'')
                            mode = Code;
                        sb.Append(blankLiterals ? ' ' : c);
                        break;

                    case Double:
                        if (c == '`')
                        {
                            sb.Append(blankLiterals ? ' ' : c);
                            if (next != '\0')
                            {
                                sb.Append(blankLiterals ? ' ' : next);
                                j++;
                            }
                        }
                        else
                        {
                            if (c == '"')
                                mode = Code;
                            sb.Append(blankLiterals ? ' ' : c);
                        }

                        break;

                    case Block:
                        if (c == '#' && next == '>')
                        {
                            mode = Code;
                            sb.Append("  ");
                            j++;
                        }
                        else
                        {
                            sb.Append(' ');
                        }

                        break;
                }
            }

            result[i] = sb.ToString();
        }

        return result;
    }
}
