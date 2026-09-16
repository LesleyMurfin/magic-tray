#Requires -Version 7
<#
.SYNOPSIS
    Structured-data (JSON-LD) checks for the docs/ site that answer engines read.

.DESCRIPTION
    Answer engines resolve "magic tray windows app" from the JSON-LD graph each
    page carries, not from the prose. docs/ is served straight from the main
    branch with no build step, so nothing has ever parsed that graph before it
    reaches a crawler: pages shipped referencing #app and #site by @id without
    declaring them, and the broken entity links stayed broken for months while
    every visible page looked healthy. This script is that missing parse. It is
    read-only, makes no network calls, and runs on ubuntu-latest in CI and on a
    developer machine with PowerShell 7.

    It owns structured data only. Links, sitemap, robots.txt, CNAME and stale
    hosts belong to scripts/check-site.ps1; the two scripts do not overlap.

    Checks, and what breaks if the check is removed:

    1. One parseable block per page (Test-JsonLdBlock)
       Every docs/**/*.html carries exactly one
       <script type="application/ld+json">, it parses as JSON, its @context is
       https://schema.org and its top level is an @graph array. Remove this and
       a trailing comma or a stray second block silently drops the page's whole
       entity graph - Google reports nothing, the page just stops being an
       entity. Everything below depends on this parse, so a page that fails
       here is skipped by the later checks.

    2. No dangling @id reference (Test-IdReference)
       Structured data is parsed per page, so a { "@id": "..." } reference only
       means something when THAT page also declares the node. Every reference
       must resolve inside its own page's graph. This is the defect that
       motivated the script: subpages pointed "about" and "isPartOf" at
       https://magictray.app/#app and #site, which nothing on those pages
       declared, so each reference resolved to a node with no type and no name
       and the software entity never accumulated authority.

    3. Entity consistency across pages (Test-EntityConsistency)
       Nodes that share an @id are the same thing to a crawler, which merges
       them. For any property declared on both copies the values must be equal
       (objects compare key-order-insensitively, arrays keep their order).
       Remove this and one page says softwareVersion 1.1.0 while another says
       1.0.3, or two descriptions contradict each other, and the merge result
       is undefined - the engine picks one, usually not the one you meant.

    4. Page identity (Test-PageIdentity)
       The page's single WebPage node url, its <link rel="canonical"> href and
       the path Pages publishes the file at must be the same URL, and the node
       must carry a dateModified shaped YYYY-MM-DD. Remove this and a
       copy-pasted page claims to be another URL: the graph is then attached to
       the wrong document, duplicate-content handling picks a winner on its
       own, and freshness signals disappear.

    5. FAQ parity (Test-FaqParity)
       Every Question in a FAQPage must have a visible <dt> carrying the same
       text, its acceptedAnswer.text must equal the plain text of the <dd> that
       follows (tags stripped, entities decoded, whitespace collapsed), and no
       <dt> inside a dl.faq may be missing from the graph. Remove this and the
       page offers an answer engine a quotable answer that is not on the page,
       which is the mismatch Google demotes rich results for; the reverse gap
       is cheaper but wastes a question the site already answers.

    6. Version coherence (Test-VersionCoherence)
       Every softwareVersion in the graph and every version shown in a visible
       link must agree. The version the site agrees on is the most common one,
       and every disagreement is reported against it. Remove this and a release
       bump half-lands: the download button or the header brand still offers
       the previous version while the graph advertises the new one, and answer
       engines quote the stale number.

    Failures are reported as GitHub Actions annotations
    (::error file=<path>,line=<n>::<message>) and as a non-zero exit code.

.PARAMETER DocsDir
    Path to the published site directory. Defaults to 'docs' relative to the
    current directory, falling back to the copy next to this script's repository
    root so the script also works when invoked from elsewhere.

.EXAMPLE
    pwsh -File scripts/check-aeo.ps1

.EXAMPLE
    pwsh -File scripts/check-aeo.ps1 -DocsDir /tmp/aeo-fixture
#>
[CmdletBinding()]
param(
    [string]$DocsDir = 'docs'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$SiteOrigin = 'https://magictray.app'
$SchemaContext = 'https://schema.org'

# ---------------------------------------------------------------- helpers ---

function New-Finding {
    <#
    .SYNOPSIS
        Builds one annotation record. Line is optional; 0 means "not derivable".
    .DESCRIPTION
        Schema values can contain newlines, and a newline inside an annotation
        body truncates the annotation, so the message is flattened here once
        rather than at every call site.
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
        Message  = ($Message -replace '\s*[\r\n]+\s*', ' ')
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

function Get-TextLine {
    <#
    .SYNOPSIS
        The 1-based line of the first literal occurrence of Needle, or Fallback.
    .DESCRIPTION
        Findings about a JSON value have no offset of their own: the value comes
        out of a parsed object tree. Searching the raw HTML for the value as it
        was written puts the annotation on the right line anyway, and falls back
        to the line the JSON-LD block starts on when the value cannot be found
        verbatim (a re-indented or escaped string, for instance).
    #>
    [OutputType([int])]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Text,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Needle,
        [int]$Fallback = 1
    )

    foreach ($candidate in $Needle) {
        if ([string]::IsNullOrEmpty($candidate)) { continue }
        $index = $Text.IndexOf($candidate, [System.StringComparison]::Ordinal)
        if ($index -ge 0) { return (Get-LineNumber -Text $Text -Offset $index) }
    }
    return $Fallback
}

function Convert-HtmlToText {
    <#
    .SYNOPSIS
        Reduces an HTML fragment to the plain text a reader sees.
    .DESCRIPTION
        Tags are dropped before entities are decoded, never after: decoding
        first would turn a literal &lt;b&gt; in the copy into a tag and then
        delete it. Whitespace is collapsed because HTML treats any run of it as
        one space, so the indentation of a <dd> must not change the comparison.
    #>
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Html
    )

    $text = [regex]::Replace($Html, '<[^>]*>', '')
    $text = [System.Net.WebUtility]::HtmlDecode($text)
    return ([regex]::Replace($text, '\s+', ' ')).Trim()
}

function Convert-SchemaText {
    <#
    .SYNOPSIS
        Normalises a schema string for comparison against page text.
    #>
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Value
    )

    return ([regex]::Replace($Value, '\s+', ' ')).Trim()
}

function ConvertTo-CanonicalText {
    <#
    .SYNOPSIS
        A stable string for any parsed JSON value, for equality comparison.
    .DESCRIPTION
        JSON object key order carries no meaning, so keys are sorted: two pages
        that write the same Offer with the fields swapped must not be reported
        as disagreeing. Array order does carry meaning in schema.org (an
        itemListElement or an alternateName list is ordered), so arrays are
        compared as written. Numbers are formatted with the invariant culture,
        or a machine with a comma decimal separator would see 503 and 503 differ.
    #>
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][AllowNull()]$Value
    )

    if ($null -eq $Value) { return 'null' }
    if ($Value -is [string]) { return '"' + $Value + '"' }
    if ($Value -is [bool]) { if ($Value) { return 'true' } else { return 'false' } }
    if ($Value -is [System.Collections.IDictionary]) {
        $parts = foreach ($key in ($Value.Keys | Sort-Object -CaseSensitive)) {
            '"{0}":{1}' -f $key, (ConvertTo-CanonicalText -Value $Value[$key])
        }
        return '{' + ($parts -join ',') + '}'
    }
    if ($Value -is [System.Collections.IEnumerable]) {
        $parts = foreach ($item in $Value) { ConvertTo-CanonicalText -Value $item }
        return '[' + ($parts -join ',') + ']'
    }
    return [string]::Format([cultureinfo]::InvariantCulture, '{0}', $Value)
}

function Format-SchemaValue {
    <#
    .SYNOPSIS
        Renders a parsed JSON value short enough to read inside an annotation.
    #>
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][AllowNull()]$Value,
        [int]$MaxLength = 120
    )

    $text = ConvertTo-CanonicalText -Value $Value
    if ($text.Length -le $MaxLength) { return $text }
    return $text.Substring(0, $MaxLength) + '...'
}

function Get-TypeName {
    <#
    .SYNOPSIS
        The @type values of a node, as a string array (@type may be a list).
    #>
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)][AllowNull()]$Node
    )

    if ($null -eq $Node -or -not ($Node -is [System.Collections.IDictionary])) { return @() }
    if (-not $Node.Contains('@type')) { return @() }
    $value = $Node['@type']
    if ($value -is [string]) { return @($value) }
    if ($value -is [System.Collections.IEnumerable]) { return @($value | ForEach-Object { "$_" }) }
    return @("$value")
}

function Get-NodeList {
    <#
    .SYNOPSIS
        Normalises a schema property that may hold one object or an array of them.
    #>
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory)][AllowNull()]$Value
    )

    if ($null -eq $Value) { return @() }
    if ($Value -is [System.Collections.IDictionary]) { return @($Value) }
    if ($Value -is [System.Collections.IEnumerable] -and -not ($Value -is [string])) { return @($Value) }
    return @()
}

function Get-GraphEntry {
    <#
    .SYNOPSIS
        Walks a parsed graph and reports every @id it declares or references.
    .DESCRIPTION
        An object whose only key is @id is a reference - it asserts nothing and
        must be resolved elsewhere. An object with an @id and any other property
        is a declaration. The walk is recursive because both forms appear nested
        (a WebPage's "publisher" reference, a FAQPage's inline Questions), and
        because a node buried inside another node still declares its @id to a
        crawler.

        Emits records with Kind ('declaration' or 'reference'), Id and Node.
    #>
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowNull()]$Value
    )

    if ($Value -is [System.Collections.IDictionary]) {
        if ($Value.Contains('@id')) {
            $id = "$($Value['@id'])"
            $kind = if (@($Value.Keys).Count -eq 1) { 'reference' } else { 'declaration' }
            [pscustomobject]@{
                Kind = $kind
                Id   = $id
                Node = $Value
            }
        }
        foreach ($key in @($Value.Keys)) {
            if ($key -eq '@id') { continue }
            Get-GraphEntry -Value $Value[$key]
        }
        return
    }

    if ($Value -is [System.Collections.IEnumerable] -and -not ($Value -is [string])) {
        foreach ($item in $Value) { Get-GraphEntry -Value $item }
    }
}

function Get-FaqPair {
    <#
    .SYNOPSIS
        Extracts the visible <dt>/<dd> pairs of a page, with line numbers.
    .DESCRIPTION
        Pairs are collected per <dl> so each one knows whether its list is the
        site's FAQ list (class contains the token 'faq'). Both directions of the
        parity check need that: a Question may be answered by any definition
        list on the page, but only a dl.faq entry is expected to exist in the
        graph, so a glossary elsewhere on the page is not reported as missing.
    #>
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Html
    )

    foreach ($list in [regex]::Matches($Html, '<dl\b(?<attrs>[^>]*)>(?<body>.*?)</dl>', 'Singleline, IgnoreCase')) {
        $classMatch = [regex]::Match($list.Groups['attrs'].Value, 'class\s*=\s*"(?<class>[^"]*)"', 'IgnoreCase')
        $isFaq = $classMatch.Success -and (($classMatch.Groups['class'].Value -split '\s+') -contains 'faq')
        $body = $list.Groups['body'].Value
        $bodyOffset = $list.Groups['body'].Index

        foreach ($pair in [regex]::Matches($body, '<dt\b[^>]*>(?<term>.*?)</dt>(?<gap>\s*)(?:<dd\b[^>]*>(?<definition>.*?)</dd>)?', 'Singleline, IgnoreCase')) {
            $hasDefinition = $pair.Groups['definition'].Success
            [pscustomobject]@{
                IsFaqList     = $isFaq
                Term          = (Convert-HtmlToText -Html $pair.Groups['term'].Value)
                HasDefinition = $hasDefinition
                Definition    = if ($hasDefinition) { Convert-HtmlToText -Html $pair.Groups['definition'].Value } else { '' }
                TermLine      = (Get-LineNumber -Text $Html -Offset ($bodyOffset + $pair.Index))
                DefinitionLine = if ($hasDefinition) {
                    Get-LineNumber -Text $Html -Offset ($bodyOffset + $pair.Groups['definition'].Index)
                } else {
                    Get-LineNumber -Text $Html -Offset ($bodyOffset + $pair.Index)
                }
            }
        }
    }
}

function Get-PublishedUrl {
    <#
    .SYNOPSIS
        The URL GitHub Pages serves a file in the published tree at.
    .DESCRIPTION
        Pages serves index.html as the directory itself, so docs/index.html is
        https://magictray.app/ and not .../index.html. Any other page keeps its
        file name. The comparison is exact, so a page claiming the index URL is
        caught.
    #>
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string]$RelativePath
    )

    $relative = $RelativePath -replace '\\', '/'
    if ($relative -eq 'index.html') { return "$SiteOrigin/" }
    if ($relative.EndsWith('/index.html')) {
        return "$SiteOrigin/" + $relative.Substring(0, $relative.Length - 'index.html'.Length)
    }
    return "$SiteOrigin/$relative"
}

function Get-JsonLdPage {
    <#
    .SYNOPSIS
        Reads each HTML page once and parses its JSON-LD, without judging it.
    .DESCRIPTION
        Every check needs the same three things - the raw HTML, the parsed graph
        and the canonical link - so the parse happens here once and the checks
        consume records. Parse failures are recorded rather than thrown: one bad
        page must still let the other pages be reported in the same run.

        Graph is $null when the page has no usable graph; Test-JsonLdBlock turns
        that into the finding and the later checks skip the page.
    #>
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][System.IO.FileInfo[]]$File,
        [Parameter(Mandatory)][string]$SiteRoot,
        [Parameter(Mandatory)][string]$RepoRoot
    )

    foreach ($page in $File) {
        $html = Get-Content -LiteralPath $page.FullName -Raw
        if ($null -eq $html) { $html = '' }

        $blocks = @([regex]::Matches($html, '<script\b[^>]*type\s*=\s*["'']application/ld\+json["''][^>]*>(?<json>.*?)</script>', 'Singleline, IgnoreCase'))
        $blockLine = if ($blocks.Count -gt 0) { Get-LineNumber -Text $html -Offset $blocks[0].Index } else { 1 }

        $json = $null
        $graph = $null
        $context = $null
        $parseError = $null
        $graphIsArray = $false

        if ($blocks.Count -eq 1) {
            $json = $blocks[0].Groups['json'].Value
            try {
                $root = $json | ConvertFrom-Json -AsHashtable -Depth 64
            } catch {
                $root = $null
                $parseError = $_.Exception.Message
            }

            if ($null -ne $parseError) {
                # already recorded
            } elseif (-not ($root -is [System.Collections.IDictionary])) {
                $parseError = 'the JSON-LD block is not a JSON object'
            } else {
                if ($root.Contains('@context')) { $context = $root['@context'] }
                if ($root.Contains('@graph')) {
                    $graphValue = $root['@graph']
                    if ($graphValue -is [System.Collections.IEnumerable] -and -not ($graphValue -is [string]) -and -not ($graphValue -is [System.Collections.IDictionary])) {
                        $graphIsArray = $true
                        $graph = @($graphValue)
                    }
                }
            }
        }

        $relative = Get-RepoRelativePath -FullPath $page.FullName -RepoRoot $RepoRoot
        $siteRelative = Get-RepoRelativePath -FullPath $page.FullName -RepoRoot $SiteRoot
        $canonicalMatch = [regex]::Match($html, '<link\b[^>]*rel\s*=\s*["'']canonical["''][^>]*>', 'IgnoreCase')
        $canonical = $null
        $canonicalLine = 0
        if ($canonicalMatch.Success) {
            $canonicalLine = Get-LineNumber -Text $html -Offset $canonicalMatch.Index
            $hrefMatch = [regex]::Match($canonicalMatch.Value, 'href\s*=\s*["''](?<href>[^"'']*)["'']', 'IgnoreCase')
            if ($hrefMatch.Success) { $canonical = $hrefMatch.Groups['href'].Value.Trim() }
        }

        [pscustomobject]@{
            File          = $page.FullName
            Relative      = $relative
            PublishedUrl  = (Get-PublishedUrl -RelativePath $siteRelative)
            Html          = $html
            BlockCount    = $blocks.Count
            BlockLine     = $blockLine
            Json          = $json
            Context       = $context
            GraphIsArray  = $graphIsArray
            Graph         = $graph
            ParseError    = $parseError
            Canonical     = $canonical
            CanonicalLine = $canonicalLine
        }
    }
}

# ----------------------------------------------------------------- checks ---

function Test-JsonLdBlock {
    <#
    .SYNOPSIS
        Check 1: exactly one JSON-LD block per page, parseable, schema.org
        @context, top-level @graph array.
    #>
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][pscustomobject[]]$Page
    )

    foreach ($page in $Page) {
        if ($page.BlockCount -eq 0) {
            New-Finding -Severity 'error' -Path $page.Relative `
                -Message 'no <script type="application/ld+json"> block: the page carries no structured data at all'
            continue
        }
        if ($page.BlockCount -gt 1) {
            New-Finding -Severity 'error' -Path $page.Relative -Line $page.BlockLine `
                -Message ("{0} <script type=""application/ld+json""> blocks: the site convention is one @graph per page, and split graphs cannot reference each other" -f $page.BlockCount)
            continue
        }
        if ($null -ne $page.ParseError) {
            New-Finding -Severity 'error' -Path $page.Relative -Line $page.BlockLine `
                -Message ("JSON-LD does not parse: {0}" -f $page.ParseError)
            continue
        }

        $context = if ($page.Context -is [string]) { $page.Context } else { $null }
        if ($context -ne $SchemaContext) {
            New-Finding -Severity 'error' -Path $page.Relative `
                -Line (Get-TextLine -Text $page.Html -Needle @('"@context"') -Fallback $page.BlockLine) `
                -Message ("@context must be ""{0}"" but is {1}" -f $SchemaContext, (Format-SchemaValue -Value $page.Context))
        }
        if (-not $page.GraphIsArray) {
            New-Finding -Severity 'error' -Path $page.Relative `
                -Line (Get-TextLine -Text $page.Html -Needle @('"@graph"') -Fallback $page.BlockLine) `
                -Message 'JSON-LD has no top-level "@graph" array'
        }
    }
}

function Test-IdReference {
    <#
    .SYNOPSIS
        Check 2: every { "@id": "..." } reference resolves inside its own page.
    #>
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][pscustomobject[]]$Page
    )

    foreach ($page in $Page) {
        if ($null -eq $page.Graph) { continue }

        $entries = @(Get-GraphEntry -Value $page.Graph)
        $declared = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        foreach ($entry in @($entries | Where-Object { $_.Kind -eq 'declaration' })) {
            [void]$declared.Add($entry.Id)
        }

        $reported = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        foreach ($entry in @($entries | Where-Object { $_.Kind -eq 'reference' })) {
            if ($declared.Contains($entry.Id)) { continue }
            if (-not $reported.Add($entry.Id)) { continue }
            New-Finding -Severity 'error' -Path $page.Relative `
                -Line (Get-TextLine -Text $page.Html -Needle @("""@id"": ""$($entry.Id)""", $entry.Id) -Fallback $page.BlockLine) `
                -Message ("dangling reference {{ ""@id"": ""{0}"" }}: this page's @graph never declares that node, so the link resolves to an entity with no type and no name" -f $entry.Id)
        }
    }
}

function Test-EntityConsistency {
    <#
    .SYNOPSIS
        Check 3: nodes sharing an @id across pages agree on every shared property.
    .DESCRIPTION
        The first page that declares an @id (in path order) is the baseline, so
        a disagreement is reported once per property and per offending page
        rather than once per pair of pages.
    #>
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][pscustomobject[]]$Page
    )

    $baseline = [ordered]@{}

    foreach ($page in $Page) {
        if ($null -eq $page.Graph) { continue }

        foreach ($entry in @(Get-GraphEntry -Value $page.Graph | Where-Object { $_.Kind -eq 'declaration' })) {
            if (-not $baseline.Contains($entry.Id)) {
                $baseline[$entry.Id] = [pscustomobject]@{
                    Page = $page
                    Node = $entry.Node
                }
                continue
            }

            $first = $baseline[$entry.Id]
            if ($first.Page.Relative -eq $page.Relative) { continue }

            foreach ($property in @($entry.Node.Keys | Sort-Object -CaseSensitive)) {
                if ($property -eq '@id') { continue }
                if (-not $first.Node.Contains($property)) { continue }

                $mine = ConvertTo-CanonicalText -Value $entry.Node[$property]
                $theirs = ConvertTo-CanonicalText -Value $first.Node[$property]
                if ($mine -ceq $theirs) { continue }

                New-Finding -Severity 'error' -Path $page.Relative `
                    -Line (Get-TextLine -Text $page.Html -Needle @("""$property""") -Fallback $page.BlockLine) `
                    -Message ("node ""{0}"" disagrees with {1} on ""{2}"": {3} here, {4} there. A crawler merges nodes that share an @id, so the two values contradict each other" -f
                        $entry.Id, $first.Page.Relative, $property,
                        (Format-SchemaValue -Value $entry.Node[$property]),
                        (Format-SchemaValue -Value $first.Node[$property]))
            }
        }
    }
}

function Test-PageIdentity {
    <#
    .SYNOPSIS
        Check 4: the WebPage node url, the canonical link and the published path
        are the same URL, and the node carries a dateModified.
    #>
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][pscustomobject[]]$Page
    )

    foreach ($page in $Page) {
        if ($null -eq $page.Graph) { continue }

        $expected = $page.PublishedUrl

        if ($null -eq $page.Canonical) {
            New-Finding -Severity 'error' -Path $page.Relative `
                -Message 'no <link rel="canonical" href="..."> in <head>'
        } elseif ($page.Canonical -cne $expected) {
            New-Finding -Severity 'error' -Path $page.Relative -Line $page.CanonicalLine `
                -Message ("canonical href is ""{0}"" but this file is published at {1}" -f $page.Canonical, $expected)
        }

        $webPages = @($page.Graph | Where-Object { (Get-TypeName -Node $_) -contains 'WebPage' })
        if ($webPages.Count -eq 0) {
            New-Finding -Severity 'error' -Path $page.Relative -Line $page.BlockLine `
                -Message 'the @graph declares no WebPage node, so nothing in it is tied to this URL'
            continue
        }
        if ($webPages.Count -gt 1) {
            New-Finding -Severity 'error' -Path $page.Relative -Line $page.BlockLine `
                -Message ("the @graph declares {0} WebPage nodes: one page is one document, so the page identity is ambiguous" -f $webPages.Count)
            continue
        }

        $webPage = $webPages[0]
        $url = if ($webPage.Contains('url')) { "$($webPage['url'])" } else { $null }
        if ($null -eq $url) {
            New-Finding -Severity 'error' -Path $page.Relative `
                -Line (Get-TextLine -Text $page.Html -Needle @('"WebPage"') -Fallback $page.BlockLine) `
                -Message ("the WebPage node has no ""url"", so it does not claim to be {0}" -f $expected)
        } elseif ($url -cne $expected) {
            New-Finding -Severity 'error' -Path $page.Relative `
                -Line (Get-TextLine -Text $page.Html -Needle @("""url"": ""$url""", '"WebPage"') -Fallback $page.BlockLine) `
                -Message ("the WebPage node url is ""{0}"" but this file is published at {1}" -f $url, $expected)
        }

        $dateModified = if ($webPage.Contains('dateModified')) { "$($webPage['dateModified'])" } else { $null }
        if ($null -eq $dateModified) {
            New-Finding -Severity 'error' -Path $page.Relative `
                -Line (Get-TextLine -Text $page.Html -Needle @('"WebPage"') -Fallback $page.BlockLine) `
                -Message 'the WebPage node has no "dateModified", so the page carries no freshness signal'
        } elseif ($dateModified -notmatch '^\d{4}-\d{2}-\d{2}$') {
            New-Finding -Severity 'error' -Path $page.Relative `
                -Line (Get-TextLine -Text $page.Html -Needle @("""dateModified"": ""$dateModified""", '"dateModified"') -Fallback $page.BlockLine) `
                -Message ("dateModified ""{0}"" is not shaped YYYY-MM-DD" -f $dateModified)
        }
    }
}

function Test-FaqParity {
    <#
    .SYNOPSIS
        Check 5: every FAQPage Question is on the page as a <dt>/<dd> pair with
        the same text, and every dl.faq <dt> is in the graph.
    #>
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][pscustomobject[]]$Page
    )

    foreach ($page in $Page) {
        if ($null -eq $page.Graph) { continue }

        $pairs = @(Get-FaqPair -Html $page.Html)
        $byTerm = @{}
        foreach ($pair in $pairs) {
            if (-not $byTerm.ContainsKey($pair.Term)) { $byTerm[$pair.Term] = $pair }
        }

        $questions = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)

        foreach ($faq in @($page.Graph | Where-Object { (Get-TypeName -Node $_) -contains 'FAQPage' })) {
            $mainEntity = if ($faq.Contains('mainEntity')) { Get-NodeList -Value $faq['mainEntity'] } else { @() }
            foreach ($question in $mainEntity) {
                if (-not ($question -is [System.Collections.IDictionary])) { continue }
                if ((Get-TypeName -Node $question) -notcontains 'Question') { continue }

                $name = if ($question.Contains('name')) { Convert-SchemaText -Value "$($question['name'])" } else { '' }
                $questionLine = Get-TextLine -Text $page.Html -Needle @("""name"": ""$name""", '"FAQPage"') -Fallback $page.BlockLine
                if ($name -eq '') {
                    New-Finding -Severity 'error' -Path $page.Relative -Line $questionLine `
                        -Message 'a FAQPage Question has no "name", so it can never match a visible question'
                    continue
                }
                [void]$questions.Add($name)

                $answer = if ($question.Contains('acceptedAnswer')) { $question['acceptedAnswer'] } else { $null }
                $answerText = $null
                if ($answer -is [System.Collections.IDictionary] -and $answer.Contains('text')) {
                    $answerText = Convert-SchemaText -Value "$($answer['text'])"
                }
                if ($null -eq $answerText -or $answerText -eq '') {
                    New-Finding -Severity 'error' -Path $page.Relative -Line $questionLine `
                        -Message ("Question ""{0}"" has no acceptedAnswer.text" -f $name)
                }

                if (-not $byTerm.ContainsKey($name)) {
                    New-Finding -Severity 'error' -Path $page.Relative -Line $questionLine `
                        -Message ("Question ""{0}"" has no visible <dt> with that text: an answer engine would quote an answer that is not on the page" -f $name)
                    continue
                }

                $pair = $byTerm[$name]
                if (-not $pair.HasDefinition) {
                    New-Finding -Severity 'error' -Path $page.Relative -Line $pair.TermLine `
                        -Message ("the <dt> for ""{0}"" is not followed by a <dd>, so the visible answer is missing" -f $name)
                    continue
                }
                if ($null -ne $answerText -and $answerText -ne '' -and $pair.Definition -cne $answerText) {
                    New-Finding -Severity 'error' -Path $page.Relative -Line $pair.DefinitionLine `
                        -Message ("Question ""{0}"": acceptedAnswer.text does not match the visible <dd>. Schema says ""{1}"". Page says ""{2}""" -f
                            $name, $answerText, $pair.Definition)
                }
            }
        }

        foreach ($pair in @($pairs | Where-Object { $_.IsFaqList })) {
            if ($pair.Term -eq '') { continue }
            if ($questions.Contains($pair.Term)) { continue }
            New-Finding -Severity 'error' -Path $page.Relative -Line $pair.TermLine `
                -Message ("visible question ""{0}"" is missing from the FAQPage graph, so answer engines never see it" -f $pair.Term)
        }
    }
}

function Test-VersionCoherence {
    <#
    .SYNOPSIS
        Check 6: every graph softwareVersion and every version shown in a
        visible link name the same version.
    .DESCRIPTION
        A link contributes a version when its text carries a v-prefixed semver
        token ("Download v1.1.0", "Get Magic Tray v1.1.0"), or names the app or
        a download ahead of a bare one ("Magic Tray 1.1.0" in the header brand,
        "Download 1.1.0"). Those are every wording the site uses. Harvesting
        any three-part number in any link instead would eventually read an OS
        build or a driver version as this app's version.

        The expected version is the most common observation across the whole
        site, ties broken by ordinal order so the report is stable, and every
        other observation is reported against it. That way a half-landed bump
        points at the files still holding the old number, whichever side of the
        site they are on.
    #>
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][pscustomobject[]]$Page
    )

    $observations = [System.Collections.Generic.List[pscustomobject]]::new()

    foreach ($page in $Page) {
        if ($null -ne $page.Graph) {
            foreach ($entry in @(Get-GraphEntry -Value $page.Graph | Where-Object { $_.Kind -eq 'declaration' })) {
                if (-not $entry.Node.Contains('softwareVersion')) { continue }
                $version = "$($entry.Node['softwareVersion'])"
                $observations.Add([pscustomobject]@{
                    Page    = $page
                    Version = $version
                    Line    = (Get-TextLine -Text $page.Html -Needle @("""softwareVersion"": ""$version""", '"softwareVersion"') -Fallback $page.BlockLine)
                    Where   = ("softwareVersion on ""{0}""" -f $entry.Id)
                })
            }
        }

        foreach ($anchor in [regex]::Matches($page.Html, '<a\b[^>]*>(?<text>.*?)</a>', 'Singleline, IgnoreCase')) {
            $text = Convert-HtmlToText -Html $anchor.Groups['text'].Value
            $versionMatch = [regex]::Match($text, '\bv(?<version>\d+\.\d+\.\d+)\b', 'IgnoreCase')
            if (-not $versionMatch.Success -and $text -imatch '\b(?:download|get|magic\s+tray)\b') {
                $versionMatch = [regex]::Match($text, '\b(?<version>\d+\.\d+\.\d+)\b')
            }
            if (-not $versionMatch.Success) { continue }
            $observations.Add([pscustomobject]@{
                Page    = $page
                Version = $versionMatch.Groups['version'].Value
                Line    = (Get-LineNumber -Text $page.Html -Offset $anchor.Index)
                Where   = ("link ""{0}""" -f $text)
            })
        }
    }

    if ($observations.Count -eq 0) { return }

    $expected = @($observations | Group-Object -Property Version |
        Sort-Object -Property @{ Expression = 'Count'; Descending = $true }, @{ Expression = 'Name'; Descending = $false })[0].Name

    foreach ($observation in $observations) {
        if ($observation.Version -ceq $expected) { continue }
        New-Finding -Severity 'error' -Path $observation.Page.Relative -Line $observation.Line `
            -Message ("{0} says version {1} but the rest of the site says {2}: a release bump has only half landed" -f
                $observation.Where, $observation.Version, $expected)
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
$pages = @(Get-JsonLdPage -File $htmlFiles -SiteRoot $siteRoot -RepoRoot $repoRoot)

$findings = [System.Collections.Generic.List[pscustomobject]]::new()
$findings.AddRange([pscustomobject[]]@(Test-JsonLdBlock -Page $pages))
$findings.AddRange([pscustomobject[]]@(Test-IdReference -Page $pages))
$findings.AddRange([pscustomobject[]]@(Test-EntityConsistency -Page $pages))
$findings.AddRange([pscustomobject[]]@(Test-PageIdentity -Page $pages))
$findings.AddRange([pscustomobject[]]@(Test-FaqParity -Page $pages))
$findings.AddRange([pscustomobject[]]@(Test-VersionCoherence -Page $pages))

$errors = @($findings | Where-Object { $_.Severity -eq 'error' })
$warnings = @($findings | Where-Object { $_.Severity -eq 'warning' })

foreach ($finding in $findings) {
    $location = "file=$($finding.Path)"
    if ($finding.Line -gt 0) { $location += ",line=$($finding.Line)" }
    Write-Output "::$($finding.Severity) $location::$($finding.Message)"
}

Write-Output ("Structured data checks: {0} pages, {1} errors, {2} warnings" -f $pages.Count, $errors.Count, $warnings.Count)

if ($errors.Count -gt 0) { exit 1 }
exit 0
