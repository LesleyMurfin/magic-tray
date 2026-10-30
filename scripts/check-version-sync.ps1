#Requires -Version 7

<#
.SYNOPSIS
    Checks that every version string Magic Tray publishes agrees with the
    version that was actually released.

.DESCRIPTION
    Magic Tray carries its version number in five independent places, and they
    do not all mean the same thing:

      1. MagicMouseTray/MagicMouseTray.csproj  - the version being DEVELOPED.
      2. packaging/winget/manifests/.../<ver>/ - the version that was RELEASED.
      3. docs/index.html                       - what magictray.app ADVERTISES.
      4. scripts/package-release.ps1           - the asset NAME pattern.
      5. docs/install.txt                      - what magictray.app TELLS AN
                                                 AI AGENT to install.

    The rule this script encodes is therefore not "all five must be equal":

      * The winget manifests and the public pages - docs/index.html and the
        agent install guide docs/install.txt - describe a build that people can
        actually download, so they must all name the LATEST RELEASED version,
        and the installer URL must point at that release's tag and at the exact
        asset file name scripts/package-release.ps1 produces.
      * The csproj may legitimately run AHEAD of the released version - that is
        the normal "next version in development" state, and it is reported as a
        ::notice, not an error. It may never run BEHIND it, because that means
        a release was cut from a tree whose version was never bumped.

    The drift this prevents, in the order it has historically happened:

      * A release is tagged and published, but docs/index.html still offers the
        previous version, so the site's "Download vX.Y.Z" text, its brand
        <small> badge and its softwareVersion JSON-LD disagree with the ZIP the
        download link actually serves.
      * The same release leaves docs/install.txt naming the previous tag as the
        current one, so an agent a user pointed at
        https://magictray.app/install.txt reads a stale example.
      * A winget manifest folder is copied for a new version but one of the
        three YAML files keeps the old PackageVersion, or the InstallerUrl still
        points at the previous tag - which winget accepts and then installs the
        wrong build from.
      * The asset file name in the manifest drifts away from the name
        package-release.ps1 emits, so the submission 404s on download.
      * AssemblyVersion or FileVersion is bumped without Version (or the other
        way round), so the shipped exe reports a version nothing else knows.

    Every failure is printed as a GitHub annotation
    (::error file=<path>,line=<n>::<message>) and the script exits non-zero.
    It reads files only: no network access and no build.

.PARAMETER RepoRoot
    Repository root to check. Defaults to the parent of the folder holding this
    script, so the script works from any working directory.

.EXAMPLE
    pwsh -File scripts/check-version-sync.ps1

.EXAMPLE
    pwsh -File scripts/check-version-sync.ps1 -RepoRoot /path/to/magic-tray
#>

[CmdletBinding()]
param(
    [Parameter()]
    [string] $RepoRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path

$script:ProblemCount = 0

function Write-Problem {
    [CmdletBinding()]
    [OutputType([void])]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [int]    $Line,
        [Parameter(Mandatory)] [string] $Message
    )

    $script:ProblemCount++
    Write-Host ('::error file={0},line={1}::{2}' -f $Path, $Line, $Message)
}

function Write-Advice {
    [CmdletBinding()]
    [OutputType([void])]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [int]    $Line,
        [Parameter(Mandatory)] [string] $Message
    )

    Write-Host ('::notice file={0},line={1}::{2}' -f $Path, $Line, $Message)
}

function Get-RepoFile {
    <#
    .SYNOPSIS
        Reads a repo-relative file as an array of lines, or $null if absent.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)] [string] $RelativePath
    )

    $full = Join-Path -Path $RepoRoot -ChildPath $RelativePath
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        return $null
    }
    return [string[]]@(Get-Content -LiteralPath $full)
}

function Get-TaggedValueList {
    <#
    .SYNOPSIS
        Every single-line regex capture in a file, each with its line number.
    #>
    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [AllowEmptyString()] [string[]] $Content,
        [Parameter(Mandatory)] [string] $Pattern
    )

    $hits = [System.Collections.Generic.List[object]]::new()
    for ($i = 0; $i -lt $Content.Count; $i++) {
        foreach ($found in [regex]::Matches($Content[$i], $Pattern)) {
            $hits.Add([pscustomobject]@{
                    Value = $found.Groups[1].Value.Trim()
                    Line  = $i + 1
                })
        }
    }
    return $hits.ToArray()
}

function Get-TaggedValue {
    <#
    .SYNOPSIS
        First single-line regex capture in a file, with its 1-based line number,
        or $null when nothing matches.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [AllowEmptyString()] [string[]] $Content,
        [Parameter(Mandatory)] [string] $Pattern
    )

    $hits = @(Get-TaggedValueList -Content $Content -Pattern $Pattern)
    if ($hits.Count -eq 0) { return $null }
    return $hits[0]
}

function Get-YamlScalar {
    <#
    .SYNOPSIS
        Every "key: value" written on one line, unquoted, with its line number.
    .DESCRIPTION
        Returns an array, empty when the key is absent. Every occurrence, not
        just the first: the winget schema lets a key sit at the manifest root and
        again on each Installers entry, so checking only the first hit would let
        a second entry carry a stale tag or asset name unchallenged.
    #>
    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [AllowEmptyString()] [string[]] $Content,
        [Parameter(Mandatory)] [string] $Key
    )

    $pattern = '^\s*-?\s*{0}\s*:\s*(\S.*?)\s*$' -f [regex]::Escape($Key)
    return @(Get-TaggedValueList -Content $Content -Pattern $pattern | ForEach-Object {
            $text = $_.Value
            $quoted = [regex]::Match($text, '^(?:''(.*)''|"(.*)")$')
            if ($quoted.Success) {
                $text = if ($quoted.Groups[1].Success) { $quoted.Groups[1].Value } else { $quoted.Groups[2].Value }
            }
            [pscustomobject]@{ Value = $text; Line = $_.Line }
        })
}

function Get-ShortVersion {
    <#
    .SYNOPSIS
        Drops a trailing ".0" revision field, so 1.1.1.0 reads as 1.1.1.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [string] $Version
    )

    $trimmed = [regex]::Match($Version, '^(\d+\.\d+\.\d+)\.0$')
    if ($trimmed.Success) {
        return $trimmed.Groups[1].Value
    }
    return $Version
}

$csprojRel = 'MagicMouseTray/MagicMouseTray.csproj'
$packagerRel = 'scripts/package-release.ps1'
$docsRel = 'docs/index.html'
$installRel = 'docs/install.txt'
$manifestRootRel = 'packaging/winget/manifests/l/LesleyMurfin/MagicTray'

# ---------------------------------------------------------------------------
# 1. The three csproj version properties must agree with each other.
# ---------------------------------------------------------------------------

$csprojVersion = $null
$csprojVersionLine = 1
$csproj = Get-RepoFile -RelativePath $csprojRel
if ($null -eq $csproj) {
    Write-Problem -Path $csprojRel -Line 1 -Message 'Project file is missing; cannot check the developed version.'
}
else {
    $properties = @('Version', 'AssemblyVersion', 'FileVersion')
    $readValues = @{}
    foreach ($property in $properties) {
        $hit = Get-TaggedValue -Content $csproj -Pattern ('<{0}>\s*([^<]+?)\s*</{0}>' -f $property)
        if ($null -eq $hit) {
            Write-Problem -Path $csprojRel -Line 1 -Message ("No <{0}> property. All of {1} must be present and agree." -f $property, ($properties -join ', '))
        }
        else {
            $readValues[$property] = $hit
        }
    }

    if ($readValues.ContainsKey('Version')) {
        $csprojVersion = $readValues['Version'].Value
        $csprojVersionLine = $readValues['Version'].Line

        if ($csprojVersion -notmatch '^\d+\.\d+\.\d+$') {
            Write-Problem -Path $csprojRel -Line $csprojVersionLine -Message ("<Version> is '{0}'; expected MAJOR.MINOR.PATCH." -f $csprojVersion)
            $csprojVersion = $null
        }
        else {
            foreach ($property in @('AssemblyVersion', 'FileVersion')) {
                if (-not $readValues.ContainsKey($property)) { continue }
                $other = Get-ShortVersion -Version $readValues[$property].Value
                if ($other -ne $csprojVersion) {
                    Write-Problem -Path $csprojRel -Line $readValues[$property].Line -Message ("<{0}> is '{1}' but <Version> is '{2}'. They must describe the same version (a trailing '.0' revision is allowed)." -f $property, $readValues[$property].Value, $csprojVersion)
                }
            }
        }
    }
}

# ---------------------------------------------------------------------------
# 2. The released version is the winget manifest folder. Each folder's three
#    manifests must agree with the folder name they sit in; the released
#    version is the newest folder.
# ---------------------------------------------------------------------------

$manifestRoot = Join-Path -Path $RepoRoot -ChildPath $manifestRootRel
$versionFolders = @()
if (-not (Test-Path -LiteralPath $manifestRoot -PathType Container)) {
    Write-Problem -Path $manifestRootRel -Line 1 -Message 'No winget manifest folder; cannot determine the released version.'
}
else {
    # Unsorted on purpose: every folder is checked against its own name, and the
    # released version is picked below by [version] comparison, which a lexical
    # sort would get wrong anyway (1.1.10 sorts before 1.1.9).
    $versionFolders = @(Get-ChildItem -LiteralPath $manifestRoot -Directory)
    if ($versionFolders.Count -eq 0) {
        Write-Problem -Path $manifestRootRel -Line 1 -Message 'No version folder under the winget manifest root; cannot determine the released version.'
    }
}

# The folder name is the source of truth for the released version: it is the
# path microsoft/winget-pkgs indexes the package under. When more than one
# version folder is kept, the newest is the released one; every folder's own
# manifests are still checked against the folder they sit in, so a disagreeing
# set is reported rather than silently picked.
$releasedVersion = $null
foreach ($candidate in $versionFolders) {
    if ($candidate.Name -notmatch '^\d+\.\d+\.\d+$') { continue }
    if ($null -eq $releasedVersion -or [version]$candidate.Name -gt [version]$releasedVersion) {
        $releasedVersion = $candidate.Name
    }
}

# ---------------------------------------------------------------------------
# 3. Every manifest set must name its own folder version, and the installer
#    URL must point at that tag and at the asset package-release.ps1 emits.
# ---------------------------------------------------------------------------

# Derive the asset name from the packaging script instead of restating it here,
# so renaming the ZIP in one place cannot silently pass this check.
$assetTemplate = $null
$packager = Get-RepoFile -RelativePath $packagerRel
if ($null -eq $packager) {
    Write-Problem -Path $packagerRel -Line 1 -Message 'Packaging script is missing; cannot derive the release asset name.'
}
else {
    $assetHit = Get-TaggedValue -Content $packager -Pattern '^\s*\$zipName\s*=\s*"([^"]+)"'
    if ($null -eq $assetHit) {
        Write-Problem -Path $packagerRel -Line 1 -Message 'No `$zipName = "..."` assignment found; this script derives the expected release asset name from it.'
    }
    elseif ($assetHit.Value -notmatch '\$Tag') {
        Write-Problem -Path $packagerRel -Line $assetHit.Line -Message ("Asset name '{0}' does not interpolate `$Tag; this script cannot derive a per-release asset name from it." -f $assetHit.Value)
    }
    else {
        $assetTemplate = $assetHit.Value
    }
}

foreach ($folder in $versionFolders) {
    $folderVersion = $folder.Name
    $folderRel = '{0}/{1}' -f $manifestRootRel, $folderVersion
    $tag = 'v{0}' -f $folderVersion

    if ($folderVersion -notmatch '^\d+\.\d+\.\d+$') {
        Write-Problem -Path $folderRel -Line 1 -Message ("Manifest folder '{0}' is not a MAJOR.MINOR.PATCH version." -f $folderVersion)
        continue
    }

    $manifestFiles = @(
        'LesleyMurfin.MagicTray.yaml'
        'LesleyMurfin.MagicTray.installer.yaml'
        'LesleyMurfin.MagicTray.locale.en-US.yaml'
    )

    foreach ($manifestFile in $manifestFiles) {
        $manifestRel = '{0}/{1}' -f $folderRel, $manifestFile
        $manifest = Get-RepoFile -RelativePath $manifestRel
        if ($null -eq $manifest) {
            Write-Problem -Path $folderRel -Line 1 -Message ("Manifest set is incomplete: {0} is missing." -f $manifestFile)
            continue
        }

        $declared = @(Get-YamlScalar -Content $manifest -Key 'PackageVersion')
        if ($declared.Count -eq 0) {
            Write-Problem -Path $manifestRel -Line 1 -Message 'No PackageVersion key.'
        }
        foreach ($hit in $declared) {
            if ($hit.Value -ne $folderVersion) {
                Write-Problem -Path $manifestRel -Line $hit.Line -Message ("PackageVersion is '{0}' but the manifest folder is '{1}'. All three manifests must name the folder's version." -f $hit.Value, $folderVersion)
            }
        }

        if ($manifestFile -eq 'LesleyMurfin.MagicTray.installer.yaml') {
            # Every Installers entry, not just the first: an arm64 entry added
            # beside the x64 one must carry the same tag and asset name.
            $installerUrls = @(Get-YamlScalar -Content $manifest -Key 'InstallerUrl')
            if ($installerUrls.Count -eq 0) {
                Write-Problem -Path $manifestRel -Line 1 -Message 'No InstallerUrl key.'
            }
            foreach ($hit in $installerUrls) {
                if ($hit.Value -notmatch ('/releases/download/{0}/' -f [regex]::Escape($tag))) {
                    Write-Problem -Path $manifestRel -Line $hit.Line -Message ("InstallerUrl '{0}' does not point at the /releases/download/{1}/ assets of tag {1}." -f $hit.Value, $tag)
                }
                if ($null -ne $assetTemplate) {
                    $expectedAsset = $assetTemplate.Replace('$Tag', $tag)
                    $actualAsset = $hit.Value.Split('/')[-1]
                    if ($actualAsset -ne $expectedAsset) {
                        Write-Problem -Path $manifestRel -Line $hit.Line -Message ("InstallerUrl asset is '{0}' but {1} publishes '{2}'." -f $actualAsset, $packagerRel, $expectedAsset)
                    }
                }
            }
        }

        if ($manifestFile -eq 'LesleyMurfin.MagicTray.locale.en-US.yaml') {
            $notesUrls = @(Get-YamlScalar -Content $manifest -Key 'ReleaseNotesUrl')
            if ($notesUrls.Count -eq 0) {
                Write-Problem -Path $manifestRel -Line 1 -Message 'No ReleaseNotesUrl key.'
            }
            foreach ($hit in $notesUrls) {
                if ($hit.Value -notmatch ('/releases/tag/{0}$' -f [regex]::Escape($tag))) {
                    Write-Problem -Path $manifestRel -Line $hit.Line -Message ("ReleaseNotesUrl '{0}' does not point at the release notes of tag {1}." -f $hit.Value, $tag)
                }
            }
        }
    }
}

# ---------------------------------------------------------------------------
# 4. The public site must advertise the released version, everywhere it says
#    a version at all.
# ---------------------------------------------------------------------------

if ($null -ne $releasedVersion) {
    $docs = Get-RepoFile -RelativePath $docsRel
    if ($null -eq $docs) {
        Write-Problem -Path $docsRel -Line 1 -Message 'Home page is missing; cannot check the advertised version.'
    }
    else {
        $advertised = [System.Collections.Generic.List[object]]::new()

        $jsonLd = Get-TaggedValue -Content $docs -Pattern '"softwareVersion"\s*:\s*"([^"]+)"'
        if ($null -eq $jsonLd) {
            Write-Problem -Path $docsRel -Line 1 -Message 'No "softwareVersion" in the JSON-LD block. Search engines read it, so it must state the released version.'
        }
        else {
            $advertised.Add([pscustomobject]@{ What = 'softwareVersion JSON-LD value'; Hit = $jsonLd })
        }

        $brand = Get-TaggedValue -Content $docs -Pattern 'class="brand"[^>]*>.*?<small>\s*([^<]+?)\s*</small>'
        if ($null -eq $brand) {
            Write-Problem -Path $docsRel -Line 1 -Message 'No <small> version badge inside the brand link.'
        }
        else {
            $advertised.Add([pscustomobject]@{ What = 'brand link <small> badge'; Hit = $brand })
        }

        $downloads = @(Get-TaggedValueList -Content $docs -Pattern 'Download v(\d+\.\d+\.\d+)')
        if ($downloads.Count -eq 0) {
            Write-Problem -Path $docsRel -Line 1 -Message 'No "Download vX.Y.Z" link text found.'
        }
        foreach ($download in $downloads) {
            $advertised.Add([pscustomobject]@{ What = 'Download link text'; Hit = $download })
        }

        foreach ($entry in $advertised) {
            $stated = $entry.Hit.Value.TrimStart('v')
            if ($stated -ne $releasedVersion) {
                Write-Problem -Path $docsRel -Line $entry.Hit.Line -Message ("{0} says '{1}' but the released version is '{2}'. The site must advertise the version people can actually download." -f $entry.What, $entry.Hit.Value, $releasedVersion)
            }
        }
    }
}

# ---------------------------------------------------------------------------
# 5. The agent install guide must name the released version where it names one
#    at all - and only where it names one.
# ---------------------------------------------------------------------------

# docs/install.txt is published at https://magictray.app/install.txt and is read
# by an AI agent that a user has asked to install Magic Tray, so it rots at
# release time exactly the way docs/index.html does. It is in scope here for the
# same reason the site is: it is public metadata describing a build that people
# can actually download, and nothing else in CI notices when it names a tag that
# is no longer the newest one. A stale version matters more here than on the
# site, because the reader is a machine acting on the text rather than a person
# who can see the release page beside it.
#
# The check is deliberately narrower than the one on docs/index.html, because
# the file makes a narrower claim. Its STEP 1 sends the agent to
# api.github.com/repos/LesleyMurfin/magic-tray/releases/latest for tag_name and
# the assets array and then says, in as many words, "Do not assume those values;
# read them from the API response". The version it prints is therefore an
# "at the time of writing" illustration, not an instruction to install that
# version - so the one thing that can go stale is the illustration, and that is
# the one thing checked. No download URL, asset digest or step text is held to
# the released version here: the file does not assert any of them, and a check
# stricter than the file it guards would only teach people to edit the file to
# please the checker.
if ($null -ne $releasedVersion) {
    $install = Get-RepoFile -RelativePath $installRel
    if ($null -eq $install) {
        Write-Problem -Path $installRel -Line 1 -Message 'Agent install guide is missing; https://magictray.app/install.txt is served from it.'
    }
    else {
        # Every occurrence, as elsewhere in this script: a second "current
        # release is vX.Y.Z" added lower down must not escape the check.
        $examples = @(Get-TaggedValueList -Content $install -Pattern 'current release is v(\d+\.\d+\.\d+)')
        if ($examples.Count -eq 0) {
            Write-Problem -Path $installRel -Line 1 -Message 'No "current release is vX.Y.Z" example found. STEP 1 names the current release as an illustration, and this check holds that illustration to the released version, so the wording has to stay recognisable. Reword the check with the file.'
        }
        foreach ($example in $examples) {
            if ($example.Value -ne $releasedVersion) {
                Write-Problem -Path $installRel -Line $example.Line -Message ('The "current release" example says v{0} but the released version is {1}. The file tells the agent not to trust the example, but a stale one still misleads: update it when a release is promoted.' -f $example.Value, $releasedVersion)
            }
        }
    }
}

# ---------------------------------------------------------------------------
# 6. The csproj may lead the released version, never trail it.
# ---------------------------------------------------------------------------

if ($null -ne $csprojVersion -and $null -ne $releasedVersion) {
    $developed = [version]$csprojVersion
    $released = [version]$releasedVersion
    if ($developed -lt $released) {
        Write-Problem -Path $csprojRel -Line $csprojVersionLine -Message ("<Version> {0} is behind the released version {1}. Bump the csproj: a release must never be cut from a tree with a stale version." -f $csprojVersion, $releasedVersion)
    }
    elseif ($developed -gt $released) {
        Write-Advice -Path $csprojRel -Line $csprojVersionLine -Message ("<Version> {0} is ahead of the released version {1}. That is the normal 'next version in development' state; the winget manifests, docs/index.html and docs/install.txt stay on {1} until {0} is tagged and published." -f $csprojVersion, $releasedVersion)
    }
}

if ($script:ProblemCount -gt 0) {
    Write-Host ('version sync: {0} problem(s) found.' -f $script:ProblemCount)
    exit 1
}

Write-Host ('version sync: OK (released {0}, csproj {1}).' -f $releasedVersion, $csprojVersion)
exit 0
