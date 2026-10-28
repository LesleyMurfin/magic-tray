#Requires -RunAsAdministrator
# Patch CachedServices SDP record to expose RID 0x47 as Feature on COL02.
#
# Inserts `09 20 B1 02` at the COL02 close inside the HID Report Descriptor
# stored in the BTHPORT cache, and increments 4 SDP length fields.
#
# Run elevated. Modifies HKLM. Backs up original blobs first.
# Requires -Mac (12 hex digits). No default MAC. The log and the backups share
# one directory: -LogPath's own, or %APPDATA%\MagicMouseTray by default, or
# %TEMP%\MagicMouseTray if APPDATA is unset.
#
# A backup is named backup-<mac>-<subkey>-<value>-<stamp>.bin. The address is
# part of the name because every keyboard ever patched on this PC drops its
# backups into that one flat directory, and a value name like 00010000 repeats
# across devices, so without the address -Restore could not tell one paired
# keyboard's originals from another's.
#
# RUNNING THIS TWICE IS SAFE AND IS NOT AN ERROR (issue #162). The insert is
# idempotent: a record that already carries `09 20 B1 02` beside `85 47` is
# reported as already patched and left exactly as it is.
#
# Every record in both cache subkeys is decided on its own, and every `85 47`
# site inside a record votes, with the precedence
# MagicMouseTray/SdpPatchReader.cs Classify() uses: a marker in any site's
# window outranks a stock close in any other site's. A record that cannot be
# read, backed up, understood or written is counted as blocked, its bytes are
# dumped to the log, and the loop carries on to the next record - that is the
# whole of this script's per-record isolation.
#
# -DryRun computes the patched bytes, saves them beside the backup as
# patched-<mac>-<subkey>-<value>-<stamp>.bin and writes NOTHING to the
# registry. It is counted separately and never reports the unlock as installed.
#
# -Restore is the undo, and it never patches: for each record it reads the
# newest backup-<mac>-...bin in that same directory - the newest original is
# the one that matches the current pairing - and writes it back over the live
# bytes, having first saved those to
# prerestore-<mac>-<subkey>-<value>-<stamp>.bin. A record whose registry value
# has gone is counted as blocked rather than restored: Windows rebuilds these
# records when a keyboard is paired again, and writing a stale blob into a
# rebuilt key is exactly the damage this script must not do. A backup that
# itself carries `09 20 B1 02` beside `85 47` is refused for the same reason -
# it is a copy of a patched record, not an original. -Restore -DryRun runs
# every one of those checks and writes nothing at all, not even the prerestore
# copy.
#
# PowerShell rejects a malformed command line at parameter binding, before the
# first line here runs, so that run leaves no log; every run that gets this far
# writes one (-LogPath, default <log dir>\kbd-patch-<stamp>.log), and the
# caller that elevated this script can read back what happened in a window the
# user never got to see.
#
# Exit codes - the tray turns these into what it tells the user:
#   0  every candidate record in the cache carries the unlock - inserted by
#      this run, or already there - and nothing was blocked. Cycling Bluetooth
#      is what makes hidbth re-read it. A -DryRun that would have patched also
#      exits 0 and says in so many words that it wrote nothing.
#   1  the run stopped before it could report: -Mac was not 12 hex digits
#      (nothing was read or written), or something failed outside the
#      per-record isolation above, in which case records before it may already
#      have been rewritten. The log holds it.
#   2  no cached record for this MAC carries Report ID 0x47, and nothing was
#      blocked: wrong -Mac, or this keyboard has never been paired on this PC.
#   3  nothing could be unlocked: every candidate record was blocked, or no
#      record could be examined at all (an unreadable cache subkey). The log
#      says which, per record.
#   4  PARTIAL: at least one record carries the unlock - inserted by this run,
#      already present, or, under -DryRun, computed and saved beside the log -
#      AND at least one other record, or a whole cache subkey, was blocked.
#      Outside -DryRun the unlocked records are real and need a Bluetooth
#      toggle; the blocked ones are untouched and the log holds their bytes. A
#      -DryRun that reaches this wrote nothing.
#
# Exit codes for -Restore, the same four meanings read as an undo:
#   0  every record a backup names is stock again - put back by this run, or
#      already stock - and nothing was blocked. Cycling Bluetooth is what makes
#      hidbth re-read it. A -DryRun that would have restored also exits 0 and
#      says in so many words that it wrote nothing.
#   1  the run stopped before it could report: -Mac was not 12 hex digits, or
#      something failed outside the per-record isolation above. The log holds
#      it.
#   2  there is no usable backup for this MAC in the backup directory, so there
#      is nothing to undo: nothing was read from or written to the registry.
#      The log names the directory it searched.
#   3  nothing could be restored: every backup this run selected was blocked.
#      The log says which, and why, per record.
#   4  PARTIAL: at least one record is stock again AND at least one other was
#      blocked. Outside -DryRun the restored records are real and need a
#      Bluetooth toggle; the blocked ones still carry the unlock and are
#      untouched. A -DryRun that reaches this wrote nothing.

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

    [switch]$DryRun,

    # The undo. With -Restore this run only puts originals back and never
    # patches, so the two jobs can never be asked for in one command line.
    [switch]$Restore
)

# Stop on any error — backup must succeed before registry write proceeds (QA-SS-1)
$ErrorActionPreference = 'Stop'

# The log path comes first, before anything can fail, and the backups go beside
# it: one directory holds this run's transcript, the backup-*.bin originals,
# any -DryRun patched-*.bin and any -Restore prerestore-*.bin. Nothing here
# depends on -Mac, and the run most worth reading is the one that rejected its
# own arguments.
# Unique per run, not per second: two runs started inside the same second must
# not name one backup file, or the second would overwrite the originals the
# first saved. A PID cannot be shared by two live processes.
$stamp = '{0}-{1}' -f (Get-Date -Format yyyyMMdd-HHmmss), $PID
if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $defaultDir = if ($env:APPDATA) {
        Join-Path $env:APPDATA 'MagicMouseTray'
    } else {
        Join-Path $env:TEMP 'MagicMouseTray'
    }
    # A restore's transcript must not read as a patch's when someone opens the
    # directory later, so the default name says which job it was.
    $logName = if ($Restore) { "kbd-restore-$stamp.log" } else { "kbd-patch-$stamp.log" }
    $LogPath = Join-Path $defaultDir $logName
}
$LogPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($LogPath)
$backupDir = Split-Path -Parent $LogPath

# A directory that cannot be created costs this run its log, not its purpose.
$script:logFailure = $null
try {
    if (-not (Test-Path $backupDir)) {
        New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
    }
} catch {
    $script:logFailure = $_.Exception.Message
}

# The console is the product here, but it is also hidden when the tray elevates
# this script, so every line goes to the log too. A log that cannot be written
# must never be the reason a keyboard stays unpatched: the first failure is
# remembered, reported once at the end, and not retried per line.
#
# CWE-532: four messages below tell the user to paste this log into a public
# issue tracker, so the two identifiers it carries - the keyboard's address and
# the Windows account name in every path beside the log - are redacted here, at
# the one sink every line passes through. The rules and the `<mac>`/`<user>`
# tokens are MagicMouseTray/Logger.cs's, so the same address redacted at either
# sink is the same string; only the shapes this script can write are ported,
# and each is anchored on a delimiter an address carries but a hex dump does
# not. The backup file names this script writes carry the address themselves,
# so the third rule is anchored on those three prefixes - a shape Logger.cs
# never emits and so does not own. The console copy is the operator's own
# screen and keeps the address, so that "check the -Mac value" can be acted on
# and the backup files can be found by name.
function Write-Step {
    param([string]$Text)
    Write-Host $Text
    if ($script:logFailure) { return }
    $line = $Text -replace '\b([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b', '<mac>'
    $line = $line -replace '(?<=\bmac=|\\Devices\\)[0-9A-Fa-f]{12}', '<mac>'
    $line = $line -replace '(?<=\b(?:backup|patched|prerestore)-)[0-9A-Fa-f]{12}(?=-)', '<mac>'
    $line = $line -replace '(?<=\\Users\\)[^\\\r\n]+?(?=\\|\r|\n|$)', '<user>'
    try {
        Add-Content -LiteralPath $LogPath -Value $line
    } catch {
        $script:logFailure = $_.Exception.Message
    }
}

# How a message names the log. A path this run could not write is named as
# such, with the reason, so no line ever points at a file that is not there.
function Get-LogRef {
    if ($script:logFailure) { "NOT WRITTEN ($LogPath - $script:logFailure)" } else { $LogPath }
}

# A registry value name is not a file name: it may legally hold '\', '/' or
# ':', so interpolating one into a backup path can steer the write out of
# $backupDir entirely. Everything outside the safe set becomes '_'; the log
# line beside it still names the record by its real name.
function ConvertTo-FileNamePart {
    param([string]$Name)
    $Name -replace '[^0-9A-Za-z._-]', '_'
}

# -Mac is validated only now, with a log to fail into. The throw ends the run
# with exit code 1 and the registry is never opened, which is what the exit
# code table above promises.
$rawMac = $Mac
$Mac = ($Mac -replace '[^0-9A-Fa-f]', '').ToLowerInvariant()
if ($Mac.Length -ne 12) {
    $macError = "-Mac must be 12 hex digits (colons optional). Example: -Mac aabbccddeeff"
    Write-Step "kbd-patch-cachedservices.ps1 mac=$rawMac dry_run=$($DryRun.IsPresent) restore=$($Restore.IsPresent) started $(Get-Date -Format s)"
    Write-Step "BAD ARGUMENT. $macError Nothing was read from or written to the registry."
    Write-Step "Log: $(Get-LogRef)"
    throw $macError
}

$base = "HKLM:\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\$Mac"
$insertBytes = [byte[]](0x09, 0x20, 0xB1, 0x02)
$ridMarker   = [byte[]](0x85, 0x47)
$stockClose  = [byte[]](0x81, 0x02, 0xC0, 0xC0)

# How far past `85 47` a byte pair still belongs to the battery collection. The
# window is 96 bytes; Find-ByteSequence takes -End as the last candidate START
# index, so a close may start as far out as rid+2+96, and the insert lands at
# close+2, so the marker probe searches two bytes further.
# MagicMouseTray/SdpPatchReader.cs mirrors all three (RidWindowBytes).
# Measured record: close 21 bytes past the RID.
$windowBytes = 96

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
# around RID 0x47, offsets included. 16 bytes of lead-in, -Count bytes after.
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

# Every path that counts a record as blocked ends here, so "the bytes around
# the RID are in the log" is true of all of them and not just the one that
# could not find a close.
function Write-RecordHexDump {
    param([byte[]]$Blob, [int]$Rid47, [string]$Source)
    Write-Step "[$Source] bytes around RID 0x47 (report these):"
    # The probes reach a match starting at rid+2+$windowBytes+2, and a match is
    # 4 bytes long, so nothing was found anywhere up to rid+$windowBytes+8.
    foreach ($row in (Format-HexWindow -Blob $Blob -Start $Rid47 -Count ($windowBytes + 8))) { Write-Step $row }
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
    # The three 8-bit fields above throw on overflow and isolate the record.
    # This one is masked into two bytes, so it has to be checked to fail the
    # same way instead of wrapping into a shorter record than it describes.
    $newTopLen = $topLen + 4
    if ($newTopLen -gt 0xFFFF) { throw "Top record length $newTopLen does not fit the 16-bit field in $Source" }
    $new[$topLenHigh] = [byte]($newTopLen -shr 8)
    $new[$topLenLow]  = [byte]($newTopLen -band 0xFF)

    Write-Step "[$Source] new lengths: outer=$($new[$outerLenOffset]) inner=$($new[$innerLenOffset]) str=$($new[$strLenOffset]) top=$newTopLen"

    return $new
}

# Does this blob already carry the unlock beside an `85 47` site? The patch
# loop below asks the same question of a record's live bytes; -Restore asks it
# of a file, because a saved blob that carries the insert is a copy of a
# patched record and not the original it claims to be.
function Test-BlobUnlocked {
    param([byte[]]$Blob)
    for ($site = 0; ($site + 1) -lt $Blob.Length; $site++) {
        if ($Blob[$site] -ne $ridMarker[0] -or $Blob[$site + 1] -ne $ridMarker[1]) { continue }
        $windowEnd = $site + 2 + $windowBytes
        if ((Find-ByteSequence -Blob $Blob -Needle $insertBytes -Start ($site + 2) -End ($windowEnd + 2)) -ge 0) {
            return $true
        }
    }
    return $false
}

# A record whose live bytes already equal its backup is stock already, and the
# quietest restore is the one that writes nothing to it.
function Test-BytesEqual {
    param([byte[]]$Left, [byte[]]$Right)
    if ($Left.Length -ne $Right.Length) { return $false }
    for ($i = 0; $i -lt $Left.Length; $i++) {
        if ($Left[$i] -ne $Right[$i]) { return $false }
    }
    return $true
}

# Which saved original belongs to which record.
#
# Every run drops its backups into one flat directory, so the file name is the
# only index there is: backup-<mac>-<subkey>-<namePart>-<stamp>.bin. The subkey
# is one of two literals and anchors the parse from the left, the stamp is
# yyyyMMdd-HHmmss-<pid> and anchors it from the right, and between them sits a
# name part that may itself contain '-' - which is why neither end is found by
# counting separators.
#
# One record collects a backup per patch run, and the newest of them is the one
# that matches the current pairing: Windows rebuilds these records when the
# keyboard is paired again, so an older original describes a record that is no
# longer there.
function Select-NewestBackup {
    param([string]$Directory)
    $pattern = '^backup-' + [regex]::Escape($Mac) +
        '-(?<subkey>CachedServices|DynamicCachedServices)-(?<name>.+)-(?<stamp>\d{8}-\d{6}-\d+)\.bin$'
    try {
        $files = @(Get-ChildItem -LiteralPath $Directory -Filter "backup-$Mac-*.bin" -File)
    } catch {
        Write-Step "The backup directory $Directory could not be listed: $($_.Exception.Message)"
        return @()
    }

    $found = @()
    foreach ($file in $files) {
        $parsed = [regex]::Match($file.Name, $pattern)
        if (-not $parsed.Success) { continue }
        $found += [pscustomobject]@{
            Subkey           = $parsed.Groups['subkey'].Value
            NamePart         = $parsed.Groups['name'].Value
            Stamp            = $parsed.Groups['stamp'].Value
            FileName         = $file.Name
            FullName         = $file.FullName
            LastWriteTimeUtc = $file.LastWriteTimeUtc
        }
    }

    # The stamp is the run that saved the file and sorts as text because it is
    # written big-endian; the file's own timestamp breaks the tie between two
    # runs of the same second, whose only difference is a process id.
    $newestFirst = @(
        @{ Expression = 'Stamp'; Descending = $true },
        @{ Expression = 'LastWriteTimeUtc'; Descending = $true }
    )
    $newest = @()
    foreach ($group in ($found | Group-Object -Property Subkey, NamePart)) {
        $newest += @($group.Group | Sort-Object -Property $newestFirst)[0]
    }
    # Caller wraps this in @(), so the array is returned as it is: a unary
    # comma around an empty result is what turns "no backup at all" into one
    # nameless record.
    return ($newest | Sort-Object -Property Subkey, NamePart)
}

# The undo, start to finish. It shares this script's header, log, -Mac
# validation and summary shape with the patch below, and it always ends in its
# own exit code: a -Restore run must never fall through into the patch loop.
function Invoke-SdpRestore {
    $restored = 0
    $alreadyStock = 0
    $blocked = 0
    # -DryRun's own tally, kept apart for the reason $wouldPatch is: a run that
    # writes nothing must never be able to report the cache as restored.
    $wouldRestore = 0

    $backups = @(Select-NewestBackup -Directory $backupDir)
    if ($backups.Count -eq 0) {
        Write-Step "NOTHING TO UNDO. No backup for this keyboard was found in $backupDir, so there is nothing to put back; nothing was read from or written to the registry. The unlock saves every original into that directory before it writes, so if it ran with a different -LogPath, point -LogPath at that directory and run -Restore again."
        Write-Step "Log: $(Get-LogRef)"
        exit 2
    }
    Write-Step "$($backups.Count) record(s) have a backup in $backupDir; for each one the newest backup is the original that matches the current pairing."

    foreach ($backup in $backups) {
        $path = "$base\$($backup.Subkey)"
        $source = "$($backup.Subkey)\$($backup.NamePart)"
        Write-Step "[$source] newest backup is $($backup.FileName)"

        # The record has to still be there, and still be binary. A value that
        # has gone is NOT one to recreate from an old blob: Windows rebuilds
        # these records when the keyboard is paired again, and a stale original
        # written into a rebuilt key is exactly the damage this script exists
        # to avoid. One such record never stops the ones after it.
        try {
            if (-not (Test-Path $path)) { throw "$path is not present any more" }
            $key = Get-ItemProperty $path
            $live = $null
            $valueName = $null
            foreach ($prop in $key.PSObject.Properties) {
                if ($prop.Name -match '^PS') { continue }
                if ((ConvertTo-FileNamePart $prop.Name) -ne $backup.NamePart) { continue }
                if ($null -ne $valueName) { throw "two values under $path have the same backup name" }
                $valueName = $prop.Name
                $live = $prop.Value
            }
            if ($null -eq $valueName) { throw "no value named $($backup.NamePart) is under $path any more" }
            if ($live -isnot [byte[]]) { throw "the value named $($backup.NamePart) under $path is no longer binary" }
        } catch {
            $blocked++
            Write-Step "[$source] BLOCKED, nothing was written: $($_.Exception.Message). Windows rebuilds these records when a keyboard is paired again, so this run cannot show that the backup belongs to what is under that name now, and writing the saved bytes into it could corrupt the cache."
            continue
        }

        try {
            $saved = [IO.File]::ReadAllBytes($backup.FullName)
        } catch {
            $blocked++
            Write-Step "[$source] BLOCKED, nothing was written: $($backup.FileName) could not be read: $($_.Exception.Message)"
            continue
        }

        if (Test-BlobUnlocked -Blob $saved) {
            $blocked++
            Write-Step "[$source] BLOCKED, nothing was written: $($backup.FileName) itself carries 09 20 b1 02 within $windowBytes bytes of an 85 47 site, so it holds a patched record and not an original. Putting it back would leave this record exactly as it is."
            continue
        }

        if (Test-BytesEqual -Left $live -Right $saved) {
            $alreadyStock++
            Write-Step "[$source] already stock - the record is byte for byte its backup, so nothing was written."
            continue
        }

        if ($DryRun) {
            $wouldRestore++
            Write-Step "[DRY-RUN] [$source] would be restored from $($backup.FileName): $($live.Length) bytes now, $($saved.Length) in the backup. Nothing was written, not even a prerestore copy."
            continue
        }

        # The bytes being replaced are saved first, for the reason the patch
        # saves the original first: a record this run cannot copy is a record
        # it must not overwrite.
        $preRestore = Join-Path $backupDir "prerestore-$Mac-$($backup.Subkey)-$($backup.NamePart)-$stamp.bin"
        try {
            [IO.File]::WriteAllBytes($preRestore, $live)
        } catch {
            $blocked++
            Write-Step "[$source] BLOCKED: the bytes it holds now could not be saved to $preRestore ($($_.Exception.Message)), so this record was left exactly as it was."
            continue
        }
        Write-Step "[$source] current bytes saved -> $preRestore"

        try {
            Set-ItemProperty -Path $path -Name $valueName -Value $saved -Type Binary
        } catch {
            $blocked++
            Write-Step "[$source] BLOCKED: the registry write failed: $($_.Exception.Message) - this record still carries the unlock, and $preRestore holds the bytes it has now."
            continue
        }
        $restored++
        Write-Step "[$source] RESTORED from $($backup.FileName). Length back to $($saved.Length) bytes"
    }

    Write-Step ""
    Write-Step "SUMMARY backups=$($backups.Count) restored=$restored already_stock=$alreadyStock blocked=$blocked would_restore=$wouldRestore"
    $logRef = Get-LogRef
    Write-Step "Log: $logRef"

    # One blocked record is not a detail of a successful undo: it is the half of
    # the cache that still carries the unlock, and the caller has to be told
    # both halves.
    $progress = $restored + $alreadyStock + $wouldRestore

    if ($blocked -gt 0 -and $progress -gt 0) {
        if ($DryRun) {
            Write-Step "DRY-RUN PARTIAL. $wouldRestore record(s) would be put back and $alreadyStock already hold their original bytes, but $blocked could not be restored. Nothing was written by this run. Please report $logRef at https://github.com/LesleyMurfin/magic-tray/issues so the remaining case(s) can be handled."
        } else {
            Write-Step "PARTIAL. $restored record(s) were put back and $alreadyStock already held their original bytes, but $blocked could not be restored and were left exactly as they were - the log says why, record by record. The records that are stock again are real: toggle BT off/on for hidbth to re-read CachedServices. Please report $logRef at https://github.com/LesleyMurfin/magic-tray/issues so the remaining case(s) can be handled."
        }
        exit 4
    }

    if ($progress -gt 0) {
        if ($wouldRestore -gt 0) {
            Write-Step "DRY-RUN COMPLETE. $wouldRestore record(s) would be put back from the backups named above and $alreadyStock already hold their original bytes; the registry was NOT modified by this run and no prerestore copy was written. Re-run without -DryRun to undo the unlock."
        } elseif ($restored -gt 0) {
            Write-Step "DONE. The cached record is its original self again, so the battery percent will stop being reported for this keyboard. Toggle BT off/on for hidbth to re-read CachedServices."
        } else {
            Write-Step "NOTHING TO UNDO. Every record with a backup already holds its original bytes, so nothing was written and the unlock is not in this keyboard's cache."
        }
        exit 0
    }

    Write-Step "NOT RESTORED. $blocked backup(s) name a record this run could not put back, and nothing was written to the registry. Each record's line above says why. Please report $logRef at https://github.com/LesleyMurfin/magic-tray/issues so the remaining case(s) can be handled."
    exit 3
}

Write-Step "kbd-patch-cachedservices.ps1 mac=$Mac dry_run=$($DryRun.IsPresent) restore=$($Restore.IsPresent) started $(Get-Date -Format s)"

# -Restore is the whole run when it is asked for: it ends in its own summary
# and its own exit code, and the patch loop below is never reached.
if ($Restore) { Invoke-SdpRestore }

# candidates: records carrying RID 0x47. The 84/85-byte service records in the
# same key carry no battery report and vote on nothing.
# wouldPatch is -DryRun's own tally: a dry run writes nothing, so it must never
# be able to raise $patched and claim the cache carries the unlock.
$candidates = 0
$patched = 0
$already = 0
$blocked = 0
$wouldPatch = 0

foreach ($subkey in 'CachedServices','DynamicCachedServices') {
    $path = "$base\$subkey"
    if (-not (Test-Path $path)) { Write-Step "$path not present, skipping"; continue }

    # A key that cannot be read is not evidence that there is nothing in it, so
    # it is blocked rather than silent: this run can no longer exit 0, and it
    # can no longer call this MAC "no record".
    try {
        $key = Get-ItemProperty $path
    } catch {
        $blocked++
        Write-Step "[$subkey] NOT read: $($_.Exception.Message) - no record in this subkey was examined, and none was changed."
        continue
    }

    foreach ($prop in $key.PSObject.Properties) {
        if ($prop.Name -match '^PS') { continue }
        $val = $prop.Value
        if ($val -isnot [byte[]]) { continue }
        $source = "$subkey\$($prop.Name)"

        $rid47 = -1
        $markerAt = -1
        $col02Close = -1
        $closeRid = -1
        for ($site = 0; ($site + 1) -lt $val.Length; $site++) {
            if ($val[$site] -ne $ridMarker[0] -or $val[$site + 1] -ne $ridMarker[1]) { continue }
            if ($rid47 -lt 0) { $rid47 = $site }
            $windowEnd = $site + 2 + $windowBytes
            $markerAt = Find-ByteSequence -Blob $val -Needle $insertBytes -Start ($site + 2) -End ($windowEnd + 2)
            if ($markerAt -ge 0) { break }

            if ($col02Close -lt 0) {
                $col02Close = Find-ByteSequence -Blob $val -Needle $stockClose -Start ($site + 2) -End $windowEnd
                if ($col02Close -ge 0) { $closeRid = $site }
            }
        }

        if ($rid47 -lt 0) { Write-Step "[$source] no RID 0x47 - skip"; continue }
        $candidates++

        # Already patched is a SUCCESS, not a failure: the unlock this script
        # exists to install is in the record it was pointed at.
        if ($markerAt -ge 0) {
            $already++
            Write-Step "[$source] already patched - 09 20 b1 02 at offset $markerAt, left untouched"
            continue
        }

        if ($col02Close -lt 0) {
            $blocked++
            Write-Step "[$source] RID 0x47 at $rid47, but no 85 47 site has either 09 20 b1 02 or the stock close 81 02 c0 c0 within $windowBytes bytes of it - descriptor shape not recognised, nothing written."
            Write-RecordHexDump -Blob $val -Rid47 $rid47 -Source $source
            continue
        }

        # Backup first, and only for a record this run is about to change. A
        # record whose original cannot be saved is not one this run will touch.
        # The address is part of the name because -Restore reads these files
        # back out of a directory that holds every patched keyboard's backups,
        # and a value name like 00010000 is the same on all of them.
        $namePart = ConvertTo-FileNamePart $prop.Name
        $backup = Join-Path $backupDir "backup-$Mac-$subkey-$namePart-$stamp.bin"
        try {
            [IO.File]::WriteAllBytes($backup, $val)
        } catch {
            $blocked++
            Write-Step "[$source] NOT patched: backup to $backup failed: $($_.Exception.Message) - this record was left exactly as it was."
            Write-RecordHexDump -Blob $val -Rid47 $closeRid -Source $source
            continue
        }
        Write-Step "[$source] backed up -> $backup"

        # One unpatchable record must not abort the records after it: both
        # cache subkeys hold the same descriptor, and a half-run that stopped
        # on the first is what left this keyboard in two different states.
        try {
            $new = Edit-Blob -Blob $val -Rid47 $closeRid -Col02Close $col02Close -Source $source
        } catch {
            $blocked++
            Write-Step "[$source] NOT patched: $($_.Exception.Message)"
            Write-RecordHexDump -Blob $val -Rid47 $closeRid -Source $source
            Write-Step "[$source] the registry was not changed for this record; $backup holds it as it was."
            continue
        }

        if ($DryRun) {
            $patchedFile = Join-Path $backupDir "patched-$Mac-$subkey-$namePart-$stamp.bin"
            try {
                [IO.File]::WriteAllBytes($patchedFile, $new)
            } catch {
                $blocked++
                Write-Step "[$source] NOT saved: $patchedFile could not be written: $($_.Exception.Message) - the registry was not modified either, this is a dry run."
                Write-RecordHexDump -Blob $val -Rid47 $closeRid -Source $source
                continue
            }
            Write-Step "[DRY-RUN] patched bytes saved -> $patchedFile (registry NOT modified)"
            $wouldPatch++
        } else {
            try {
                Set-ItemProperty -Path $path -Name $prop.Name -Value $new -Type Binary
            } catch {
                $blocked++
                Write-Step "[$source] NOT patched: the registry write failed: $($_.Exception.Message) - this record still holds the bytes in $backup."
                Write-RecordHexDump -Blob $val -Rid47 $closeRid -Source $source
                continue
            }
            Write-Step "[$source] WRITTEN to registry. New length: $($new.Length) bytes"
            $patched++
        }
    }
}

Write-Step ""
Write-Step "SUMMARY candidates=$candidates patched=$patched already=$already blocked=$blocked would_patch=$wouldPatch"
$logRef = Get-LogRef
Write-Step "Log: $logRef"

# One blocked record is not a detail of a successful run: it is the half of the
# cache this script could not fix, and the caller has to be told both halves.
$progress = $patched + $already + $wouldPatch

# $blocked counts records and whole unreadable subkeys alike, and a subkey
# holds an unknown number of records, so the messages below say how many things
# were blocked without claiming they were all records.
if ($blocked -gt 0 -and $progress -gt 0) {
    if ($DryRun) {
        Write-Step "DRY-RUN PARTIAL. $wouldPatch record(s) would be patched and $already already carry the unlock, but $blocked could not be patched. Nothing was written to the registry by this run. Please report $logRef at https://github.com/LesleyMurfin/magic-tray/issues so the remaining descriptor shape(s) can be supported."
    } else {
        Write-Step "PARTIAL. $patched record(s) patched and $already already carried the unlock, but $blocked could not be patched. The records that now carry the unlock are real - toggle BT off/on for hidbth to re-read CachedServices - and the blocked ones were left exactly as they were. Please report $logRef at https://github.com/LesleyMurfin/magic-tray/issues so the remaining descriptor shape(s) can be supported."
    }
    exit 4
}

if ($progress -gt 0) {
    if ($wouldPatch -gt 0) {
        Write-Step "DRY-RUN COMPLETE. $wouldPatch record(s) would be patched and $already already carry the unlock; the registry was NOT modified by this run. The bytes that would be written are in $backupDir. Re-run without -DryRun to apply them."
    } elseif ($patched -gt 0) {
        Write-Step "DONE. Toggle BT off/on for hidbth to re-read CachedServices."
    } else {
        Write-Step "NOTHING TO DO. The unlock is already in this keyboard's cached record. If the percent is still missing, toggle BT off/on for hidbth to re-read CachedServices."
    }
    exit 0
}

if ($candidates -eq 0 -and $blocked -eq 0) {
    Write-Step "NO RECORD. Nothing under $base carries Report ID 0x47. Check the -Mac value (Device Manager -> Bluetooth -> the keyboard -> Details -> Bluetooth device address), and that the keyboard is paired with this PC."
    exit 2
}

if ($candidates -gt 0) {
    Write-Step "NOT PATCHED. $candidates record(s) carry Report ID 0x47 and none could be patched; the registry was not changed. Please report $logRef at https://github.com/LesleyMurfin/magic-tray/issues so this descriptor shape can be supported."
} else {
    Write-Step "NOT PATCHED. $blocked cache subkey(s) could not be read, so no record was examined and the registry was not changed. Please report $logRef at https://github.com/LesleyMurfin/magic-tray/issues."
}
exit 3
