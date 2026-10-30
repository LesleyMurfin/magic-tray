#Requires -Version 7
<#
.SYNOPSIS
    Corpus check for ConvertTo-MacSafeText, the redaction sink in
    scripts/mm-hid-report.ps1.

.DESCRIPTION
    ConvertTo-MacSafeText is the only control standing between a reporter's
    hardware identifiers and a public GitHub issue, and mm-hid-report.ps1 prints
    a banner telling the reporter the saved file is safe to paste. Every line
    the report contains goes through that one function, so a regression in any
    of its four rules publishes a stranger's Bluetooth address or a unit's
    iSerialNumber under a promise that it would not.

    Until this file existed the function was verified only by a harness outside
    the repository, so nothing in continuous integration protected it. This is
    that corpus, moved in.

    Shape: a plain pwsh script that counts assertions and exits non-zero, the
    same shape as check-site.ps1 and check-aeo.ps1, reporting through the same
    GitHub Actions annotations. Pester would be the other option and is not
    used here: the repository has no *.Tests.ps1 and no Pester reference
    anywhere, ps-lint.yml's only module dependency is a pinned
    PSScriptAnalyzer, and one function with string in and string out needs no
    test framework to drive it. The dependency would cost more than it buys.

    Every script under scripts/ that defines ConvertTo-MacSafeText is checked,
    not just mm-hid-report.ps1, and the copies are compared to each other as
    well - see the comment above the discovery loop for why the function is
    duplicated rather than shared.

    The function is sliced out of each script with the PowerShell parser rather
    than dot-sourced. Dot-sourcing a whole script would run mm-hid-report.ps1's
    Add-Type block, which is Windows P/Invoke and does not compile on the
    ubuntu-latest runner this check uses. Slicing by AST and not by line range
    is what stops the next edit higher up those files breaking this one.
    $script:MacSafeConstants comes across with it because it is the function's
    whitelist: pinning the function without pinning the whitelist would let the
    GUID tails every Windows PC carries be redacted away unnoticed.

    Rules, and what breaks if the rule breaks. Each is named in the Rule field
    of the corpus below, so a failure says which one moved.

    1. mac-punctuated
       The colon- and dash-separated address forms become <mac>. Break it and
       the address a BTHENUM device reports under FriendlyName or a paired-
       device listing ships verbatim.

    2. mac-dev-prefix
       Dev_<12 hex> becomes Dev_<mac>, keeping the prefix so the reader can
       still tell a Dev_ node from a bare address.

    3. mac-bare-belt
       Any unpunctuated 12-hex run becomes <mac> unless it is one of the
       whitelisted constants. This is the belt: a real address can look like
       anything, so the default is redact and the exceptions are a list, never
       a pattern that tries to recognise an address.

    4. usb-serial
       The third node of a USB instance id is the unit's iSerialNumber, which
       is not a MAC shape and so matches none of the three rules above. A
       Lightning Magic Mouse 2 enumerates over USB while it charges, so without
       this rule the report carries that serial. It runs last and consumes an
       already-redacted <mac> token only when that token is the whole node, so
       an iSerialNumber that IS the unit's address keeps the <mac> spelling and
       stays correlatable with debug.log.

    5. friendly-name
       A paired Apple peripheral advertises a name built from the account
       holder's given name, so ConvertTo-SafeFriendlyName keeps the model words
       and redacts everything else token by token, collapsing adjacent <name>
       tokens. A given name is neither a MAC shape nor a serial shape, so none
       of the four rules above can touch it. This sink lives in
       mm-bt-stack-snapshot.ps1 and diagnose-and-recover.ps1, not in
       mm-hid-report.ps1.

.PARAMETER ScriptPath
    Pin the check to one script instead of discovering every copy. Used to
    drive a mutated copy under /tmp when proving an assertion is load-bearing;
    CI passes nothing and gets the full sweep.

.EXAMPLE
    pwsh -File scripts/check-redaction.ps1
#>
[CmdletBinding()]
param(
    [string]$ScriptPath
)

# Version 2, not 3, and deliberately: diagnose-and-recover.ps1 sets exactly
# this and mm-bt-stack-snapshot.ps1 sets none, so 2 is the strictest mode any
# shipping copy actually runs under. Driving a sliced function under a stricter
# mode than production uses would report failures production cannot have - the
# empty-$out branch of ConvertTo-SafeFriendlyName reads $out[-1] on an empty
# array, which is a silent $null here and a thrown error under 3.
Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

# ------------------------------------------------------------------ corpus ---
# Every row of the table recorded in issue #183, plus the four cases the later
# review rounds of PR #168 added. Expected is compared case-sensitively: the
# redaction tokens are lower case and a case-insensitive comparison would call
# <MAC> a pass.
$corpus = @(
    # Rule 1. Both separators are listed because each is separately
    # load-bearing: dropping either from the separator class leaves the other
    # form green, so one example would hide half the rule.
    @{ Rule = 'mac-punctuated'; Name = 'colon-separated address'
       Text = 'e8:06:88:4b:07:41'; Expected = '<mac>' }
    @{ Rule = 'mac-punctuated'; Name = 'dash-separated address, upper case'
       Text = 'E8-06-88-4B-07-41'; Expected = '<mac>' }

    # Rule 2. The prefix has to survive, which is the whole difference between
    # this rule and the belt below - the belt alone would also redact the hex.
    @{ Rule = 'mac-dev-prefix'; Name = 'Dev_ node keeps its prefix'
       Text = 'Dev_e806884b0741'; Expected = 'Dev_<mac>' }

    # Rule 3, and the two whitelisted constants it must not eat.
    @{ Rule = 'mac-bare-belt'; Name = 'bare 12-hex run'
       Text = 'e806884b0741'; Expected = '<mac>' }
    # The address goes and the surrounding instance id stays: a BTHENUM node
    # whose service GUID or trailing _C00000000 were swallowed no longer says
    # which SDP service it hangs off, and that is the evidence the report is
    # being written to carry.
    @{ Rule = 'mac-bare-belt'; Name = 'BTHENUM instance id, address out and tail intact'
       Text     = 'BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&000205ac_PID&0269\8&36b55ed3&0&E806884B0741_C00000000'
       Expected = 'BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&000205ac_PID&0269\8&36b55ed3&0&<mac>_C00000000' }
    @{ Rule = 'mac-bare-belt'; Name = 'Bluetooth SIG base UUID tail is whitelisted'
       Text = '00805f9b34fb'; Expected = '00805f9b34fb' }
    @{ Rule = 'mac-bare-belt'; Name = 'HID interface class GUID tail is whitelisted'
       Text = '001111000030'; Expected = '001111000030' }
    # The end-to-end case: a real device interface path has to come through
    # untouched, which pins that no rule fires on the VID/PID words or on the
    # HIDClass-minted middle node either.
    @{ Rule = 'mac-bare-belt'; Name = 'full Bluetooth HID interface path is unchanged end to end'
       Text     = '\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&000205ac_PID&0269&COL02#8&36b55ed3&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}'
       Expected = '\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&000205ac_PID&0269&COL02#8&36b55ed3&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}' }

    # Rule 4.
    @{ Rule = 'usb-serial'; Name = 'USB instance id serial is redacted'
       Text = 'USB\VID_05AC&PID_0269\C02XZ9QW1KLM'; Expected = 'USB\VID_05AC&PID_0269\<serial>' }
    # One address, one spelling. This unit reports its Bluetooth address as the
    # USB iSerialNumber, so the same address arrives over two transports; if
    # the serial rule spelled it <serial> here and Logger.RedactMacs spelled it
    # <mac> in debug.log, a maintainer could no longer correlate the two.
    @{ Rule = 'usb-serial'; Name = 'MAC-shaped serial keeps the <mac> spelling'
       Text = 'USB\VID_05AC&PID_0269\E806884B0741'; Expected = 'USB\VID_05AC&PID_0269\<mac>' }
    @{ Rule = 'usb-serial'; Name = 'serial is redacted through an &MI_ interface node'
       Text = 'USB\VID_05AC&PID_0269&MI_01\C02XZ9QW1KLM'; Expected = 'USB\VID_05AC&PID_0269&MI_01\<serial>' }
    # An empty third node has nothing to redact, and minting a <serial> token
    # for it would claim a serial was there.
    @{ Rule = 'usb-serial'; Name = 'empty tail is left alone'
       Text = 'USB\VID_05AC&PID_0269\'; Expected = 'USB\VID_05AC&PID_0269\' }
    # The HID child's third node is minted by HIDClass and is never the unit's
    # serial, so redacting it would cost the one field that tells two
    # collections of the same mouse apart.
    @{ Rule = 'usb-serial'; Name = 'HID child instance id is not a serial'
       Text = 'HID\VID_05AC&PID_0269&COL02\9&2a1cdc4a&0&0000'; Expected = 'HID\VID_05AC&PID_0269&COL02\9&2a1cdc4a&0&0000' }
    # Thirteen characters: the belt ran first and minted <mac> from the first
    # twelve, leaving the thirteenth exposed. A node longer than an address is
    # not an address, so the serial rule must swallow the belt's <mac> along
    # with the residue - publishing 'X' beside a <mac> leaks a fragment of the
    # real serial and claims an address was there.
    @{ Rule = 'usb-serial'; Name = 'thirteen-character serial loses both the residue and the belt token'
       Text = 'USB\VID_05AC&PID_0269\A4B1C2D3E4F5X'; Expected = 'USB\VID_05AC&PID_0269\<serial>' }
    # The tail stops at the literal '#{' pair that opens a trailing interface
    # class identifier, because that GUID is what the whitelist exists to
    # preserve.
    @{ Rule = 'usb-serial'; Name = 'trailing interface class identifier survives the serial rule'
       Text     = 'USB\VID_05AC&PID_0269\FVFXQ0ABCDE1#{4d1e55b2-f16f-11cf-88cb-001111000030}'
       Expected = 'USB\VID_05AC&PID_0269\<serial>#{4d1e55b2-f16f-11cf-88cb-001111000030}' }
    # The other half of that stop, and why it anchors on the pair rather than
    # excluding the two characters individually: excluding '#' alone halts at
    # the first '#' inside a serial and publishes the remainder beside a token
    # claiming it was redacted, and excluding '{' alone refuses to redact a
    # serial that opens with a brace. Both shapes reach the function through
    # the InstanceId of a device.
    @{ Rule = 'usb-serial'; Name = 'serial containing a # is redacted whole'
       Text = 'USB\VID_05AC&PID_0269\ABC#DEF'; Expected = 'USB\VID_05AC&PID_0269\<serial>' }
    @{ Rule = 'usb-serial'; Name = 'serial opening with a brace is still redacted'
       Text = 'USB\VID_05AC&PID_0269\{brace}'; Expected = 'USB\VID_05AC&PID_0269\<serial>' }
)

# ----------------------------------------------------- friendly-name corpus ---
# The fifth sink. Measured outputs, supplied by the author of the rule and
# re-measured here against both copies; nothing below is an intention.
$friendlyCorpus = @(
    # The shape the rule exists for: the owner's name goes, the model words
    # stay, because FriendlyName is how a reader tells a Magic Mouse from a
    # Magic Trackpad on a PC carrying four Apple nodes.
    @{ Rule = 'friendly-name'; Name = 'possessive given name is redacted, model words survive'
       Text = "Lesley's Magic Mouse"; Expected = '<name> Magic Mouse' }
    @{ Rule = 'friendly-name'; Name = 'full name is redacted, model and generation survive'
       Text = "Lesley Murfin's Magic Trackpad 2"; Expected = '<name> Magic Trackpad 2' }
    # Three redacted tokens collapse to one. Without the collapse the output
    # publishes how many words the owner's name had, which is itself a fact
    # about the owner.
    @{ Rule = 'friendly-name'; Name = 'adjacent redactions collapse, so the name length does not leak'
       Text = "Jane Q Doe's Mouse"; Expected = '<name> Mouse' }
    # The stated cost, pinned as a cost and not as a success: a renamed device
    # loses its model word along with the name. Anyone who makes this case
    # green by widening the word list has widened the leak.
    @{ Rule = 'friendly-name'; Name = 'a renamed device loses its model word too - the accepted cost'
       Text = 'Buero-Maus von Lesley'; Expected = '<name>' }

    # Stock names carry nothing to redact and must come through whole. A child
    # still called "Bluetooth Low Energy HID Device" IS the finding - Windows
    # never bound the vendor name - so redacting it would destroy the evidence.
    @{ Rule = 'friendly-name'; Name = 'stock model name is unchanged'
       Text = 'Magic Mouse'; Expected = 'Magic Mouse' }
    @{ Rule = 'friendly-name'; Name = 'one- and two-digit generation numbers survive'
       Text = 'Magic Mouse 2'; Expected = 'Magic Mouse 2' }
    @{ Rule = 'friendly-name'; Name = 'multi-word model name is unchanged'
       Text = 'Magic Keyboard with Numeric Keypad'; Expected = 'Magic Keyboard with Numeric Keypad' }
    @{ Rule = 'friendly-name'; Name = 'Apple Wireless Keyboard is unchanged'
       Text = 'Apple Wireless Keyboard'; Expected = 'Apple Wireless Keyboard' }
    @{ Rule = 'friendly-name'; Name = 'unbound BLE child name is unchanged - that name is the finding'
       Text = 'Bluetooth Low Energy HID Device'; Expected = 'Bluetooth Low Energy HID Device' }
    @{ Rule = 'friendly-name'; Name = 'hyphenated stock name is unchanged'
       Text = 'HID-compliant mouse'; Expected = 'HID-compliant mouse' }

    # A bare given name has no model word to keep.
    @{ Rule = 'friendly-name'; Name = 'a lone given name is redacted whole'
       Text = 'Lesley'; Expected = '<name>' }
    # The MAC whitelist carve-out does NOT reach this sink. As a whole
    # FriendlyName, a SIG base UUID tail is not a service GUID tail, it is an
    # unknown value, and the default for an unknown value is redact.
    @{ Rule = 'friendly-name'; Name = 'a whitelisted GUID tail is still redacted as a whole name'
       Text = '00805f9b34fb'; Expected = '<name>' }
    @{ Rule = 'friendly-name'; Name = 'a bare address as a name is redacted'
       Text = 'e806884b0741'; Expected = '<name>' }
    @{ Rule = 'friendly-name'; Name = 'a Dev_ node as a name is redacted'
       Text = 'Dev_E806884B0741'; Expected = '<name>' }

    # Pure punctuation carries nothing to redact, and a parenthesised
    # generation number is still a generation number.
    @{ Rule = 'friendly-name'; Name = 'a lone hyphen is left alone'
       Text = '-'; Expected = '-' }
    @{ Rule = 'friendly-name'; Name = 'a parenthesised generation number is left alone'
       Text = '(2)'; Expected = '(2)' }
    # -split '\s+' collapses runs, so a double space cannot smuggle a token
    # past the tokeniser.
    @{ Rule = 'friendly-name'; Name = 'runs of whitespace collapse, so no token hides between them'
       Text = '  Magic   Mouse  '; Expected = 'Magic Mouse' }
    @{ Rule = 'friendly-name'; Name = 'an empty name is returned as-is'
       Text = ''; Expected = '' }
    @{ Rule = 'friendly-name'; Name = 'an all-whitespace name is returned as-is'
       Text = '   '; Expected = '   ' }
)

# --------------------------------------------------------------------- sinks ---
# The two redaction sinks this repository ships, each with the script-scope
# state its function reads. Membership of $script:FriendlyNameWords is
# deliberately NOT asserted: that list is expected to grow when Apple ships a
# model word it lacks, and pinning its contents would turn a routine addition
# into a test failure for no safety gain. The behaviour cases above already go
# red if a word is REMOVED, which is the direction that leaks.
$sinks = @(
    @{ Function = 'ConvertTo-MacSafeText';     State = '$script:MacSafeConstants';  Corpus = $corpus }
    @{ Function = 'ConvertTo-SafeFriendlyName'; State = '$script:FriendlyNameWords'; Corpus = $friendlyCorpus }
)

# -------------------------------------------------------------------- main ---

# Each sink is duplicated across several scripts rather than shared, and that
# is deliberate: stage-publish.ps1 stages the diagnostic scripts FLAT beside the
# executable and release.yml uploads each as a standalone asset, so a reader who
# downloads a single .ps1 has no sibling helper to dot-source. Duplicated code
# drifts silently, so each corpus runs against every copy and the copies are
# then compared to each other. Discovery is by content - a script that defines
# one of these functions is in scope the day it is written, which is the only
# arrangement that does not need somebody to remember this file.
$scriptsDir = Split-Path -Parent $PSScriptRoot | Join-Path -ChildPath 'scripts'
$pinned = $PSBoundParameters.ContainsKey('ScriptPath')

if ($pinned) {
    if (-not (Test-Path -LiteralPath $ScriptPath -PathType Leaf)) {
        # Never a skip. These corpora exist because the functions are security
        # controls; a missing control is the loudest possible failure, not a
        # reason to report green.
        Write-Output "::error::redaction corpus: '$ScriptPath' not found, so no redaction sink was exercised"
        exit 1
    }
    $searchRoot = @((Get-Item -LiteralPath $ScriptPath).FullName)
} else {
    if (-not (Test-Path -LiteralPath $scriptsDir -PathType Container)) {
        Write-Output "::error::redaction corpus: '$scriptsDir' not found, so no redaction sink was exercised"
        exit 1
    }
    $searchRoot = @(
        Get-ChildItem -LiteralPath $scriptsDir -Filter '*.ps1' -File |
            Sort-Object -Property Name |
            ForEach-Object { $_.FullName }
    )
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$failures = [System.Collections.Generic.List[string]]::new()
$assertions = 0
$summary = [System.Collections.Generic.List[string]]::new()

foreach ($sink in $sinks) {
    $pattern = '(?m)^function\s+{0}\b' -f [regex]::Escape($sink.Function)
    $candidates = @(
        $searchRoot | Where-Object { (Get-Content -LiteralPath $_ -Raw) -match $pattern }
    )

    if ($candidates.Count -eq 0) {
        # Pinned to one script, a sink that script does not carry is simply not
        # under test - mm-hid-report.ps1 has no FriendlyName rule. On the full
        # sweep a sink with no home has gone missing, and that is a failure.
        if ($pinned) { continue }
        Write-Output ("::error::redaction corpus: no script under scripts/ defines {0} - if it moved, point this check at its new home rather than deleting the check" -f $sink.Function)
        exit 1
    }

    $bodies = [System.Collections.Generic.List[pscustomobject]]::new()

    foreach ($candidate in $candidates) {
        $rel = [IO.Path]::GetRelativePath($repoRoot, $candidate).Replace('\', '/')

        $tokens = $null
        $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($candidate, [ref]$tokens, [ref]$parseErrors)
        if ($parseErrors -and $parseErrors.Count -gt 0) {
            Write-Output ("::error file={0},line=1::redaction corpus: {0} does not parse, so {1} could not be extracted" -f $rel, $sink.Function)
            exit 1
        }

        $functionAst = $ast.Find({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq $sink.Function
            }.GetNewClosure(), $true)
        $stateAst = $ast.Find({
                param($node)
                $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                $node.Left.Extent.Text -eq $sink.State
            }.GetNewClosure(), $true)

        if ($null -eq $functionAst -or $null -eq $stateAst) {
            $missing = if ($null -eq $functionAst) { $sink.Function } else { $sink.State }
            Write-Output ("::error file={0},line=1::redaction corpus: {1} is not in {0} - if it moved, point this check at its new home rather than deleting the check" -f $rel, $missing)
            exit 1
        }

        $functionLine = $functionAst.Extent.StartLineNumber
        $bodies.Add([pscustomobject]@{
                Path = $rel
                Line = $functionLine
                Text = $functionAst.Extent.Text
            })

        # Dot-sourced rather than run through Invoke-Expression so
        # PSScriptAnalyzer's PSAvoidUsingInvokeExpression stays enforced across
        # the tree; dot-sourcing a created scriptblock lands the assignment and
        # the function in this script's own scope, which is the scope the
        # $script: state then resolves in. Each copy overwrites the last, so
        # every copy is driven by its OWN state - mm-bt-stack-snapshot.ps1
        # carries a third whitelisted constant the others do not, because it is
        # the only script that writes a ClassGUID.
        . ([scriptblock]::Create($stateAst.Extent.Text))
        . ([scriptblock]::Create($functionAst.Extent.Text))

        foreach ($case in $sink.Corpus) {
            $assertions++
            $actual = & $sink.Function $case.Text
            if ([string]$actual -ceq [string]$case.Expected) {
                Write-Output ("ok   {0} [{1}] {2}" -f $rel, $case.Rule, $case.Name)
            } else {
                $failures.Add("$rel :: $($case.Name)")
                $message = "redaction corpus [{0}] {1}: expected [{2}] but got [{3}] for input [{4}]" -f `
                    $case.Rule, $case.Name, $case.Expected, $actual, $case.Text
                $message = $message -replace '%', '%25' -replace "`r", '%0D' -replace "`n", '%0A'
                Write-Output ("::error file={0},line={1},title={2}::{3}" -f
                    $rel, $functionLine, ('{0} regression' -f $sink.Function), $message)
            }
        }
    }

    # The corpus above would pass once per copy on copies that had quietly
    # diverged in a case nobody thought to write down. The copies are declared
    # verbatim, so assert exactly that: one text, byte for byte. The $script:
    # state is deliberately NOT compared - the MAC whitelists differ per script
    # by design, and the word list is expected to grow.
    $reference = $bodies[0]
    foreach ($body in $bodies | Select-Object -Skip 1) {
        $assertions++
        if ($body.Text -ceq $reference.Text) {
            Write-Output ("ok   {0} [copy-parity] {1} is verbatim against {2}" -f $body.Path, $sink.Function, $reference.Path)
        } else {
            $failures.Add("$($body.Path) :: copy-parity $($sink.Function)")
            Write-Output ("::error file={0},line={1},title={2}::{3}" -f
                $body.Path, $body.Line, ('{0} copies have diverged' -f $sink.Function),
                ("redaction corpus [copy-parity] {0} no longer carries the same {1} as {2} - the copies are duplicated on purpose because each script ships as a standalone release asset, so a change to one is a change to all" -f $body.Path, $sink.Function, $reference.Path))
        }
    }

    $summary.Add(("{0} x{1}" -f $sink.Function, $bodies.Count))
}

if ($assertions -eq 0) {
    Write-Output "::error::redaction corpus: '$ScriptPath' defines no known redaction sink, so nothing was exercised"
    exit 1
}

Write-Output ("Redaction corpus: {0} assertion(s) over {1}, {2} failure(s)." -f
    $assertions, ($summary -join ', '), $failures.Count)

if ($failures.Count -gt 0) { exit 1 }
exit 0
