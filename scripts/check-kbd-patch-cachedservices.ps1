#Requires -Version 7
# Registry-stubbed harness for scripts/kbd-patch-cachedservices.ps1.
#
#   pwsh -NoProfile -File scripts/check-kbd-patch-cachedservices.ps1
#
# Exits non-zero if any assertion fails. Dev-only: it is deliberately absent
# from package-release.ps1's allowlist, so it never reaches a release ZIP.
#
# The script under test rewrites HKLM and opens with `#Requires
# -RunAsAdministrator`, so no CI runner and no dev box can run it as it stands.
# This harness runs an unprivileged copy of it (line 1 stripped, BOM kept), one
# child pwsh per case, with Test-Path, Get-ItemProperty and Set-ItemProperty
# aliased onto stubs over an in-memory hive. Each case reads back the child's
# own exit code, the registry the run would have left behind, the run log and
# the console copy.
#
# FIXTURE PROVENANCE - the known limit of this evidence. There is no
# factory-stock SDP record anywhere in this repo. The only measured bytes are
# the 458-byte PATCHED record, read at runtime out of
# MagicMouseTray.Tests/SdpPatchReaderTests.cs (PatchedHex is the single source;
# nothing here retypes it). The "stock" fixture is derived BACKWARDS from it,
# exactly as SdpPatchReaderTests.Stock() does: delete the 4 inserted bytes and
# give the 4 SDP length fields their 4 bytes back. So "a stock record is
# patched to the measured bytes" proves the writer agrees with the measurement
# and with the C# reader; it does not prove either agrees with a keyboard
# nobody has dumped.
#
# Beware when editing: in PowerShell `[byte] -shl 8` wraps inside 8 bits. The
# 16-bit top record length is computed here as ([int]$b) * 256.

[CmdletBinding()]
param(
    # The repo to test. Both inputs are read from it: the script under test and
    # the measured record in the xunit fixture.
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),

    # Scratch root for the stripped script copy, the per-case hives, logs and
    # backups. Created if missing and removed again unless -KeepWorkDir.
    [string]$WorkDir,

    [switch]$KeepWorkDir,

    # Internal. Runs ONE case in a child pwsh; see Invoke-Case.
    [string]$CaseFile
)

$ErrorActionPreference = 'Stop'

# ===========================================================================
# Child mode: one case, one pwsh, so $LASTEXITCODE is the script's own.
# ===========================================================================
if ($CaseFile) {
    $case = Import-Clixml -LiteralPath $CaseFile

    # Hive shape: @{ 'CachedServices' = @{ '00010000' = <byte[]> } }. A subkey
    # whose value is a plain string is one that cannot be read: the
    # Get-ItemProperty stub throws that string instead of returning values.
    $hive = @{}
    foreach ($subkey in $case.Hive.Keys) {
        $values = $case.Hive[$subkey]
        if ($values -is [string]) { $hive[$subkey] = $values; continue }
        $copy = @{}
        foreach ($name in $values.Keys) { $copy[$name] = [byte[]]$values[$name] }
        $hive[$subkey] = $copy
    }
    $setCall = [System.Collections.Generic.List[string]]::new()
    $failValue = $case.FailValue

    # The stubs are closures, not plain functions, because the caller is
    # another script file: $script: inside a function resolves against the
    # script scope of whoever is running, which would be the script under test
    # and not this one. A closure carries $hive and $setCall with it, and both
    # are mutated in place, so what the run did is readable here afterwards.
    Set-Item -Path function:Test-StubPath -Value ({
        param([Parameter(Position = 0)][string]$Path)
        if ($Path -like 'HKLM:*') { return $hive.ContainsKey(($Path -split '\\')[-1]) }
        return (Microsoft.PowerShell.Management\Test-Path -LiteralPath $Path)
    }.GetNewClosure())

    Set-Item -Path function:Get-StubItemProperty -Value ({
        param([Parameter(Position = 0)][string]$Path)
        $leaf = ($Path -split '\\')[-1]
        $values = $hive[$leaf]
        if ($values -is [string]) { throw $values }
        $ordered = [ordered]@{}
        foreach ($name in ($values.Keys | Sort-Object)) { $ordered[$name] = $values[$name] }
        $item = [pscustomobject]$ordered
        Add-Member -InputObject $item -NotePropertyName 'PSPath' -NotePropertyValue $Path -Force
        Add-Member -InputObject $item -NotePropertyName 'PSChildName' -NotePropertyValue $leaf -Force
        return $item
    }.GetNewClosure())

    Set-Item -Path function:Set-StubItemProperty -Value ({
        param(
            [Parameter(Position = 0)][string]$Path,
            [string]$Name,
            $Value,
            [string]$Type
        )
        $setCall.Add($Name)
        if ($Type -ne 'Binary') { throw "the keyboard's SDP blob must be written as REG_BINARY, not $Type" }
        if ($failValue -and $Name -eq $failValue) { throw "injected registry write failure for value '$Name'" }
        $hive[($Path -split '\\')[-1]][$Name] = [byte[]]$Value
    }.GetNewClosure())

    # Aliases, not functions named after the cmdlets: command lookup prefers an
    # alias to a cmdlet, the script under test is invoked with & into a child
    # scope and so inherits these, and nothing in this repo has to define a
    # function that shadows a built-in cmdlet by name.
    Set-Alias -Name Test-Path -Value Test-StubPath -Scope Script
    Set-Alias -Name Get-ItemProperty -Value Get-StubItemProperty -Scope Script
    Set-Alias -Name Set-ItemProperty -Value Set-StubItemProperty -Scope Script

    # Backup-uniqueness case: both children must stamp the same second.
    if ($null -ne $case.StartAt) {
        while ((Get-Date) -lt $case.StartAt) { Start-Sleep -Milliseconds 5 }
    }

    # The fallback directory for the backups when the log directory cannot be
    # prepared is %APPDATA%\MagicMouseTray, so a case that exercises it has to
    # own %APPDATA%.
    if ($case.AppData) { $env:APPDATA = $case.AppData }

    $invocation = @{ Mac = $case.Mac; LogPath = $case.LogPath }
    if ($case.DryRun) { $invocation['DryRun'] = $true }

    $code = 0
    try {
        & $case.ScriptPath @invocation
        $code = $LASTEXITCODE
        if ($null -eq $code) { $code = 0 }
    } catch {
        # pwsh -File exits 1 on an uncaught terminating error; mirror that.
        $code = 1
        Write-Host "HARNESS CAUGHT THROW: $($_.Exception.Message)"
    } finally {
        Export-Clixml -LiteralPath $case.ResultPath -Depth 10 -InputObject @{
            Hive    = $hive
            SetCall = $setCall.ToArray()
        }
    }
    exit $code
}

# ===========================================================================
# Bytes
# ===========================================================================
function Find-Sequence {
    param([byte[]]$Blob, [byte[]]$Needle, [int]$Start = 0, [int]$End = [int]::MaxValue)
    $last = [Math]::Min($End, $Blob.Length - $Needle.Length)
    for ($i = [Math]::Max(0, $Start); $i -le $last; $i++) {
        $k = 0
        while ($k -lt $Needle.Length -and $Blob[$i + $k] -eq $Needle[$k]) { $k++ }
        if ($k -eq $Needle.Length) { return $i }
    }
    return -1
}

function Test-ByteMatch {
    param([byte[]]$Left, [byte[]]$Right)
    if ($null -eq $Left -or $null -eq $Right) { return $false }
    if ($Left.Length -ne $Right.Length) { return $false }
    for ($i = 0; $i -lt $Left.Length; $i++) { if ($Left[$i] -ne $Right[$i]) { return $false } }
    return $true
}

function ConvertTo-HexText {
    param([byte[]]$Blob)
    if ($null -eq $Blob) { return '<null>' }
    return (($Blob | ForEach-Object { '{0:x2}' -f $_ }) -join '')
}

# ===========================================================================
# Fixtures
# ===========================================================================
function Get-MeasuredRecord {
    param([string]$CsPath, [string]$Name)
    $text = Get-Content -LiteralPath $CsPath -Raw
    $const = [regex]::Match($text, "const\s+string\s+$Name\s*=(?<body>.*?);", 'Singleline')
    if (-not $const.Success) { throw "const $Name not found in $CsPath" }
    $hex = ''
    foreach ($quoted in [regex]::Matches($const.Groups['body'].Value, '"([0-9A-Fa-f]*)"')) {
        $hex += $quoted.Groups[1].Value
    }
    if ($hex.Length -eq 0) { throw "const $Name produced no hex" }
    $blob = [byte[]]::new($hex.Length / 2)
    for ($i = 0; $i -lt $blob.Length; $i++) { $blob[$i] = [Convert]::ToByte($hex.Substring($i * 2, 2), 16) }
    return , $blob
}

# SdpPatchReaderTests.Stock(), byte for byte: remove the inserted marker and
# take 4 off each of the 4 SDP length fields. See FIXTURE PROVENANCE above -
# this is a derivation, not a measurement.
function Get-StockRecord {
    param([byte[]]$Patched, [int]$MarkerOffset)
    $stock = [byte[]]::new($Patched.Length - 4)
    [Array]::Copy($Patched, 0, $stock, 0, $MarkerOffset)
    [Array]::Copy($Patched, $MarkerOffset + 4, $stock, $MarkerOffset, $Patched.Length - $MarkerOffset - 4)

    $attrId = Find-Sequence -Blob $stock -Needle ([byte[]](0x09, 0x02, 0x06))
    if ($attrId -lt 0) { throw 'attr id 09 02 06 not found while deriving the stock record' }
    $stock[$attrId + 10] = [byte]($stock[$attrId + 10] - 4)
    $stock[$attrId + 6] = [byte]($stock[$attrId + 6] - 4)
    $stock[$attrId + 4] = [byte]($stock[$attrId + 4] - 4)
    $top = (([int]$stock[1]) * 256) + $stock[2] - 4
    $stock[1] = [byte][Math]::Floor($top / 256)
    $stock[2] = [byte]($top % 256)
    return , $stock
}

# Move the COL02 close to rid+$Delta by ROTATING the bytes that follow the
# close in front of it. The record keeps its length and every length field
# keeps its value, which padding could not do: the 8-bit string length is
# already 224 and overflows long before the window ends.
function New-CloseDeltaRecord {
    param([byte[]]$Stock, [int]$Delta)
    $rid = Find-Sequence -Blob $Stock -Needle ([byte[]](0x85, 0x47))
    $close = Find-Sequence -Blob $Stock -Needle ([byte[]](0x81, 0x02, 0xC0, 0xC0)) -Start ($rid + 2)
    $shift = $Delta - ($close - $rid)
    if ($shift -lt 0) { throw "cannot move the close backwards (delta $Delta)" }
    if ($close + 4 + $shift -gt $Stock.Length) { throw "not enough tail to rotate for delta $Delta" }
    $moved = [byte[]]::new($Stock.Length)
    [Array]::Copy($Stock, 0, $moved, 0, $close)
    [Array]::Copy($Stock, $close + 4, $moved, $close, $shift)
    [Array]::Copy($Stock, $close, $moved, $close + $shift, 4)
    [Array]::Copy($Stock, $close + 4 + $shift, $moved, $close + 4 + $shift, $Stock.Length - $close - 4 - $shift)
    return , $moved
}

# Break the `c0 c0` half of the close, so no 85 47 site has a shape the script
# recognises and nothing in the record is patchable.
function New-MangledRecord {
    param([byte[]]$Stock)
    $rid = Find-Sequence -Blob $Stock -Needle ([byte[]](0x85, 0x47))
    $close = Find-Sequence -Blob $Stock -Needle ([byte[]](0x81, 0x02, 0xC0, 0xC0)) -Start ($rid + 2)
    $mangled = [byte[]]$Stock.Clone()
    $mangled[$close + 2] = 0x00
    $mangled[$close + 3] = 0x00
    return , $mangled
}

# An independent re-implementation of the script's Edit-Blob, so "what was
# written" is predicted here rather than read back from the thing under test.
function New-ExpectedRecord {
    param([byte[]]$Blob)
    $rid = Find-Sequence -Blob $Blob -Needle ([byte[]](0x85, 0x47))
    if ($rid -lt 0) { throw 'no RID 0x47 in blob' }
    $close = Find-Sequence -Blob $Blob -Needle ([byte[]](0x81, 0x02, 0xC0, 0xC0)) -Start ($rid + 2) -End ($rid + 2 + 96)
    if ($close -lt 0) { throw 'no COL02 close inside the window' }
    $insert = $close + 2
    $new = [byte[]]::new($Blob.Length + 4)
    [Array]::Copy($Blob, 0, $new, 0, $insert)
    [Array]::Copy([byte[]](0x09, 0x20, 0xB1, 0x02), 0, $new, $insert, 4)
    [Array]::Copy($Blob, $insert, $new, $insert + 4, $Blob.Length - $insert)
    $attrId = Find-Sequence -Blob $Blob -Needle ([byte[]](0x09, 0x02, 0x06)) -End $rid
    $new[$attrId + 10] = [byte]($Blob[$attrId + 10] + 4)
    $new[$attrId + 6] = [byte]($Blob[$attrId + 6] + 4)
    $new[$attrId + 4] = [byte]($Blob[$attrId + 4] + 4)
    $top = (([int]$Blob[1]) * 256) + $Blob[2] + 4
    $new[1] = [byte][Math]::Floor($top / 256)
    $new[2] = [byte]($top % 256)
    return , $new
}

# ===========================================================================
# Assertions, counted per case
# ===========================================================================
$script:CaseName = 'startup'
$script:Tally = [ordered]@{}

function Set-Case {
    param([string]$Name)
    $script:CaseName = $Name
    if (-not $script:Tally.Contains($Name)) { $script:Tally[$Name] = @{ Pass = 0; Fail = 0; Detail = @() } }
    Write-Host ''
    Write-Host "== $Name =="
}

function Assert-True {
    param([string]$Name, [bool]$Condition, [string]$Detail = '')
    $case = $script:Tally[$script:CaseName]
    if ($Condition) {
        $case.Pass++
        Write-Host "  PASS  $Name"
        return
    }
    $case.Fail++
    $case.Detail += "$Name`n        $Detail"
    Write-Host "  FAIL  $Name" -ForegroundColor Red
    if ($Detail) { Write-Host "        $Detail" -ForegroundColor Red }
}

function Assert-Equal {
    param([string]$Name, $Expected, $Actual)
    Assert-True -Name $Name -Condition ($Expected -eq $Actual) -Detail "expected <$Expected> actual <$Actual>"
}

# ===========================================================================
# Case runner
# ===========================================================================
function New-CaseFile {
    param(
        [string]$Dir,
        [hashtable]$Hive,
        [string]$LogPath,
        [string]$Mac = 'aabbccddeeff',
        [switch]$DryRun,
        [string]$FailValue,
        [string]$AppData,
        [nullable[datetime]]$StartAt
    )
    New-Item -ItemType Directory -Force -Path $Dir | Out-Null
    $case = @{
        ScriptPath = $script:ScriptCopy
        Hive       = $Hive
        Mac        = $Mac
        LogPath    = $LogPath
        DryRun     = [bool]$DryRun
        FailValue  = $FailValue
        AppData    = $AppData
        StartAt    = $StartAt
        ResultPath = Join-Path $Dir 'result.xml'
    }
    $path = Join-Path $Dir 'case.xml'
    Export-Clixml -LiteralPath $path -InputObject $case -Depth 10
    return $path
}

function Invoke-Case {
    param(
        [string]$Name,
        [hashtable]$Hive,
        [string]$Mac = 'aabbccddeeff',
        [switch]$DryRun,
        [string]$FailValue,
        [string]$AppData,
        # Where the script is told to put its log, relative to the case
        # directory. The backups land beside it, unless that directory cannot
        # be prepared and they fall back to %APPDATA%\MagicMouseTray.
        [string]$LogDir = 'logs'
    )
    $dir = Join-Path $script:RunRoot $Name
    $logsDir = Join-Path $dir $LogDir
    $logPath = Join-Path $logsDir 'kbd-patch.log'
    $consolePath = Join-Path $dir 'console.txt'
    $caseFile = New-CaseFile -Dir $dir -Hive $Hive -LogPath $logPath -Mac $Mac -DryRun:$DryRun -FailValue $FailValue -AppData $AppData

    & pwsh -NoProfile -File $PSCommandPath -CaseFile $caseFile > $consolePath 2>&1
    $code = $LASTEXITCODE

    $hiveBack = @{}
    $setCall = @()
    $resultPath = Join-Path $dir 'result.xml'
    if (Test-Path -LiteralPath $resultPath) {
        $result = Import-Clixml -LiteralPath $resultPath
        $setCall = @($result.SetCall)
        foreach ($subkey in $result.Hive.Keys) {
            $values = $result.Hive[$subkey]
            if ($values -is [string]) { $hiveBack[$subkey] = $values; continue }
            $copy = @{}
            foreach ($valueName in $values.Keys) { $copy[$valueName] = [byte[]]$values[$valueName] }
            $hiveBack[$subkey] = $copy
        }
    }
    # A -LogPath the filesystem will not even look at is a path Test-Path
    # errors on rather than answering, and "no log" is the answer wanted here.
    $logExists = Test-Path -LiteralPath $logPath -ErrorAction SilentlyContinue
    return [pscustomobject]@{
        ExitCode = $code
        Hive     = $hiveBack
        SetCall  = $setCall
        Log      = $(if ($logExists) { Get-Content -LiteralPath $logPath -Raw } else { '' })
        LogExists = $logExists
        LogPath  = $logPath
        LogDir   = $logsDir
        Console  = (Get-Content -LiteralPath $consolePath -Raw)
        Dir      = $dir
    }
}

function Get-Record {
    param($Result, [string]$Subkey, [string]$Name)
    if (-not $Result.Hive.ContainsKey($Subkey)) { return $null }
    $values = $Result.Hive[$Subkey]
    if ($values -is [string]) { return $null }
    if (-not $values.ContainsKey($Name)) { return $null }
    return , ([byte[]]$values[$Name])
}

# ===========================================================================
# Setup
# ===========================================================================
$sourceScript = Join-Path $RepoRoot 'scripts/kbd-patch-cachedservices.ps1'
$fixtureSource = Join-Path $RepoRoot 'MagicMouseTray.Tests/SdpPatchReaderTests.cs'
foreach ($required in $sourceScript, $fixtureSource) {
    if (-not (Test-Path -LiteralPath $required)) { throw "not found: $required (wrong -RepoRoot?)" }
}

if (-not $WorkDir) {
    $WorkDir = Join-Path ([IO.Path]::GetTempPath()) ('kbd-patch-check-' + [guid]::NewGuid().ToString('n'))
}
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$script:RunRoot = Join-Path $WorkDir 'cases'
New-Item -ItemType Directory -Force -Path $script:RunRoot | Out-Null

try {
    Write-Host "script : $sourceScript"
    Write-Host "fixture: $fixtureSource"
    Write-Host "workdir: $WorkDir"

    # ----------------------------------------------------------- script copy
    # `#Requires -RunAsAdministrator` would refuse to run as this user. Strip
    # line 1 and nothing else, and keep the UTF-8 BOM the repo's .ps1 files
    # carry, so what runs below differs from the shipped script by that line.
    $script:ScriptCopy = Join-Path $WorkDir 'kbd-patch-cachedservices-noadmin.ps1'
    $lines = Get-Content -LiteralPath $sourceScript
    if ($lines[0] -notmatch '#Requires\s+-RunAsAdministrator') {
        throw "line 1 of $sourceScript is not the #Requires line: $($lines[0])"
    }
    Set-Content -LiteralPath $script:ScriptCopy -Encoding utf8BOM -Value ($lines[1..($lines.Count - 1)] -join "`n")

    Set-Case 'script copy'
    $copyBytes = [IO.File]::ReadAllBytes($script:ScriptCopy)
    Assert-True -Name 'the copy under test keeps the UTF-8 BOM' `
        -Condition ($copyBytes[0] -eq 0xEF -and $copyBytes[1] -eq 0xBB -and $copyBytes[2] -eq 0xBF) `
        -Detail ('first bytes: ' + (ConvertTo-HexText $copyBytes[0..2]))
    $copyLines = Get-Content -LiteralPath $script:ScriptCopy
    Assert-True -Name 'the copy differs from the shipped script by the #Requires line alone' `
        -Condition ((-not (Compare-Object $copyLines $lines[1..($lines.Count - 1)])) -and $copyLines.Count -eq ($lines.Count - 1)) `
        -Detail "copy=$($copyLines.Count) source=$($lines.Count)"

    # -------------------------------------------------------------- fixtures
    Set-Case 'fixtures'
    Write-Host '  (stock is DERIVED from the measured patched record - see FIXTURE PROVENANCE at the top of this file)'
    $patched = Get-MeasuredRecord -CsPath $fixtureSource -Name 'PatchedHex'
    $nonCandidate = Get-MeasuredRecord -CsPath $fixtureSource -Name 'NonCandidateHex'
    $markerOffset = Find-Sequence -Blob $patched -Needle ([byte[]](0x09, 0x20, 0xB1, 0x02))
    $stock = Get-StockRecord -Patched $patched -MarkerOffset $markerOffset

    Assert-Equal -Name 'the measured patched record is 458 bytes' -Expected 458 -Actual $patched.Length
    Assert-Equal -Name 'patched: RID 85 47 at 258' -Expected 258 -Actual (Find-Sequence -Blob $patched -Needle ([byte[]](0x85, 0x47)))
    Assert-Equal -Name 'patched: marker 09 20 b1 02 at 281' -Expected 281 -Actual $markerOffset
    Assert-Equal -Name 'patched: top length 455' -Expected 455 -Actual ((([int]$patched[1]) * 256) + $patched[2])
    Assert-Equal -Name 'stock: 454 bytes' -Expected 454 -Actual $stock.Length
    Assert-Equal -Name 'stock: RID 85 47 at 258' -Expected 258 -Actual (Find-Sequence -Blob $stock -Needle ([byte[]](0x85, 0x47)))
    Assert-Equal -Name 'stock: close 81 02 c0 c0 at 279, 21 bytes past the RID' -Expected 279 `
        -Actual (Find-Sequence -Blob $stock -Needle ([byte[]](0x81, 0x02, 0xC0, 0xC0)))
    Assert-Equal -Name 'stock: no marker anywhere' -Expected (-1) -Actual (Find-Sequence -Blob $stock -Needle ([byte[]](0x09, 0x20, 0xB1, 0x02)))
    $attrId = Find-Sequence -Blob $stock -Needle ([byte[]](0x09, 0x02, 0x06))
    Assert-Equal -Name 'stock: attr id 09 02 06 at 174' -Expected 174 -Actual $attrId
    Assert-Equal -Name 'stock: outer length 230' -Expected 230 -Actual ([int]$stock[$attrId + 4])
    Assert-Equal -Name 'stock: inner length 228' -Expected 228 -Actual ([int]$stock[$attrId + 6])
    Assert-Equal -Name 'stock: string length 224' -Expected 224 -Actual ([int]$stock[$attrId + 10])
    Assert-Equal -Name 'stock: top length 451' -Expected 451 -Actual ((([int]$stock[1]) * 256) + $stock[2])
    Assert-True -Name 'the expected-record builder turns the derived stock back into the measured record' `
        -Condition (Test-ByteMatch (New-ExpectedRecord -Blob $stock) $patched) `
        -Detail ('built   =' + (ConvertTo-HexText (New-ExpectedRecord -Blob $stock)) + "`n        measured=" + (ConvertTo-HexText $patched))
    Assert-Equal -Name 'non-candidate fixture is 84 bytes' -Expected 84 -Actual $nonCandidate.Length
    Assert-Equal -Name 'non-candidate carries no RID 0x47' -Expected (-1) -Actual (Find-Sequence -Blob $nonCandidate -Needle ([byte[]](0x85, 0x47)))

    $mangled = New-MangledRecord -Stock $stock
    Assert-Equal -Name 'mangled: the close is gone from the window' -Expected (-1) `
        -Actual (Find-Sequence -Blob $mangled -Needle ([byte[]](0x81, 0x02, 0xC0, 0xC0)) -Start 260 -End (258 + 2 + 96))
    Assert-Equal -Name 'mangled: no marker in the window' -Expected (-1) `
        -Actual (Find-Sequence -Blob $mangled -Needle ([byte[]](0x09, 0x20, 0xB1, 0x02)) -Start 260 -End (258 + 2 + 98))
    Assert-Equal -Name 'mangled: still 454 bytes' -Expected 454 -Actual $mangled.Length

    # The window is 96 bytes and the script searches two further for a marker,
    # so a close at rid+98 is the last one it can use and rid+99 is the first
    # it must refuse.
    $deltas = 21, 60, 92, 93, 95, 96, 97, 98, 99, 100
    $deltaRecord = @{}
    foreach ($delta in $deltas) {
        $moved = New-CloseDeltaRecord -Stock $stock -Delta $delta
        $deltaRecord[$delta] = $moved
        $movedRid = Find-Sequence -Blob $moved -Needle ([byte[]](0x85, 0x47))
        $movedClose = Find-Sequence -Blob $moved -Needle ([byte[]](0x81, 0x02, 0xC0, 0xC0)) -Start ($movedRid + 2)
        $fieldsHeld = ((Test-ByteMatch $moved[0..2] $stock[0..2]) -and
            ($moved[$attrId + 4] -eq $stock[$attrId + 4]) -and
            ($moved[$attrId + 6] -eq $stock[$attrId + 6]) -and
            ($moved[$attrId + 10] -eq $stock[$attrId + 10]))
        Assert-True -Name "delta $delta fixture: RID 258, close at 258+$delta, length and length fields unchanged" `
            -Condition (($movedRid -eq 258) -and ($movedClose -eq (258 + $delta)) -and ($moved.Length -eq 454) -and $fieldsHeld) `
            -Detail "rid=$movedRid close=$movedClose length=$($moved.Length) fieldsHeld=$fieldsHeld"
    }

    # ---------------------------------------------------------------- case 1
    Set-Case 'a stock record, with a non-candidate record beside it'
    $r1 = Invoke-Case -Name 'c01-stock' -Hive @{
        'CachedServices' = @{ '00010000' = $stock; '00010001' = $nonCandidate }
    }
    Assert-Equal -Name 'exit 0' -Expected 0 -Actual $r1.ExitCode
    $written1 = Get-Record $r1 'CachedServices' '00010000'
    Assert-True -Name 'the bytes written are byte-identical to the measured 458-byte patched record' `
        -Condition (Test-ByteMatch $written1 $patched) `
        -Detail ('written =' + (ConvertTo-HexText $written1) + "`n        measured=" + (ConvertTo-HexText $patched))
    Assert-True -Name 'the non-candidate record beside it is untouched' `
        -Condition (Test-ByteMatch (Get-Record $r1 'CachedServices' '00010001') $nonCandidate) -Detail 'non-candidate changed'
    Assert-True -Name 'a log was written' -Condition ($r1.LogExists -and $r1.Log.Length -gt 0) -Detail "log at $($r1.LogPath)"
    Assert-True -Name 'log: the length offsets it read, outer=178(=230) inner=180(=228) str=184(=224)' `
        -Condition ($r1.Log -match 'length offsets: outer=178\(=230\) inner=180\(=228\) str=184\(=224\)') -Detail $r1.Log
    Assert-True -Name 'log: the 16-bit top length it read, 451' `
        -Condition ($r1.Log -match 'top record length 16-bit at offsets 1\.\.2 = 451') -Detail $r1.Log
    Assert-True -Name 'log: the lengths it wrote, outer=234 inner=232 str=228 top=455' `
        -Condition ($r1.Log -match 'new lengths: outer=234 inner=232 str=228 top=455') -Detail $r1.Log
    Assert-True -Name 'log: WRITTEN to registry, new length 458' `
        -Condition ($r1.Log -match 'WRITTEN to registry\. New length: 458 bytes') -Detail $r1.Log
    Assert-True -Name 'log: DONE' -Condition ($r1.Log -match 'DONE\.') -Detail $r1.Log
    Assert-True -Name 'log: the non-candidate was skipped for want of a RID' `
        -Condition ($r1.Log -match '00010001\] no RID 0x47 - skip') -Detail $r1.Log
    $backup1 = @(Get-ChildItem -LiteralPath $r1.LogDir -Filter 'backup-CachedServices-00010000-*.bin')
    Assert-True -Name 'one backup of the original was written beside the log' -Condition ($backup1.Count -eq 1) `
        -Detail ((Get-ChildItem -LiteralPath $r1.LogDir | ForEach-Object Name) -join ', ')
    if ($backup1.Count -eq 1) {
        Assert-True -Name 'the backup holds the bytes the record had before the run' `
            -Condition (Test-ByteMatch ([IO.File]::ReadAllBytes($backup1[0].FullName)) $stock) -Detail 'backup differs from stock'
    }

    # ---------------------------------------------------------------- case 2
    Set-Case 'a second run over the record the first one patched'
    $r2 = Invoke-Case -Name 'c02-rerun' -Hive @{ 'CachedServices' = @{ '00010000' = $patched } }
    Assert-Equal -Name 'exit 0' -Expected 0 -Actual $r2.ExitCode
    Assert-True -Name 'the record is untouched' -Condition (Test-ByteMatch (Get-Record $r2 'CachedServices' '00010000') $patched) `
        -Detail 'the record changed on a re-run'
    Assert-True -Name 'log: already patched at offset 281' `
        -Condition ($r2.Log -match 'already patched - 09 20 b1 02 at offset 281, left untouched') -Detail $r2.Log
    Assert-True -Name 'no registry write was attempted at all' -Condition ($r2.SetCall.Count -eq 0) `
        -Detail ('Set-ItemProperty calls: ' + ($r2.SetCall -join ','))
    Assert-True -Name 'log: NOTHING TO DO' -Condition ($r2.Log -match 'NOTHING TO DO') -Detail $r2.Log

    # ---------------------------------------------------------------- case 3
    Set-Case 'a half-patched cache: one subkey done, the other still stock'
    $r3 = Invoke-Case -Name 'c03-half' -Hive @{
        'CachedServices'        = @{ '00010000' = $patched }
        'DynamicCachedServices' = @{ '00010000' = $stock }
    }
    Assert-Equal -Name 'exit 0' -Expected 0 -Actual $r3.ExitCode
    Assert-True -Name 'the already-patched subkey is untouched' `
        -Condition (Test-ByteMatch (Get-Record $r3 'CachedServices' '00010000') $patched) -Detail 'the patched record changed'
    Assert-True -Name 'the stock subkey is completed to the measured patched bytes' `
        -Condition (Test-ByteMatch (Get-Record $r3 'DynamicCachedServices' '00010000') $patched) -Detail 'the stock record was not completed'
    Assert-True -Name 'log: SUMMARY candidates=2 patched=1 already=1 blocked=0' `
        -Condition ($r3.Log -match 'SUMMARY candidates=2 patched=1 already=1 blocked=0 would_patch=0') -Detail $r3.Log

    # ---------------------------------------------------------------- case 4
    Set-Case 'no record carries Report ID 0x47'
    $r4 = Invoke-Case -Name 'c04-no-rid' -Hive @{ 'CachedServices' = @{ '00010001' = $nonCandidate } }
    Assert-Equal -Name 'exit 2' -Expected 2 -Actual $r4.ExitCode
    Assert-True -Name 'log: NO RECORD' -Condition ($r4.Log -match 'NO RECORD\.') -Detail $r4.Log
    Assert-True -Name 'the record is untouched' -Condition (Test-ByteMatch (Get-Record $r4 'CachedServices' '00010001') $nonCandidate) `
        -Detail 'the record changed'
    Assert-True -Name 'the log redacts the address in \Devices\<mac>' -Condition ($r4.Log -match '\\Devices\\<mac>') -Detail $r4.Log
    Assert-True -Name 'the console copy still carries the real address, so -Mac can be checked' `
        -Condition ($r4.Console -match '\\Devices\\aabbccddeeff') -Detail 'the console was redacted too'

    # ---------------------------------------------------------------- case 5
    Set-Case 'a record whose COL02 close is mangled'
    $r5 = Invoke-Case -Name 'c05-mangled' -Hive @{ 'CachedServices' = @{ '00010000' = $mangled } }
    Assert-Equal -Name 'exit 3' -Expected 3 -Actual $r5.ExitCode
    Assert-True -Name 'nothing was written' -Condition ($r5.SetCall.Count -eq 0) `
        -Detail ('Set-ItemProperty calls: ' + ($r5.SetCall -join ','))
    Assert-True -Name 'the record is untouched' -Condition (Test-ByteMatch (Get-Record $r5 'CachedServices' '00010000') $mangled) `
        -Detail 'the record changed'
    Assert-True -Name 'log: descriptor shape not recognised, nothing written' `
        -Condition ($r5.Log -match 'descriptor shape not recognised, nothing written') -Detail $r5.Log
    Assert-True -Name 'log: the hex dump header' -Condition ($r5.Log -match 'bytes around RID 0x47') -Detail $r5.Log
    $dumpRows = @($r5.Log -split "`n" | Where-Object { $_ -match '^\s+\d+: ([0-9a-f]{2} ?)+$' })
    Assert-True -Name 'log: the hex dump rows a bug report needs' -Condition ($dumpRows.Count -ge 7) -Detail "rows=$($dumpRows.Count)"
    Assert-True -Name 'log: NOT PATCHED, 1 record carries Report ID 0x47' `
        -Condition ($r5.Log -match 'NOT PATCHED\. 1 record\(s\) carry Report ID 0x47') -Detail $r5.Log
    Assert-True -Name 'log: the request to publish it says the log quotes the keyboard''s own record' `
        -Condition ($r5.Log -match 'Please report .* \(it quotes a hex excerpt of this keyboard''s own cached Bluetooth record\) at https') -Detail $r5.Log

    # ---------------------------------------------------------------- case 6
    Set-Case 'one patchable record and one mangled record'
    $r6 = Invoke-Case -Name 'c06-partial' -Hive @{
        'CachedServices' = @{ '00010000' = $stock; '00010002' = $mangled }
    }
    Assert-Equal -Name 'exit 4' -Expected 4 -Actual $r6.ExitCode
    Assert-True -Name 'the patchable record was still written' `
        -Condition (Test-ByteMatch (Get-Record $r6 'CachedServices' '00010000') $patched) -Detail 'the patchable record was not written'
    Assert-True -Name 'the mangled record is untouched' `
        -Condition (Test-ByteMatch (Get-Record $r6 'CachedServices' '00010002') $mangled) -Detail 'the mangled record changed'
    Assert-True -Name 'log: PARTIAL, 1 record patched' -Condition ($r6.Log -match 'PARTIAL\. 1 record\(s\) patched') -Detail $r6.Log

    # ---------------------------------------------------------------- case 7
    Set-Case 'the registry write fails on the first of two patchable records'
    $r7 = Invoke-Case -Name 'c07-write-fails' -FailValue '00010000' -Hive @{
        'CachedServices' = @{ '00010000' = $stock; '00010002' = $stock }
    }
    Assert-Equal -Name 'exit 4' -Expected 4 -Actual $r7.ExitCode
    Assert-True -Name 'the loop survived: both records reached Set-ItemProperty' `
        -Condition (($r7.SetCall -contains '00010000') -and ($r7.SetCall -contains '00010002')) `
        -Detail ('Set-ItemProperty calls: ' + ($r7.SetCall -join ','))
    Assert-True -Name 'the record whose write failed is exactly as it was' `
        -Condition (Test-ByteMatch (Get-Record $r7 'CachedServices' '00010000') $stock) -Detail 'the failed record changed'
    Assert-True -Name 'the record after it was still written' `
        -Condition (Test-ByteMatch (Get-Record $r7 'CachedServices' '00010002') $patched) -Detail 'the second record was not written'
    Assert-True -Name 'log: the registry write failed, with the reason' `
        -Condition ($r7.Log -match 'NOT patched: the registry write failed: injected registry write failure') -Detail $r7.Log
    Assert-True -Name 'log: a hex dump for the record that failed' `
        -Condition (@($r7.Log -split "`n" | Where-Object { $_ -match '^\s+\d+: ([0-9a-f]{2} ?)+$' }).Count -ge 7) -Detail 'no hex dump'

    # ---------------------------------------------------------------- case 8
    Set-Case 'a cache subkey that will not open, beside one that will'
    $r8 = Invoke-Case -Name 'c08-unreadable' -Hive @{
        'CachedServices'        = @{ '00010000' = $stock }
        'DynamicCachedServices' = 'Requested registry access is not allowed.'
    }
    Assert-Equal -Name 'exit 4' -Expected 4 -Actual $r8.ExitCode
    Assert-True -Name 'the readable record was patched' `
        -Condition (Test-ByteMatch (Get-Record $r8 'CachedServices' '00010000') $patched) -Detail 'the readable record was not patched'
    Assert-True -Name 'log: the subkey was NOT read, with the reason' `
        -Condition ($r8.Log -match '\[DynamicCachedServices\] NOT read: Requested registry access is not allowed\.') -Detail $r8.Log

    # ---------------------------------------------------------------- case 9
    Set-Case 'every cache subkey refuses to open'
    $r9 = Invoke-Case -Name 'c09-all-unreadable' -Hive @{
        'CachedServices'        = 'Requested registry access is not allowed.'
        'DynamicCachedServices' = 'Requested registry access is not allowed.'
    }
    Assert-Equal -Name 'exit 5: nothing was examined, so this is not evidence about the descriptor' -Expected 5 -Actual $r9.ExitCode
    Assert-True -Name 'nothing was written' -Condition ($r9.SetCall.Count -eq 0) `
        -Detail ('Set-ItemProperty calls: ' + ($r9.SetCall -join ','))
    Assert-True -Name 'log: 2 cache subkeys could not be read' `
        -Condition ($r9.Log -match 'NOT PATCHED\. 2 cache subkey\(s\) could not be read') -Detail $r9.Log
    Assert-True -Name 'log: it does not claim this keyboard has no record' -Condition ($r9.Log -notmatch 'NO RECORD\.') -Detail $r9.Log
    Assert-True -Name 'log: nothing was dumped, so the request to publish it claims no excerpt' `
        -Condition ($r9.Log -notmatch 'hex excerpt') -Detail $r9.Log

    # --------------------------------------------------------------- case 10
    Set-Case '-DryRun over a stock record'
    $r10 = Invoke-Case -Name 'c10-dry-run' -DryRun -Hive @{ 'CachedServices' = @{ '00010000' = $stock } }
    Assert-Equal -Name 'exit 0' -Expected 0 -Actual $r10.ExitCode
    Assert-True -Name 'the registry is untouched' -Condition (Test-ByteMatch (Get-Record $r10 'CachedServices' '00010000') $stock) `
        -Detail 'a dry run modified the registry'
    Assert-True -Name 'not one Set-ItemProperty call' -Condition ($r10.SetCall.Count -eq 0) `
        -Detail ('Set-ItemProperty calls: ' + ($r10.SetCall -join ','))
    Assert-True -Name 'log: it never claims the unlock was installed' -Condition ($r10.Log -notmatch 'DONE\. Toggle') -Detail $r10.Log
    Assert-True -Name 'log: DRY-RUN COMPLETE' -Condition ($r10.Log -match 'DRY-RUN COMPLETE\.') -Detail $r10.Log
    $proposal = @(Get-ChildItem -LiteralPath $r10.LogDir -Filter 'patched-CachedServices-00010000-*.bin')
    Assert-True -Name 'a patched-*.bin proposal was saved beside the backup' `
        -Condition (($proposal.Count -eq 1) -and (@(Get-ChildItem -LiteralPath $r10.LogDir -Filter 'backup-CachedServices-00010000-*.bin').Count -eq 1)) `
        -Detail ((Get-ChildItem -LiteralPath $r10.LogDir | ForEach-Object Name) -join ', ')
    if ($proposal.Count -eq 1) {
        Assert-True -Name 'the proposal is byte-identical to the measured patched record' `
            -Condition (Test-ByteMatch ([IO.File]::ReadAllBytes($proposal[0].FullName)) $patched) -Detail 'the proposal differs from the fixture'
    }

    # --------------------------------------------------------------- case 11
    Set-Case 'a COL02 close 21..98 bytes past the RID is still inside the window'
    foreach ($delta in 21, 60, 92, 93, 95, 96, 97, 98) {
        $record = $deltaRecord[$delta]
        $expected = New-ExpectedRecord -Blob $record
        $first = Invoke-Case -Name "c11-delta$delta-run1" -Hive @{ 'CachedServices' = @{ '00010000' = $record } }
        Assert-Equal -Name "delta $delta first run: exit 0" -Expected 0 -Actual $first.ExitCode
        $firstBytes = Get-Record $first 'CachedServices' '00010000'
        Assert-True -Name "delta $delta first run: marker written at $(258 + $delta + 2)" `
            -Condition (Test-ByteMatch $firstBytes $expected) `
            -Detail ('written=' + (ConvertTo-HexText $firstBytes) + "`n        wanted =" + (ConvertTo-HexText $expected))
        $second = Invoke-Case -Name "c11-delta$delta-run2" -Hive @{ 'CachedServices' = @{ '00010000' = $firstBytes } }
        Assert-Equal -Name "delta $delta second run: exit 0" -Expected 0 -Actual $second.ExitCode
        Assert-True -Name "delta $delta second run: the marker is found again and nothing is rewritten" `
            -Condition (($second.Log -match 'already patched - 09 20 b1 02 at offset') -and ($second.SetCall.Count -eq 0)) `
            -Detail $second.Log
    }

    # --------------------------------------------------------------- case 12
    Set-Case 'a COL02 close 99 or 100 bytes past the RID is out of the window'
    foreach ($delta in 99, 100) {
        $record = $deltaRecord[$delta]
        $far = Invoke-Case -Name "c12-delta$delta" -Hive @{ 'CachedServices' = @{ '00010000' = $record } }
        Assert-Equal -Name "delta ${delta}: exit 3" -Expected 3 -Actual $far.ExitCode
        Assert-True -Name "delta ${delta}: refused, nothing written" `
            -Condition (($far.SetCall.Count -eq 0) -and (Test-ByteMatch (Get-Record $far 'CachedServices' '00010000') $record)) `
            -Detail ('Set-ItemProperty calls: ' + ($far.SetCall -join ','))
    }

    # --------------------------------------------------------------- case 13
    Set-Case '-Mac that is not 12 hex digits'
    $r13 = Invoke-Case -Name 'c13-bad-mac' -Mac 'not-12-hex' -Hive @{ 'CachedServices' = @{ '00010000' = $stock } }
    Assert-Equal -Name 'exit 1' -Expected 1 -Actual $r13.ExitCode
    Assert-True -Name 'a log was written anyway, so the hidden console is not the only record' `
        -Condition ($r13.LogExists -and $r13.Log.Length -gt 0) -Detail "log at $($r13.LogPath) exists=$($r13.LogExists)"
    Assert-True -Name 'log: BAD ARGUMENT, and nothing was read or written' `
        -Condition ($r13.Log -match 'BAD ARGUMENT\..*Nothing was read from or written to the registry\.') -Detail $r13.Log
    Assert-True -Name 'the registry was never opened' `
        -Condition (($r13.SetCall.Count -eq 0) -and (Test-ByteMatch (Get-Record $r13 'CachedServices' '00010000') $stock)) `
        -Detail 'the registry was touched'

    # --------------------------------------------------------------- case 14
    Set-Case 'the log is safe to paste into a public issue'
    $mac = 'aabbccddeeff'
    $r14 = Invoke-Case -Name 'c14-redaction' -Mac $mac -Hive @{
        'CachedServices'        = @{ '00010000' = $mangled }
        'DynamicCachedServices' = @{ '00010000' = $stock }
    }
    Assert-True -Name 'the log carries no bare 12-hex address anywhere' `
        -Condition ($r14.Log -notmatch "(?i)(?<![0-9A-Fa-f])$mac(?![0-9A-Fa-f])") `
        -Detail (($r14.Log -split "`n" | Where-Object { $_ -match "(?i)$mac" }) -join "`n        ")
    Assert-True -Name 'log: mac=<mac>' -Condition ($r14.Log -match 'mac=<mac>') -Detail $r14.Log
    Assert-True -Name 'the console copy still carries the real address' -Condition ($r14.Console -match "(?i)$mac") -Detail 'the console was redacted'
    $logRows = @($r14.Log -split "`r?`n" | Where-Object { $_ -match '^\s+\d+: ' })
    $consoleRows = @($r14.Console -split "`r?`n" | Where-Object { $_ -match '^\s+\d+: ' })
    Assert-True -Name 'redaction leaves the hex dump rows and their offsets alone' `
        -Condition (($logRows.Count -gt 0) -and ($logRows.Count -eq $consoleRows.Count) -and (-not (Compare-Object $logRows $consoleRows))) `
        -Detail ("log rows=$($logRows.Count) console rows=$($consoleRows.Count)`n        " + (($logRows | Select-Object -First 3) -join "`n        "))
    Assert-True -Name 'the dump rows carry decimal byte offsets, starting at 242' `
        -Condition (($logRows -join "`n") -match '^\s+242: ') -Detail (($logRows | Select-Object -First 1))

    # --------------------------------------------------------------- case 15
    Set-Case 'a registry value name holding backslashes cannot steer the backup out of its directory'
    $hostileName = '..\..\..\evil'
    $r15 = Invoke-Case -Name 'c15-hostile-name' -LogDir 'a/b/logs' -Hive @{
        'CachedServices' = @{ $hostileName = $stock }
    }
    Assert-Equal -Name 'exit 0' -Expected 0 -Actual $r15.ExitCode
    $inside = @(Get-ChildItem -LiteralPath $r15.LogDir -File | Where-Object { $_.Name -like 'backup-CachedServices-*evil*' })
    Assert-True -Name 'the backup landed inside the backup directory, its name sanitised' `
        -Condition (($inside.Count -eq 1) -and ($inside[0].Name -match '^backup-CachedServices-\.\._\.\._\.\._evil-\d{8}-\d{6}-\d+\.bin$')) `
        -Detail ((Get-ChildItem -LiteralPath $r15.LogDir -File | ForEach-Object Name) -join ', ')
    $stray = @(Get-ChildItem -LiteralPath $r15.Dir -Recurse -File |
            Where-Object { $_.Name -like '*evil*' -and $_.DirectoryName -ne (Resolve-Path $r15.LogDir).Path })
    Assert-True -Name 'no file named after it appears anywhere else under the case directory' `
        -Condition ($stray.Count -eq 0) -Detail (($stray | ForEach-Object FullName) -join ', ')
    Assert-True -Name 'the log still names the record by its real value name' `
        -Condition ($r15.Log -match '\[CachedServices\\\.\.\\\.\.\\\.\.\\evil\]') -Detail $r15.Log
    Assert-True -Name 'the record was patched' -Condition (Test-ByteMatch (Get-Record $r15 'CachedServices' $hostileName) $patched) `
        -Detail 'the record with the hostile name was not patched'

    # --------------------------------------------------------------- case 16
    Set-Case 'two runs inside the same second cannot share one backup filename'
    $sharedDir = Join-Path $script:RunRoot 'c16-same-second'
    $sharedLogDir = Join-Path $sharedDir 'logs'
    New-Item -ItemType Directory -Force -Path $sharedLogDir | Out-Null
    $startAt = (Get-Date).AddSeconds(3)
    $startAt = $startAt.AddMilliseconds(200 - $startAt.Millisecond)
    $children = @()
    foreach ($tag in 'a', 'b') {
        $caseFile = New-CaseFile -Dir (Join-Path $sharedDir $tag) `
            -Hive @{ 'CachedServices' = @{ '00010000' = $stock } } `
            -LogPath (Join-Path $sharedLogDir "kbd-patch-$tag.log") -StartAt $startAt
        $children += Start-Process -FilePath 'pwsh' -PassThru `
            -RedirectStandardOutput (Join-Path $sharedDir "console-$tag.txt") `
            -ArgumentList @('-NoProfile', '-File', $PSCommandPath, '-CaseFile', $caseFile)
    }
    $children | Wait-Process
    $backups = @(Get-ChildItem -LiteralPath $sharedLogDir -Filter 'backup-*.bin' | Sort-Object Name)
    $stamps = @($backups | ForEach-Object { if ($_.Name -match '-(\d{8}-\d{6})-\d+\.bin$') { $Matches[1] } })
    Assert-True -Name 'both runs backed the record up' -Condition ($backups.Count -eq 2) `
        -Detail (($backups | ForEach-Object Name) -join ', ')
    Assert-True -Name 'both backups are stamped with the same second' `
        -Condition (($stamps.Count -eq 2) -and ($stamps[0] -eq $stamps[1])) -Detail ($stamps -join ' vs ')
    Assert-True -Name 'the two backups still have different filenames, so neither overwrote the other' `
        -Condition (($backups.Count -eq 2) -and ($backups[0].Name -ne $backups[1].Name)) `
        -Detail (($backups | ForEach-Object Name) -join ', ')

    # A path component no filesystem will take, so the directory under it
    # cannot be prepared on Windows or on this harness's own OS.
    $unmakeable = 'x' * 300

    # --------------------------------------------------------------- case 17
    Set-Case 'a log directory that cannot be prepared costs the run its log, not the unlock'
    $fallbackRoot = Join-Path (Join-Path $script:RunRoot 'c17-no-log-dir') 'appdata'
    $r17 = Invoke-Case -Name 'c17-no-log-dir' -LogDir "$unmakeable/logs" -AppData $fallbackRoot `
        -Hive @{ 'CachedServices' = @{ '00010000' = $stock } }
    $fallbackDir = Join-Path $fallbackRoot 'MagicMouseTray'
    Assert-Equal -Name 'exit 0: the unlock still landed' -Expected 0 -Actual $r17.ExitCode
    Assert-True -Name 'the record was patched to the measured bytes' `
        -Condition (Test-ByteMatch (Get-Record $r17 'CachedServices' '00010000') $patched) -Detail 'the record was not patched'
    Assert-True -Name 'no log file was written' -Condition (-not $r17.LogExists) -Detail "log at $($r17.LogPath)"
    Assert-True -Name 'console: NO LOG, with the reason and the directory the backups went to instead' `
        -Condition ($r17.Console -match 'NO LOG\.' -and $r17.Console -match ([regex]::Escape("the backup copies go to $fallbackDir instead"))) `
        -Detail $r17.Console
    Assert-True -Name 'console: the log is named as NOT WRITTEN, not as a file to open' `
        -Condition ($r17.Console -match 'Log: NOT WRITTEN \(') -Detail $r17.Console
    $fallbackFiles = @(if (Test-Path -LiteralPath $fallbackDir) { Get-ChildItem -LiteralPath $fallbackDir | ForEach-Object Name })
    $fallbackBackup = @($fallbackFiles | Where-Object { $_ -like 'backup-CachedServices-00010000-*.bin' })
    Assert-True -Name 'the original was still saved, in the fallback directory' -Condition ($fallbackBackup.Count -eq 1) `
        -Detail "$fallbackDir holds: $($fallbackFiles -join ', ')"
    if ($fallbackBackup.Count -eq 1) {
        $fallbackPath = Join-Path $fallbackDir $fallbackBackup[0]
        Assert-True -Name 'the fallback backup holds the bytes the record had before the run' `
            -Condition (Test-ByteMatch ([IO.File]::ReadAllBytes($fallbackPath)) $stock) -Detail 'backup differs from stock'
        Assert-True -Name 'console: the backup line names the file that is really there' `
            -Condition ($r17.Console -match ([regex]::Escape("backed up -> $fallbackPath"))) -Detail $r17.Console
    }

    # --------------------------------------------------------------- case 18
    Set-Case 'neither the log directory nor the fallback can be prepared'
    $r18 = Invoke-Case -Name 'c18-no-backup-dir' -LogDir "$unmakeable/logs" `
        -AppData (Join-Path (Join-Path $script:RunRoot 'c18-no-backup-dir') $unmakeable) `
        -Hive @{ 'CachedServices' = @{ '00010000' = $stock } }
    Assert-Equal -Name 'exit 1: the run stopped before it read anything' -Expected 1 -Actual $r18.ExitCode
    Assert-True -Name 'console: CANNOT START, naming the backup directory as the reason' `
        -Condition ($r18.Console -match 'CANNOT START\. No directory could be prepared for the backup copies:') -Detail $r18.Console
    Assert-True -Name 'console: it does not blame the descriptor' `
        -Condition ($r18.Console -notmatch 'descriptor shape') -Detail $r18.Console
    Assert-True -Name 'the registry was never written' `
        -Condition (($r18.SetCall.Count -eq 0) -and (Test-ByteMatch (Get-Record $r18 'CachedServices' '00010000') $stock)) `
        -Detail ('Set-ItemProperty calls: ' + ($r18.SetCall -join ','))

    # --------------------------------------------------------------- case 19
    Set-Case 'a rejected -Mac leaves no address in the file the user is asked to publish'
    # Neither shape can be caught by the two redaction rules: rule one needs
    # ':' or '-' separators, rule two needs exactly 12 contiguous hex digits
    # right after `mac=`.
    foreach ($bad in '0xE806884B0741', 'e806884b07415') {
        $r19 = Invoke-Case -Name "c19-bad-mac-$($bad.Length)" -Mac $bad -Hive @{ 'CachedServices' = @{ '00010000' = $stock } }
        Assert-Equal -Name "$bad : exit 1" -Expected 1 -Actual $r19.ExitCode
        Assert-True -Name "$bad : a log was written" -Condition ($r19.LogExists -and $r19.Log.Length -gt 0) -Detail "log at $($r19.LogPath)"
        Assert-True -Name "$bad : the log carries the address nowhere" `
            -Condition ($r19.Log -notmatch '(?i)e806.{0,2}884b.{0,2}0741') `
            -Detail (($r19.Log -split "`n" | Where-Object { $_ -match '(?i)884b' }) -join "`n        ")
        Assert-True -Name "$bad : log: mac=<mac>" -Condition ($r19.Log -match 'mac=<mac>') -Detail $r19.Log
        Assert-True -Name "$bad : the console keeps the value the user typed, so -Mac can be checked" `
            -Condition ($r19.Console -match ([regex]::Escape("mac=$bad"))) -Detail $r19.Console
    }

    # --------------------------------------------------------------- case 20
    Set-Case 'two value names that sanitise to one backup filename'
    # Which of the two the enumeration reaches first is the hive's business;
    # what matters is that only one of them was touched.
    $r20 = Invoke-Case -Name 'c20-name-collision' -Hive @{
        'CachedServices' = @{ 'A/B' = $stock; 'A:B' = $stock }
    }
    $collide = @('A/B', 'A:B')
    $donePart = @($collide | Where-Object { Test-ByteMatch (Get-Record $r20 'CachedServices' $_) $patched })
    $keptPart = @($collide | Where-Object { Test-ByteMatch (Get-Record $r20 'CachedServices' $_) $stock })
    Assert-Equal -Name 'exit 4: one patched, one blocked' -Expected 4 -Actual $r20.ExitCode
    Assert-True -Name 'one of the two records was patched and the other left exactly as it was' `
        -Condition (($donePart.Count -eq 1) -and ($keptPart.Count -eq 1)) `
        -Detail "patched: $($donePart -join ',') untouched: $($keptPart -join ',')"
    Assert-True -Name 'the registry was written once, for the record that kept its backup' `
        -Condition (($r20.SetCall.Count -eq 1) -and ($donePart.Count -eq 1) -and ($r20.SetCall[0] -eq $donePart[0])) `
        -Detail ('Set-ItemProperty calls: ' + ($r20.SetCall -join ','))
    $shared = @(Get-ChildItem -LiteralPath $r20.LogDir -Filter 'backup-CachedServices-A_B-*.bin')
    Assert-True -Name 'one backup file, holding the original of the record that was patched' `
        -Condition (($shared.Count -eq 1) -and (Test-ByteMatch ([IO.File]::ReadAllBytes($shared[0].FullName)) $stock)) `
        -Detail ((Get-ChildItem -LiteralPath $r20.LogDir | ForEach-Object Name) -join ', ')
    Assert-True -Name 'log: the collision is named, not silently overwritten' `
        -Condition ($r20.Log -match 'already holds another record''s original from this run') -Detail $r20.Log

    # ------------------------------------------------------------ per case tally
    Write-Host ''
    Write-Host '================ TALLY ================'
    $passed = 0
    $failed = 0
    foreach ($name in $script:Tally.Keys) {
        $case = $script:Tally[$name]
        $passed += $case.Pass
        $failed += $case.Fail
        $mark = if ($case.Fail -gt 0) { 'FAIL' } else { 'pass' }
        Write-Host ('  {0}  {1,3}/{2,-3}  {3}' -f $mark, $case.Pass, ($case.Pass + $case.Fail), $name)
    }
    Write-Host ('  TOTAL {0}/{1} assertions passed across {2} case(s)' -f $passed, ($passed + $failed), $script:Tally.Count)

    if ($failed -gt 0) {
        Write-Host ''
        Write-Host '---- FAILURES ----'
        foreach ($name in $script:Tally.Keys) {
            foreach ($detail in $script:Tally[$name].Detail) { Write-Host "  - [$name] $detail" }
        }
    }
    exit ([int]($failed -gt 0))
} finally {
    if ($KeepWorkDir) {
        Write-Host "work directory kept: $WorkDir"
    } else {
        Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
