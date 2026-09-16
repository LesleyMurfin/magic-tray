#Requires -Version 7
<#
.SYNOPSIS
    Static integrity checks for the docs/ site that GitHub Pages publishes as-is.

.DESCRIPTION
    docs/ is served straight from the main branch (deploy-from-branch, folder /docs,
    custom domain magictray.app). There is no build step, so nothing catches a
    renamed page, a moved image, or a stale host name before it reaches visitors.
    This script is that missing build step. It is read-only and cross-platform:
    it runs on ubuntu-latest in CI and on a developer machine with PowerShell 7.

    Checks, and what breaks if the check is removed:

    1. Dead internal links and assets (Test-InternalLink)
       Every relative href/src in docs/**/*.html and every url(...) in
       docs/site.css must resolve to a file on disk. Remove this and a renamed
       page or image 404s silently in production; GitHub Pages is case sensitive,
       so a case-only rename is equally fatal and equally invisible locally on
       Windows.

    2. Sitemap integrity (Test-Sitemap)
       Every <loc> must live under https://magictray.app/, map to a real file,
       and carry a <lastmod>; every docs/*.html page must be listed. Remove this
       and search engines keep crawling deleted URLs while new pages never get
       discovered.

    3. Pages plumbing (Test-PagesPlumbing)
       docs/CNAME must contain exactly magictray.app and docs/.nojekyll must
       exist. Remove this and an accidental deletion silently drops the custom
       domain (the site reverts to the github.io host, breaking every inbound
       link) or lets Jekyll swallow files and directories beginning with an
       underscore.

    4. robots.txt sanity (Test-RobotsFile)
       robots.txt must contain only valid directives, point Sitemap: at the real
       sitemap, and cover every published Markdown file that is not deliberately
       listed in the sitemap. Remove this and internal design notes get indexed,
       or a typo silently turns the whole file into a no-op.

    5. Stale hosts (Test-StaleHost)
       No href/src may reference localhost, 127.0.0.1, or the project's old
       github.io Pages URL. Remove this and a copy-pasted local URL ships to
       production, or a link quietly bounces through the pre-custom-domain host.

    Failures are reported as GitHub Actions annotations
    (::error file=<path>,line=<n>::<message>) and as a non-zero exit code.

.PARAMETER DocsDir
    Path to the published site directory. Defaults to 'docs' relative to the
    current directory, falling back to the copy next to this script's repository
    root so the script also works when invoked from elsewhere.

.EXAMPLE
    pwsh -File scripts/check-site.ps1

.EXAMPLE
    pwsh -File scripts/check-site.ps1 -DocsDir docs
#>
[CmdletBinding()]
param(
    [string]$DocsDir = 'docs'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$SiteOrigin = 'https://magictray.app'
$SitemapUrl = "$SiteOrigin/sitemap.xml"

# ---------------------------------------------------------------- helpers ---

function New-Finding {
    <#
    .SYNOPSIS
        Builds one annotation record. Line is optional; 0 means "not derivable".
    #>
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][ValidateSet('error', 'warning')][string]$Severity,
        [Parameter(Mandatory)][string]$Path,
        [int]$Line = 0,
        [Parameter(Mandatory)][string]$Message
    )

    [pscustomobject]@{
        Severity = $Severity
        Path     = $Path
        Line     = $Line
        Message  = $Message
    }
}

function Get-LineNumber {
    <#
    .SYNOPSIS
        Converts a character offset inside a file's text into a 1-based line number.
    #>
    [OutputType([int])]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Text,
        [Parameter(Mandatory)][int]$Offset
    )

    if ($Offset -le 0) { return 1 }
    $head = $Text.Substring(0, [Math]::Min($Offset, $Text.Length))
    return ($head.Split("`n").Count)
}

function Get-RepoRelativePath {
    <#
    .SYNOPSIS
        Renders a full path relative to the repository root, using forward slashes.
    #>
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string]$FullPath,
        [Parameter(Mandatory)][string]$RepoRoot
    )

    $full = [System.IO.Path]::GetFullPath($FullPath)
    $root = [System.IO.Path]::GetFullPath($RepoRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    if ($full.StartsWith($root + [System.IO.Path]::DirectorySeparatorChar)) {
        $full = $full.Substring($root.Length + 1)
    }
    return ($full -replace '\\', '/')
}

function Get-HtmlReference {
    <#
    .SYNOPSIS
        Extracts every href/src attribute value from the given HTML files.
    .DESCRIPTION
        Returns records carrying the source file, the 1-based line, the attribute
        name and the raw value, so link checks and host checks share one parse.
    #>
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][System.IO.FileInfo[]]$File
    )

    $pattern = '(?<attr>href|src)\s*=\s*(?:"(?<value>[^"]*)"|''(?<value>[^'']*)'')'
    foreach ($item in $File) {
        $text = Get-Content -LiteralPath $item.FullName -Raw
        foreach ($match in [regex]::Matches($text, $pattern, 'IgnoreCase')) {
            [pscustomobject]@{
                File      = $item.FullName
                Directory = $item.DirectoryName
                Line      = Get-LineNumber -Text $text -Offset $match.Index
                Attribute = $match.Groups['attr'].Value.ToLowerInvariant()
                Value     = $match.Groups['value'].Value.Trim()
            }
        }
    }
}

function Test-ExternalReference {
    <#
    .SYNOPSIS
        True when a reference is not a local file path (scheme, mailto, fragment,
        protocol-relative URL, data URI or template placeholder).
    #>
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) { return $true }
    if ($Value.StartsWith('#')) { return $true }
    if ($Value.StartsWith('//')) { return $true }
    if ($Value -match '^[a-z][a-z0-9+.-]*:') { return $true }
    return $false
}

function Resolve-Reference {
    <#
    .SYNOPSIS
        Maps a relative reference to the file on disk it must resolve to.
    .DESCRIPTION
        Strips #fragment and ?query, treats a directory reference ('', '.', './',
        or any trailing '/') as index.html, and resolves root-relative values
        against the site root rather than the containing directory.
    #>
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][string]$BaseDir,
        [Parameter(Mandatory)][string]$SiteRoot
    )

    $relative = ($Value -split '[#?]')[0]
    $anchor = $BaseDir
    if ($relative.StartsWith('/')) {
        $anchor = $SiteRoot
        $relative = $relative.TrimStart('/')
    }
    if ($relative -eq '' -or $relative -eq '.' -or $relative.EndsWith('/')) {
        $relative = "$relative/index.html" -replace '(^|/)\./', '$1' -replace '//+', '/'
        $relative = $relative.TrimStart('/')
    }

    $native = $relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    return [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($anchor, $native))
}

# ----------------------------------------------------------------- checks ---

function Test-InternalLink {
    <#
    .SYNOPSIS
        Check 1: every relative href/src and every CSS url(...) resolves on disk.
    #>
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][string]$SiteRoot,
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][AllowEmptyCollection()][pscustomobject[]]$Reference
    )

    foreach ($ref in $Reference) {
        if (Test-ExternalReference -Value $ref.Value) { continue }
        $target = Resolve-Reference -Value $ref.Value -BaseDir $ref.Directory -SiteRoot $SiteRoot
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
            New-Finding -Severity 'error' `
                -Path (Get-RepoRelativePath -FullPath $ref.File -RepoRoot $RepoRoot) `
                -Line $ref.Line `
                -Message ("dead {0}=""{1}"": no file at {2}" -f $ref.Attribute, $ref.Value,
                    (Get-RepoRelativePath -FullPath $target -RepoRoot $RepoRoot))
        }
    }

    $cssPath = Join-Path $SiteRoot 'site.css'
    if (-not (Test-Path -LiteralPath $cssPath -PathType Leaf)) { return }

    $css = Get-Content -LiteralPath $cssPath -Raw
    $cssRelative = Get-RepoRelativePath -FullPath $cssPath -RepoRoot $RepoRoot
    foreach ($match in [regex]::Matches($css, 'url\(\s*(?<quote>["'']?)(?<value>[^"''()]*)\k<quote>\s*\)', 'IgnoreCase')) {
        $value = $match.Groups['value'].Value.Trim()
        if (Test-ExternalReference -Value $value) { continue }
        $target = Resolve-Reference -Value $value -BaseDir $SiteRoot -SiteRoot $SiteRoot
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
            New-Finding -Severity 'error' -Path $cssRelative `
                -Line (Get-LineNumber -Text $css -Offset $match.Index) `
                -Message ("dead url(""{0}""): no file at {1}" -f $value,
                    (Get-RepoRelativePath -FullPath $target -RepoRoot $RepoRoot))
        }
    }
}

function Test-Sitemap {
    <#
    .SYNOPSIS
        Check 2: sitemap.xml lists exactly the published pages, with lastmod.
    #>
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][string]$SiteRoot,
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.IO.FileInfo[]]$HtmlFile
    )

    $sitemapPath = Join-Path $SiteRoot 'sitemap.xml'
    $sitemapRelative = Get-RepoRelativePath -FullPath $sitemapPath -RepoRoot $RepoRoot
    $siteRelative = Get-RepoRelativePath -FullPath $SiteRoot -RepoRoot $RepoRoot
    if (-not (Test-Path -LiteralPath $sitemapPath -PathType Leaf)) {
        New-Finding -Severity 'error' -Path $sitemapRelative -Message 'sitemap.xml is missing'
        return
    }

    $raw = Get-Content -LiteralPath $sitemapPath -Raw
    try {
        $xml = [xml]$raw
    } catch {
        New-Finding -Severity 'error' -Path $sitemapRelative -Message "sitemap.xml is not well-formed XML: $($_.Exception.Message)"
        return
    }

    # StrictMode makes dotted access to an absent child throw, so read the <url>
    # elements through the DOM: an empty sitemap must report, not crash.
    $entries = @($xml.GetElementsByTagName('url'))
    if ($entries.Count -eq 0) {
        New-Finding -Severity 'error' -Path $sitemapRelative -Message 'sitemap.xml lists no <url> entries'
    }

    $listed = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($entry in $entries) {
        $loc = "$($entry.loc)".Trim()
        $line = 1
        $locMatch = [regex]::Match($raw, "<loc>\s*$([regex]::Escape($loc))\s*</loc>")
        if ($locMatch.Success) { $line = Get-LineNumber -Text $raw -Offset $locMatch.Index }

        $hasLastmod = $entry.PSObject.Properties.Name -contains 'lastmod' -and -not [string]::IsNullOrWhiteSpace("$($entry.lastmod)")
        if (-not $hasLastmod) {
            New-Finding -Severity 'error' -Path $sitemapRelative -Line $line -Message "<loc>$loc</loc> has no <lastmod>"
        }

        if (-not $loc.StartsWith("$SiteOrigin/")) {
            New-Finding -Severity 'error' -Path $sitemapRelative -Line $line -Message "<loc>$loc</loc> is not under $SiteOrigin/"
            continue
        }

        $relative = $loc.Substring("$SiteOrigin/".Length)
        if ($relative -eq '' -or $relative.EndsWith('/')) { $relative = "${relative}index.html" }
        $target = Join-Path $SiteRoot ($relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
            New-Finding -Severity 'error' -Path $sitemapRelative -Line $line -Message "<loc>$loc</loc> maps to $relative, which does not exist in $siteRelative/"
            continue
        }
        [void]$listed.Add($relative)
    }

    $rootPath = (Get-Item -LiteralPath $SiteRoot).FullName
    foreach ($page in $HtmlFile) {
        if ($page.DirectoryName -ne $rootPath) { continue }
        if ($listed.Contains($page.Name)) { continue }
        New-Finding -Severity 'error' -Path $sitemapRelative -Message "$($page.Name) is published but missing from sitemap.xml"
    }
}

function Test-PagesPlumbing {
    <#
    .SYNOPSIS
        Check 3: CNAME and .nojekyll, the two files that keep Pages serving the
        custom domain and underscore-prefixed assets.
    #>
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][string]$SiteRoot,
        [Parameter(Mandatory)][string]$RepoRoot
    )

    $expectedHost = ([uri]$SiteOrigin).Host

    $cnamePath = Join-Path $SiteRoot 'CNAME'
    $cnameRelative = Get-RepoRelativePath -FullPath $cnamePath -RepoRoot $RepoRoot
    if (-not (Test-Path -LiteralPath $cnamePath -PathType Leaf)) {
        New-Finding -Severity 'error' -Path $cnameRelative -Message "CNAME is missing; the custom domain $expectedHost would be dropped on the next Pages deploy"
    } else {
        $cname = (Get-Content -LiteralPath $cnamePath -Raw).Trim()
        if ($cname -cne $expectedHost) {
            New-Finding -Severity 'error' -Path $cnameRelative -Line 1 -Message "CNAME must contain exactly '$expectedHost' but contains '$cname'"
        }
    }

    $nojekyllPath = Join-Path $SiteRoot '.nojekyll'
    if (-not (Test-Path -LiteralPath $nojekyllPath -PathType Leaf)) {
        New-Finding -Severity 'error' -Path (Get-RepoRelativePath -FullPath $nojekyllPath -RepoRoot $RepoRoot) `
            -Message '.nojekyll is missing; Pages would run Jekyll and drop files whose names begin with an underscore'
    }
}

function Test-RobotsFile {
    <#
    .SYNOPSIS
        Check 4: robots.txt is syntactically valid, points at the real sitemap,
        and keeps every non-published Markdown file out of search indexes.
    #>
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][string]$SiteRoot,
        [Parameter(Mandatory)][string]$RepoRoot
    )

    $robotsPath = Join-Path $SiteRoot 'robots.txt'
    $robotsRelative = Get-RepoRelativePath -FullPath $robotsPath -RepoRoot $RepoRoot
    if (-not (Test-Path -LiteralPath $robotsPath -PathType Leaf)) {
        New-Finding -Severity 'error' -Path $robotsRelative -Message 'robots.txt is missing'
        return
    }

    $lines = @(Get-Content -LiteralPath $robotsPath)
    $disallow = [System.Collections.Generic.List[string]]::new()
    $sitemapSeen = $false

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i].Trim()
        $number = $i + 1
        if ($line -eq '' -or $line.StartsWith('#')) { continue }

        $directive = [regex]::Match($line, '^(?<key>User-agent|Allow|Disallow|Sitemap|Crawl-delay)\s*:\s*(?<value>.*)$', 'IgnoreCase')
        if (-not $directive.Success) {
            New-Finding -Severity 'error' -Path $robotsRelative -Line $number -Message "unrecognised robots.txt directive: '$line'"
            continue
        }

        $key = $directive.Groups['key'].Value.ToLowerInvariant()
        $value = $directive.Groups['value'].Value.Trim()

        if ($key -eq 'sitemap') {
            $sitemapSeen = $true
            if ($value -ne $SitemapUrl) {
                New-Finding -Severity 'error' -Path $robotsRelative -Line $number -Message "Sitemap: must be $SitemapUrl but is '$value'"
            } elseif (-not (Test-Path -LiteralPath (Join-Path $SiteRoot 'sitemap.xml') -PathType Leaf)) {
                New-Finding -Severity 'error' -Path $robotsRelative -Line $number -Message "Sitemap: points at $value but sitemap.xml does not exist"
            }
        }

        if ($key -eq 'disallow' -and $value -ne '') {
            if (-not $disallow.Contains($value)) { $disallow.Add($value) }
            # A rule naming a concrete file (has an extension, no wildcard) that no
            # longer exists is dead weight, not a leak: warn rather than fail.
            if ($value -notmatch '[*$]' -and [System.IO.Path]::GetExtension($value) -ne '') {
                $target = Join-Path $SiteRoot ($value.TrimStart('/').Replace('/', [System.IO.Path]::DirectorySeparatorChar))
                if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
                    New-Finding -Severity 'warning' -Path $robotsRelative -Line $number -Message "Disallow: $value names a file that no longer exists; the rule is dead"
                }
            }
        }
    }

    if (-not $sitemapSeen) {
        New-Finding -Severity 'error' -Path $robotsRelative -Message "robots.txt has no Sitemap: line; it must point at $SitemapUrl"
    }

    # Markdown deliberately published (listed in the sitemap) is allowed to be
    # indexed. Everything else under docs/ must be covered by a Disallow rule.
    $published = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $sitemapPath = Join-Path $SiteRoot 'sitemap.xml'
    if (Test-Path -LiteralPath $sitemapPath -PathType Leaf) {
        foreach ($match in [regex]::Matches((Get-Content -LiteralPath $sitemapPath -Raw), '<loc>\s*([^<]+?)\s*</loc>')) {
            [void]$published.Add($match.Groups[1].Value.Trim())
        }
    }

    foreach ($markdown in Get-ChildItem -LiteralPath $SiteRoot -Filter '*.md' -File) {
        $url = "$SiteOrigin/$($markdown.Name)"
        if ($published.Contains($url)) { continue }

        $covered = $false
        foreach ($rule in $disallow) {
            $pattern = '^' + ([regex]::Escape($rule.TrimStart('/')) -replace '\\\*', '.*')
            if ("$($markdown.Name)" -match $pattern) { $covered = $true; break }
        }
        if (-not $covered) {
            New-Finding -Severity 'error' -Path $robotsRelative `
                -Message "$($markdown.Name) is published but matches no Disallow rule and is not in sitemap.xml; it would be indexed"
        }
    }
}

function Test-StaleHost {
    <#
    .SYNOPSIS
        Check 5: no link points at a local development host or at this project's
        retired github.io Pages URL.
    #>
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][AllowEmptyCollection()][pscustomobject[]]$Reference
    )

    foreach ($ref in $Reference) {
        $value = $ref.Value
        $reason = $null
        if ($value -match 'localhost|127\.0\.0\.1') {
            $reason = 'points at a local development host'
        } elseif ($value -match '(?i)github\.io/magic-tray(/|$)' -or $value -match '(?i)^https?://magictray\.github\.io') {
            $reason = "points at the retired github.io Pages URL; the site moved to $SiteOrigin"
        }
        if ($reason) {
            New-Finding -Severity 'error' `
                -Path (Get-RepoRelativePath -FullPath $ref.File -RepoRoot $RepoRoot) `
                -Line $ref.Line `
                -Message ("{0}=""{1}"" {2}" -f $ref.Attribute, $value, $reason)
        }
    }
}

# ------------------------------------------------------------------- main ---

if (-not (Test-Path -LiteralPath $DocsDir -PathType Container)) {
    $fallback = Join-Path (Split-Path -Parent $PSScriptRoot) $DocsDir
    if (Test-Path -LiteralPath $fallback -PathType Container) {
        $DocsDir = $fallback
    } else {
        Write-Output "::error::docs directory '$DocsDir' not found"
        exit 1
    }
}

$siteRoot = (Get-Item -LiteralPath $DocsDir).FullName
$repoRoot = Split-Path -Parent $siteRoot
$htmlFiles = @(Get-ChildItem -LiteralPath $siteRoot -Filter '*.html' -File -Recurse | Sort-Object -Property FullName)
$references = @(Get-HtmlReference -File $htmlFiles)

$findings = [System.Collections.Generic.List[pscustomobject]]::new()
$findings.AddRange([pscustomobject[]]@(Test-InternalLink -SiteRoot $siteRoot -RepoRoot $repoRoot -Reference $references))
$findings.AddRange([pscustomobject[]]@(Test-Sitemap -SiteRoot $siteRoot -RepoRoot $repoRoot -HtmlFile $htmlFiles))
$findings.AddRange([pscustomobject[]]@(Test-PagesPlumbing -SiteRoot $siteRoot -RepoRoot $repoRoot))
$findings.AddRange([pscustomobject[]]@(Test-RobotsFile -SiteRoot $siteRoot -RepoRoot $repoRoot))
$findings.AddRange([pscustomobject[]]@(Test-StaleHost -RepoRoot $repoRoot -Reference $references))

$errors = @($findings | Where-Object { $_.Severity -eq 'error' })
$warnings = @($findings | Where-Object { $_.Severity -eq 'warning' })

foreach ($finding in $findings) {
    $location = "file=$($finding.Path)"
    if ($finding.Line -gt 0) { $location += ",line=$($finding.Line)" }
    Write-Output "::$($finding.Severity) $location::$($finding.Message)"
}

$scanned = $htmlFiles.Count
foreach ($extra in @('site.css', 'sitemap.xml', 'robots.txt', 'CNAME', '.nojekyll')) {
    if (Test-Path -LiteralPath (Join-Path $siteRoot $extra) -PathType Leaf) { $scanned++ }
}

Write-Output ("Site checks: {0} files, {1} errors, {2} warnings" -f $scanned, $errors.Count, $warnings.Count)

if ($errors.Count -gt 0) { exit 1 }
exit 0
