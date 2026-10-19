#Requires -RunAsAdministrator
# Patch CachedServices SDP record to expose RID 0x47 as Feature on COL02.
#
# Inserts `09 20 B1 02` at the COL02 close inside the HID Report Descriptor
# stored in the BTHPORT cache, and increments 4 SDP length fields.
#
# Run elevated. Modifies HKLM. Backs up original blobs first.
# Requires -Mac (12 hex digits). No default MAC. Backups go to
# %APPDATA%\MagicMouseTray, or %TEMP%\MagicMouseTray if APPDATA is unset.
#
# RUNNING THIS TWICE IS SAFE AND IS NOT AN ERROR (issue #162). The insert is
# idempotent: a record that already carries `09 20 B1 02` beside `85 47` is
# reported as already patched and left exactly as it is. It used to throw
# "COL02 close pattern '81 02 c0 c0' not found" on the second run - the stock
# close is GONE once the insert is in, so the only record the script had
# already fixed was the one it then called a failure - and the tray, which runs
# this with a hidden window, could only relay "powershell.exe exited 1".
#
# Every line printed is also appended to a run log (-LogPath, default
# <backup dir>\kbd-patch-<stamp>.log) so the caller that elevated this script
# can read back what happened in a window the user never got to see.
#
# Exit codes - the tray turns these into what it tells the user:
#   0  the cache carries the unlock: this run inserted it, or it was already
#      there. Cycling Bluetooth is what makes hidbth re-read it.
#   2  no cached record for this MAC carries Report ID 0x47 at all: wrong
#      -Mac, or this keyboard has never been paired on this PC.
#   3  a record carries RID 0x47 but could not be patched - an unrecognised
#      descriptor shape. Nothing was written. The log holds the bytes.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Mac,

    # Where to append this run's transcript. The tray passes its own
    # %APPDATA%\MagicMouseTray path so it reads back the log of the run it
    # started, rather than guessing at the newest file in a directory that
    # belongs to whichever account approved the UAC prompt.
    [string]$LogPath,

    [switch]$DryRun
)

# Stop on any error — backup must succeed before registry write proceeds (QA-SS-1)
$ErrorActionPreference = 'Stop'

$Mac = ($Mac -replace '[^0-9A-Fa-f]', '').ToLowerInvariant()
if ($Mac.Length -ne 12) {
    throw "-Mac must be 12 hex digits (colons optional). Example: -Mac aabbccddeeff"
}

$backupDir = if ($env:APPDATA) {
    Join-Path $env:APPDATA 'MagicMouseTray'
} else {
    Join-Path $env:TEMP 'MagicMouseTray'
}
if (-not (Test-Path $backupDir)) {
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
}

$stamp = Get-Date -Format yyyyMMdd-HHmmss
if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $LogPath = Join-Path $backupDir "kbd-patch-$stamp.log"
}
$logDir = Split-Path -Parent $LogPath
if ($logDir -and -not (Test-Path $logDir)) {
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
}

$base = "HKLM:\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\$Mac"
$insertBytes = [byte[]](0x09, 0x20, 0xB1, 0x02)
$ridMarker   = [byte[]](0x85, 0x47)
$stockClose  = [byte[]](0x81, 0x02, 0xC0, 0xC0)

# How far past `85 47` a byte pair still belongs to the battery collection.
# Same window MagicMouseTray/SdpPatchReader.cs reads the result back through
# (RidWindowBytes), so what this script calls patched is what the tray calls
# Applied. Measured record: close 21 bytes past the RID.
$windowBytes = 96

# The console is the product here, but it is also hidden when the tray elevates
# this script, so every line goes to the log too. A log that cannot be written
# must never be the reason a keyboard stays unpatched.
function Write-Step {
    param([string]$Text)
    Write-Host $Text
    try {
        Add-Content -LiteralPath $LogPath -Value $Text
    } catch {
        Write-Host "(could not append to $LogPath : $($_.Exception.Message))"
    }
}

function Find-ByteSequence {
    param([byte[]]$Blob, [byte[]]$Needle, [int]$Start, [int]$End)
    $last = [Math]::Min($End, $Blob.Length - $Needle.Length)
    for ($i = [Math]::Max(0, $Start); $i -le $last; $i++) {
        $k = 0
        while ($k -lt $Needle.Length -and $Blob[$i + $k] -eq $Needle[$k]) { $k++ }
        if ($k -eq $Needle.Length) { return $i }
    }
    return -1
}

# What a human has to paste into an issue when the shape is new: the descriptor
# around RID 0x47, offsets included. 16 bytes of lead-in, 64 bytes after.
function Format-HexWindow {
    param([byte[]]$Blob, [int]$Start, [int]$Count)
    $from = [Math]::Max(0, $Start - 16)
    $to   = [Math]::Min($Blob.Length - 1, $Start + $Count)
    $rows = @()
    for ($i = $from; $i -le $to; $i += 16) {
        $end = [Math]::Min($i + 15, $to)
        $hex = ''
        for ($j = $i; $j -le $end; $j++) { $hex += ('{0:x2} ' -f $Blob[$j]) }
        $rows += ('  {0,5}: {1}' -f $i, $hex.TrimEnd())
    }
    return $rows
}

function Edit-Blob {
    param([byte[]]$Blob, [int]$Rid47, [int]$Col02Close, [string]$Source)
    $len = $Blob.Length
    Write-Step "[$Source] original length: $len bytes"

    $insertOffset = $Col02Close + 2     # between '81 02' and 'c0 c0'
    Write-Step "[$Source] RID 0x47 at $Rid47, COL02 close at $Col02Close, insert at $insertOffset"

    # Find the SDP length fields by parsing back from the descriptor.
    # Structure ahead of descriptor:
    #   <descriptor-string-tag> 25 <len8>  <descriptor bytes...>
    #   <inner-seq-tag>         35 <len8>  <descriptor-string-tag> ...
    #   <outer-seq-tag>         35 <len8>  <inner-seq-tag> ...
    #   <attr-id 0x0206>        09 02 06   <outer-seq-tag> ...
    #   <top-record-tag>        36 <hi> <lo>  ...

    # Find `09 02 06` (HIDDescriptorList attr id) before the descriptor.
    $attrIdOffset = Find-ByteSequence -Blob $Blob -Needle ([byte[]](0x09, 0x02, 0x06)) -Start 0 -End $Rid47
    if ($attrIdOffset -lt 0) { throw "HIDDescriptorList attr id 0x0206 (09 02 06) not found in $Source" }

    # Sequence of length fields immediately after `09 02 06`:
    #  byte[attrIdOffset+3] = 0x35 (outer seq)
    #  byte[attrIdOffset+4] = outer length (uint8)
    #  byte[attrIdOffset+5] = 0x35 (inner seq)
    #  byte[attrIdOffset+6] = inner length (uint8)
    #  byte[attrIdOffset+7] = 0x08 (UINT_8 marker for descriptor type)
    #  byte[attrIdOffset+8] = 0x22
    #  byte[attrIdOffset+9] = 0x25 (String 8-bit length marker)
    #  byte[attrIdOffset+10] = string length (uint8)
    $outerLenOffset  = $attrIdOffset + 4
    $innerLenOffset  = $attrIdOffset + 6
    $strLenOffset    = $attrIdOffset + 10
    if ($Blob[$attrIdOffset + 3]  -ne 0x35) { throw "Expected outer 0x35 at $($attrIdOffset + 3)" }
    if ($Blob[$attrIdOffset + 5]  -ne 0x35) { throw "Expected inner 0x35 at $($attrIdOffset + 5)" }
    if ($Blob[$attrIdOffset + 7]  -ne 0x08) { throw "Expected 0x08 at $($attrIdOffset + 7)" }
    if ($Blob[$attrIdOffset + 9]  -ne 0x25) { throw "Expected 0x25 at $($attrIdOffset + 9)" }

    Write-Step "[$Source] length offsets: outer=$outerLenOffset(=$($Blob[$outerLenOffset])) inner=$innerLenOffset(=$($Blob[$innerLenOffset])) str=$strLenOffset(=$($Blob[$strLenOffset]))"

    # Top record `36 hi lo`
    if ($Blob[0] -ne 0x36) { throw "Expected top record tag 0x36 at offset 0, got 0x$('{0:X2}' -f $Blob[0])" }
    $topLenHigh = 1
    $topLenLow  = 2
    $topLen = ($Blob[$topLenHigh] * 256) + $Blob[$topLenLow]
    Write-Step "[$Source] top record length 16-bit at offsets 1..2 = $topLen"

    # Build patched blob: insert 4 bytes at $insertOffset, +4 to each length field
    $newLen = $len + 4
    $new = New-Object byte[] $newLen
    [Array]::Copy($Blob, 0, $new, 0, $insertOffset)
    [Array]::Copy($insertBytes, 0, $new, $insertOffset, 4)
    [Array]::Copy($Blob, $insertOffset, $new, $insertOffset + 4, $len - $insertOffset)

    $new[$strLenOffset]    = [byte]($Blob[$strLenOffset]   + 4)
    $new[$innerLenOffset]  = [byte]($Blob[$innerLenOffset] + 4)
    $new[$outerLenOffset]  = [byte]($Blob[$outerLenOffset] + 4)
    $newTopLen = $topLen + 4
    $new[$topLenHigh] = [byte](($newTopLen -shr 8) -band 0xFF)
    $new[$topLenLow]  = [byte]( $newTopLen        -band 0xFF)

    Write-Step "[$Source] new lengths: outer=$($new[$outerLenOffset]) inner=$($new[$innerLenOffset]) str=$($new[$strLenOffset]) top=$newTopLen"

    return $new
}

Write-Step "kbd-patch-cachedservices.ps1 mac=$Mac dry_run=$($DryRun.IsPresent) started $(Get-Date -Format s)"

# candidates: records carrying RID 0x47. The 84/85-byte service records in the
# same key carry no battery report and vote on nothing.
$candidates = 0
$patched = 0
$already = 0
$blocked = 0

foreach ($subkey in 'CachedServices','DynamicCachedServices') {
    $path = "$base\$subkey"
    if (-not (Test-Path $path)) { Write-Step "$path not present, skipping"; continue }
    $key = Get-ItemProperty $path
    foreach ($prop in $key.PSObject.Properties) {
        if ($prop.Name -match '^PS') { continue }
        $val = $prop.Value
        if ($val -isnot [byte[]]) { continue }
        $source = "$subkey\$($prop.Name)"

        $rid47 = Find-ByteSequence -Blob $val -Needle $ridMarker -Start 0 -End $val.Length
        if ($rid47 -lt 0) { Write-Step "[$source] no RID 0x47 - skip"; continue }
        $candidates++

        $windowEnd = $rid47 + 2 + $windowBytes

        # Already patched is a SUCCESS, not a failure: the unlock this script
        # exists to install is in the record it was pointed at.
        $markerAt = Find-ByteSequence -Blob $val -Needle $insertBytes -Start ($rid47 + 2) -End $windowEnd
        if ($markerAt -ge 0) {
            $already++
            Write-Step "[$source] already patched - 09 20 b1 02 at offset $markerAt, left untouched"
            continue
        }

        $col02Close = Find-ByteSequence -Blob $val -Needle $stockClose -Start ($rid47 + 2) -End $windowEnd
        if ($col02Close -lt 0) {
            $blocked++
            Write-Step "[$source] RID 0x47 at $rid47, but neither 09 20 b1 02 nor the stock close 81 02 c0 c0 is within $windowBytes bytes of it - descriptor shape not recognised, nothing written."
            Write-Step "[$source] bytes around RID 0x47 (report these):"
            foreach ($row in (Format-HexWindow -Blob $val -Start $rid47 -Count 64)) { Write-Step $row }
            continue
        }

        # Backup first, and only for a record this run is about to change.
        $backup = Join-Path $backupDir "backup-$subkey-$($prop.Name)-$stamp.bin"
        [IO.File]::WriteAllBytes($backup, $val)
        Write-Step "[$source] backed up -> $backup"

        # One unpatchable record must not abort the records after it: both
        # cache subkeys hold the same descriptor, and a half-run that stopped
        # on the first is what left this keyboard in two different states.
        try {
            $new = Edit-Blob -Blob $val -Rid47 $rid47 -Col02Close $col02Close -Source $source
        } catch {
            $blocked++
            Write-Step "[$source] NOT patched: $($_.Exception.Message)"
            continue
        }

        if ($DryRun) {
            $patchedFile = Join-Path $backupDir "patched-$subkey-$($prop.Name).bin"
            [IO.File]::WriteAllBytes($patchedFile, $new)
            Write-Step "[DRY-RUN] patched bytes saved -> $patchedFile (registry NOT modified)"
            $patched++
        } else {
            Set-ItemProperty -Path $path -Name $prop.Name -Value $new -Type Binary
            Write-Step "[$source] WRITTEN to registry. New length: $($new.Length) bytes"
            $patched++
        }
    }
}

Write-Step ""
Write-Step "SUMMARY candidates=$candidates patched=$patched already=$already blocked=$blocked"
Write-Step "Log: $LogPath"

if (($patched + $already) -gt 0) {
    if ($patched -gt 0) {
        Write-Step "DONE. Toggle BT off/on for hidbth to re-read CachedServices."
    } else {
        Write-Step "NOTHING TO DO. The unlock is already in this keyboard's cached record. If the percent is still missing, toggle BT off/on for hidbth to re-read CachedServices."
    }
    exit 0
}

if ($candidates -eq 0) {
    Write-Step "NO RECORD. Nothing under $base carries Report ID 0x47. Check the -Mac value (Device Manager -> Bluetooth -> the keyboard -> Details -> Bluetooth device address), and that the keyboard is paired with this PC."
    exit 2
}

Write-Step "NOT PATCHED. $candidates record(s) carry Report ID 0x47 and none could be patched; the registry was not changed. Please report $LogPath at https://github.com/LesleyMurfin/magic-tray/issues so this descriptor shape can be supported."
exit 3
