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
       docs/site.css must resolve to a file on disk, inside the published tree.
       Remove this and a renamed page or image 404s silently in production, or a
       '../' reference points at a file Pages never serves; GitHub Pages is case
       sensitive, so a case-only rename is equally fatal and equally invisible
       locally on Windows.

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
       sitemap, and keep every published Markdown file - at any depth under the
       site root - out of the index unless it is deliberately listed in the
       sitemap. Coverage is decided the way a crawler decides it (RFC 9309):
       groups naming the same user-agent are merged, each user-agent is judged
       against its own rules rather than everyone else's, and the most specific
       matching rule wins, so an Allow can defeat a Disallow. Remove this and
       internal design notes get indexed, or a typo silently turns the whole
       file into a no-op.

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
# A reference may name this site by its own origin instead of a relative path -
# every page's rel=canonical does. Those resolve on disk like any other page, so
# strip the origin and check them rather than writing them off as external.
$SelfOriginPattern = '^(?:https?:)?//' + [regex]::Escape(([uri]$SiteOrigin).Host) + '(?=[/?#]|$)'

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

function Test-PathInside {
    <#
    .SYNOPSIS
        True when a resolved path is a descendant of the site root.
    .DESCRIPTION
        Resolve-Reference normalises '..' away, so a reference like
        '../../etc/passwd' can land on a real file outside the published tree and
        Test-Path alone would call it healthy. The prefix test here is
        separator-aware: the root is normalised to end with the platform
        separator first, so a sibling directory that merely shares the root's
        textual prefix ('docs-old' beside 'docs') is rejected while real
        descendants still pass. The comparison is ordinal because GitHub Pages
        is case sensitive.
    #>
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Root
    )

    $separator = [System.IO.Path]::DirectorySeparatorChar
    $full = [System.IO.Path]::GetFullPath($Path)
    $prefix = [System.IO.Path]::GetFullPath($Root).TrimEnd($separator) + $separator
    return $full.StartsWith($prefix, [System.StringComparison]::Ordinal)
}

function Convert-RobotsRuleToRegex {
    <#
    .SYNOPSIS
        Translates a robots.txt path rule into a regex anchored at the start of a
        site-root-relative path.
    .DESCRIPTION
        In robots.txt only two characters are special: '*' matches any sequence,
        and a TRAILING '$' anchors the rule to the end of the path. Everything
        else is literal, so the rule is escaped first. The trailing '$' is
        removed before escaping and re-added as a real anchor afterwards -
        [regex]::Escape turns it into '\$', which would otherwise match a literal
        dollar sign and make the rule match nothing. A '$' anywhere other than
        the end stays literal, which is what robots.txt means by it.
    #>
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Rule
    )

    $path = $Rule.TrimStart('/')
    $anchored = $path.EndsWith('$')
    if ($anchored) { $path = $path.Substring(0, $path.Length - 1) }

    $pattern = '^' + ([regex]::Escape($path) -replace '\\\*', '.*')
    if ($anchored) { $pattern += '$' }
    return $pattern
}

function Get-RobotsVerdict {
    <#
    .SYNOPSIS
        The effective robots.txt verdict for one path under one merged rule set.
    .DESCRIPTION
        RFC 9309 section 2.2.2: the most specific matching rule wins, measured by
        the octet length of the rule's path pattern, and an Allow beats an equally
        specific Disallow. A path that no rule matches is crawlable. Collecting
        only Disallow rules and calling any match "covered" would therefore be
        wrong twice over: it ignores an Allow that overrides a broader Disallow,
        and it lets a short Disallow outrank a longer, more specific Allow.

        Returns 'disallow', 'allow', or 'none' when nothing matched.
    #>
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][AllowEmptyCollection()][pscustomobject[]]$Rule
    )

    $verdict = 'none'
    $specificity = -1
    foreach ($item in $Rule) {
        if ($Path -cnotmatch $item.Pattern) { continue }
        $length = $item.Value.Length
        if ($length -gt $specificity) {
            $specificity = $length
            $verdict = $item.Type
        } elseif ($length -eq $specificity -and $item.Type -eq 'allow') {
            $verdict = 'allow'
        }
    }
    return $verdict
}

function Test-Reference {
    <#
    .SYNOPSIS
        Resolves one reference and reports it when it escapes the site root or
        names no file.
    .DESCRIPTION
        Shared by the href/src sweep and the CSS url(...) sweep, which differ
        only in how they word the finding and where they get the line from.
        A self-origin reference becomes root-relative first: a rel=canonical
        pointing at a page that was renamed de-indexes it, which is the most
        expensive dead link on the site and the one a relative-only sweep misses.
    #>
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Value,
        [Parameter(Mandatory)][string]$BaseDir,
        [Parameter(Mandatory)][string]$SiteRoot,
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][int]$Line,
        [Parameter(Mandatory)][string]$Label
    )

    $value = $Value
    if ($value -match $SelfOriginPattern) {
        $value = $value -replace $SelfOriginPattern, ''
        if ($value -eq '') { $value = '/' }
    }
    if (Test-ExternalReference -Value $value) { return }

    $target = Resolve-Reference -Value $value -BaseDir $BaseDir -SiteRoot $SiteRoot
    $targetRelative = Get-RepoRelativePath -FullPath $target -RepoRoot $RepoRoot
    if (-not (Test-PathInside -Path $target -Root $SiteRoot)) {
        New-Finding -Severity 'error' -Path $Path -Line $Line `
            -Message ("{0} escapes the site root: {1}" -f $Label, $targetRelative)
        return
    }
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
        New-Finding -Severity 'error' -Path $Path -Line $Line `
            -Message ("dead {0}: no file at {1}" -f $Label, $targetRelative)
    }
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
        Test-Reference -Value $ref.Value -BaseDir $ref.Directory -SiteRoot $SiteRoot -RepoRoot $RepoRoot `
            -Path (Get-RepoRelativePath -FullPath $ref.File -RepoRoot $RepoRoot) -Line $ref.Line `
            -Label ('{0}="{1}"' -f $ref.Attribute, $ref.Value)
    }

    $cssPath = Join-Path $SiteRoot 'site.css'
    if (-not (Test-Path -LiteralPath $cssPath -PathType Leaf)) { return }

    $css = Get-Content -LiteralPath $cssPath -Raw
    $cssRelative = Get-RepoRelativePath -FullPath $cssPath -RepoRoot $RepoRoot
    foreach ($match in [regex]::Matches($css, 'url\(\s*(?<quote>["'']?)(?<value>[^"''()]*)\k<quote>\s*\)', 'IgnoreCase')) {
        $value = $match.Groups['value'].Value.Trim()
        Test-Reference -Value $value -BaseDir $SiteRoot -SiteRoot $SiteRoot -RepoRoot $RepoRoot `
            -Path $cssRelative -Line (Get-LineNumber -Text $css -Offset $match.Index) `
            -Label ('url("{0}")' -f $value)
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
        if ($entry.PSObject.Properties.Name -notcontains 'loc') {
            New-Finding -Severity 'error' -Path $sitemapRelative -Message 'a <url> entry has no <loc>'
            continue
        }
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
        if (-not (Test-PathInside -Path $target -Root $SiteRoot)) {
            New-Finding -Severity 'error' -Path $sitemapRelative -Line $line -Message "<loc>$loc</loc> maps to $relative, which escapes $siteRelative/"
            continue
        }
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
    .DESCRIPTION
        Coverage is judged the way a crawler judges it (RFC 9309): groups naming
        the same product token are merged, each user-agent is evaluated against
        its own merged rules, and the most specific matching rule wins - so an
        Allow can defeat a Disallow. A file is only safe when the effective rule
        is a Disallow for every user-agent named in the file.
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

    # robots.txt is a sequence of GROUPS: one or more consecutive User-agent
    # lines, then the rules that bind exactly those agents. A rule line closes
    # the agent list, so the next User-agent starts a fresh group. Rules have to
    # be tracked per group, because a crawler that has a group of its own never
    # reads the wildcard group - a Disallow that only appears under
    # 'User-agent: *' does not keep GPTBot out of anything.
    $groups = [System.Collections.Generic.List[pscustomobject]]::new()
    $current = $null
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
        # RFC 9309 2.2: a directive line may end in a '# comment'.
        $value = ($directive.Groups['value'].Value -split '#', 2)[0].Trim()

        if ($key -eq 'sitemap') {
            $sitemapSeen = $true
            if ($value -ne $SitemapUrl) {
                New-Finding -Severity 'error' -Path $robotsRelative -Line $number -Message "Sitemap: must be $SitemapUrl but is '$value'"
            } elseif (-not (Test-Path -LiteralPath (Join-Path $SiteRoot 'sitemap.xml') -PathType Leaf)) {
                New-Finding -Severity 'error' -Path $robotsRelative -Line $number -Message "Sitemap: points at $value but sitemap.xml does not exist"
            }
            continue
        }

        if ($key -eq 'user-agent') {
            if ($null -eq $current -or $current.Closed) {
                $current = [pscustomobject]@{
                    Agents = [System.Collections.Generic.List[string]]::new()
                    Rules  = [System.Collections.Generic.List[pscustomobject]]::new()
                    Closed = $false
                }
                $groups.Add($current)
            }
            $current.Agents.Add($value)
            continue
        }

        # Allow / Disallow / Crawl-delay: a rule, so this group takes no further
        # User-agent lines.
        if ($null -eq $current) {
            New-Finding -Severity 'error' -Path $robotsRelative -Line $number -Message "'$line' comes before any User-agent line, so no crawler is bound by it"
            continue
        }
        $current.Closed = $true

        # An empty Disallow value means "disallow nothing", and an empty Allow is
        # a no-op: neither constrains a path, so neither joins the rule set.
        if (($key -eq 'allow' -or $key -eq 'disallow') -and $value -ne '') {
            $current.Rules.Add([pscustomobject]@{
                    Type    = $key
                    Value   = $value
                    Pattern = Convert-RobotsRuleToRegex -Rule $value
                })
        }

        if ($key -eq 'disallow' -and $value -ne '') {
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

    # RFC 9309 section 2.2.1: records naming the same product token are merged
    # into one rule set, so a repeated 'User-agent: GPTBot' group extends the
    # earlier one rather than standing alone. Collapse the groups accordingly and
    # judge each user-agent against its own merged rules. Matching is ordinal;
    # product tokens are case-insensitive, so they are folded to lower case.
    $byAgent = [ordered]@{}
    foreach ($group in $groups) {
        foreach ($agent in $group.Agents) {
            $token = $agent.ToLowerInvariant()
            if (-not $byAgent.Contains($token)) {
                $byAgent[$token] = [System.Collections.Generic.List[pscustomobject]]::new()
            }
            $byAgent[$token].AddRange($group.Rules)
        }
    }

    # Markdown deliberately published (listed in the sitemap) is allowed to be
    # indexed. For everything else under the site root, the effective rule for
    # EVERY user-agent must be a Disallow, or the crawlers it does not bind will
    # index it.
    $published = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $sitemapPath = Join-Path $SiteRoot 'sitemap.xml'
    if (Test-Path -LiteralPath $sitemapPath -PathType Leaf) {
        foreach ($match in [regex]::Matches((Get-Content -LiteralPath $sitemapPath -Raw), '<loc>\s*([^<]+?)\s*</loc>')) {
            [void]$published.Add($match.Groups[1].Value.Trim())
        }
    }

    foreach ($markdown in Get-ChildItem -LiteralPath $SiteRoot -Filter '*.md' -File -Recurse) {
        # Rules are URL paths, so match the file's path relative to the site root
        # rather than its bare name: docs/notes/DESIGN-x.md is served at
        # /notes/DESIGN-x.md, which 'Disallow: /DESIGN-' does not cover. For a
        # root-level file the relative path is just the name, as before.
        $relative = [System.IO.Path]::GetRelativePath($SiteRoot, $markdown.FullName).Replace('\', '/')
        if ($published.Contains("$SiteOrigin/$relative")) { continue }

        if ($byAgent.Count -eq 0) {
            New-Finding -Severity 'error' -Path $robotsRelative `
                -Message "$relative is published and not in sitemap.xml, and robots.txt declares no User-agent group at all; nothing keeps it out of an index"
            continue
        }

        $reachable = [System.Collections.Generic.List[string]]::new()
        foreach ($token in $byAgent.Keys) {
            $verdict = Get-RobotsVerdict -Path $relative -Rule $byAgent[$token].ToArray()
            if ($verdict -ne 'disallow') { $reachable.Add(("{0} ({1})" -f $token, $verdict)) }
        }

        if ($reachable.Count -gt 0) {
            New-Finding -Severity 'error' -Path $robotsRelative `
                -Message ("{0} is published and not in sitemap.xml, and robots.txt does not disallow it for user-agent(s): {1}; it would be indexed" -f $relative, ($reachable -join ', '))
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

Write-Output ("Site checks: {0} pages, {1} references, {2} errors, {3} warnings" -f
    $htmlFiles.Count, $references.Count, $errors.Count, $warnings.Count)

if ($errors.Count -gt 0) { exit 1 }
exit 0
