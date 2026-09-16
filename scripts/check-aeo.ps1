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

    It owns what a crawler or an answer engine reads *about* a page: the
    JSON-LD graph, and the head and footer metadata that has to agree with it.
    Link resolution, sitemap membership, robots.txt syntax, CNAME and stale
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

    7. Preview metadata completeness (Test-PreviewMetadata)
       Every indexable page carries the whole og:/twitter: set the site has
       settled on - type, site_name, title, description, url, image, image
       width, height and alt, twitter card, title, description and image - no
       tag is empty, no tag is declared twice, og:url equals the page's own
       canonical, and both images exist in the published tree. Remove this and
       the set drifts the way it already had: six pages carried nine og: tags
       and one carried four, so sharing or quoting those pages produced a bare
       link with no title, no summary and no card - the cheapest impression
       the site can earn, thrown away.

    8. Visible freshness matches the graph (Test-FreshnessLine)
       Every indexable page has exactly one <time datetime="YYYY-MM-DD"> in
       its footer, and that date equals the page's WebPage.dateModified.
       Remove this and the date lives only inside the JSON-LD, which is a
       freshness claim with nothing on the page behind it; worse, the two
       drift apart and the page shows a reader one date while telling a
       crawler another, which is a weaker signal than carrying no date at all.

    9. No Markdown links in HTML (Test-MarkdownLink)
       No href in the site points at a .md file under the site root. Pages
       serves those URLs as text/markdown: no <title>, no canonical, no nav,
       no structured data, nothing that can rank. TESTED.md sat in the sitemap
       as exactly that while being the highest-intent page on the site. A link
       to Markdown on github.com is fine - that is someone else's document,
       not a page of this site.

   10. Picture fallbacks (Test-PictureFallback)
       Every <source srcset> target exists on disk, every <picture> holds
       exactly one <img> fallback, and that <img> carries alt, width and
       height. Remove this and a typo in a .webp name leaves every browser
       that accepts WebP with no image at all while the JPEG sits unused next
       to it, a <picture> with no <img> shows nothing anywhere, and an <img>
       with no dimensions reflows the page as it loads - layout shift is a
       ranking input, not only a nuisance.

   11. noindex pages stay out of the sitemap (Test-NoindexPage)
       A page carrying <meta name="robots" content="...noindex..."> is not
       listed in sitemap.xml and carries no canonical link. Remove this and
       the site submits a URL for crawling that it then tells the crawler to
       throw away, and a canonical on a noindex page aims that noindex at
       another URL, which is how a page that should rank disappears instead.

    A noindex page is exempt from checks 1, 4, 7 and 8. 404.html deliberately
    carries no entity graph, no canonical and nothing worth quoting: demanding
    a graph and a canonical there would force the page to contradict itself.
    It is still held to checks 9, 10 and 11.

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

# The preview set every indexable page carries. A value is the literal every
# page must repeat; $null means the value is the page's own (a title, a
# description, its canonical URL) and only presence and non-emptiness are
# checked here.
$PreviewMeta = [ordered]@{
    'og:type'             = 'website'
    'og:site_name'        = 'Magic Tray'
    'og:title'            = $null
    'og:description'      = $null
    'og:url'              = $null
    'og:image'            = $null
    'og:image:width'      = '1200'
    'og:image:height'     = '630'
    'og:image:alt'        = $null
    'twitter:card'        = 'summary_large_image'
    'twitter:title'       = $null
    'twitter:description' = $null
    'twitter:image'       = $null
}

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

function Get-MetaTag {
    <#
    .SYNOPSIS
        Every <meta> tag of a page as Key/Value/Line records.
    .DESCRIPTION
        Open Graph keys arrive on property=, Twitter and robots keys on name=,
        and either spelling is legal for either, so whichever attribute is
        present becomes the key and callers can ask for "og:title" without
        caring how it was written. Values are entity-decoded, because &amp; in
        a title is the same character to a crawler as &, and trimmed, because
        a content attribute holding only spaces is an empty tag with extra
        steps.
    #>
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Html
    )

    foreach ($tag in [regex]::Matches($Html, '<meta\b[^>]*>', 'IgnoreCase')) {
        $keyMatch = [regex]::Match($tag.Value, '\b(?:property|name)\s*=\s*["''](?<key>[^"'']*)["'']', 'IgnoreCase')
        if (-not $keyMatch.Success) { continue }
        $contentMatch = [regex]::Match($tag.Value, '\bcontent\s*=\s*["''](?<content>[^"'']*)["'']', 'IgnoreCase')
        [pscustomobject]@{
            Key      = $keyMatch.Groups['key'].Value.Trim().ToLowerInvariant()
            HasValue = $contentMatch.Success
            Value    = if ($contentMatch.Success) {
                ([System.Net.WebUtility]::HtmlDecode($contentMatch.Groups['content'].Value)).Trim()
            } else {
                ''
            }
            Line     = (Get-LineNumber -Text $Html -Offset $tag.Index)
        }
    }
}

function Resolve-SiteReference {
    <#
    .SYNOPSIS
        The file on disk a reference names, or $null when it is off-site.
    .DESCRIPTION
        An absolute URL on this origin and a root-relative path both resolve
        against the site root; anything else resolves against the directory of
        the page that wrote it, which is what a browser does. Fragments and
        query strings are cut first: og.png?v=2 is still og.png on disk.

        $null means "this script cannot check it" - another origin, mailto:,
        data: - so a caller can tell an unverifiable reference apart from a
        missing file instead of reporting every external URL as broken.
    #>
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Reference,
        [Parameter(Mandatory)][string]$PageDirectory,
        [Parameter(Mandatory)][string]$SiteRoot
    )

    $value = ($Reference.Trim() -split '[#?]')[0]
    if ($value -eq '') { return $null }

    if ($value.StartsWith("$SiteOrigin/", [System.StringComparison]::OrdinalIgnoreCase)) {
        $value = $value.Substring("$SiteOrigin/".Length)
    } elseif ($value -match '^(?:[a-zA-Z][a-zA-Z0-9+.\-]*:|//)') {
        return $null
    } elseif ($value.StartsWith('/')) {
        $value = $value.TrimStart('/')
    } else {
        return [System.IO.Path]::GetFullPath((Join-Path $PageDirectory $value))
    }

    if ($value -eq '') { return $null }
    return [System.IO.Path]::GetFullPath((Join-Path $SiteRoot $value))
}

function Test-PathUnderRoot {
    <#
    .SYNOPSIS
        True when a resolved full path lies inside the published tree.
    #>
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$FullPath,
        [Parameter(Mandatory)][string]$SiteRoot
    )

    if ([string]::IsNullOrEmpty($FullPath)) { return $false }
    $root = [System.IO.Path]::GetFullPath($SiteRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    return $FullPath.StartsWith($root + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::Ordinal)
}

function Get-FooterTime {
    <#
    .SYNOPSIS
        Every <time> element inside a page's <footer>, with line numbers.
    .DESCRIPTION
        Only the footer is searched. A <time> in the body is prose - a release
        date, a measurement - and must not be mistaken for the page's own
        "last updated" stamp, which the site writes in exactly one place.
    #>
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Html
    )

    foreach ($footer in [regex]::Matches($Html, '<footer\b[^>]*>(?<body>.*?)</footer>', 'Singleline, IgnoreCase')) {
        $body = $footer.Groups['body'].Value
        $bodyOffset = $footer.Groups['body'].Index
        foreach ($element in [regex]::Matches($body, '<time\b[^>]*>', 'IgnoreCase')) {
            $attribute = [regex]::Match($element.Value, '\bdatetime\s*=\s*["''](?<value>[^"'']*)["'']', 'IgnoreCase')
            [pscustomobject]@{
                HasDateTime = $attribute.Success
                Value       = if ($attribute.Success) { $attribute.Groups['value'].Value.Trim() } else { '' }
                Line        = (Get-LineNumber -Text $Html -Offset ($bodyOffset + $element.Index))
            }
        }
    }
}

function Get-SitemapLocation {
    <#
    .SYNOPSIS
        The <loc> URLs of the site's sitemap.xml, with line numbers.
    .DESCRIPTION
        Emits nothing when there is no sitemap: whether the file must exist is
        scripts/check-site.ps1's question, not this one's. This script only
        asks whether a URL it finds there contradicts the page it names.
    #>
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][string]$SiteRoot
    )

    $path = Join-Path $SiteRoot 'sitemap.xml'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return }
    $text = Get-Content -LiteralPath $path -Raw
    if ([string]::IsNullOrEmpty($text)) { return }

    foreach ($match in [regex]::Matches($text, '<loc>(?<loc>[^<]*)</loc>', 'IgnoreCase')) {
        [pscustomobject]@{
            Url  = $match.Groups['loc'].Value.Trim()
            Line = (Get-LineNumber -Text $text -Offset $match.Index)
        }
    }
}

function Get-WebPageNode {
    <#
    .SYNOPSIS
        The page's single WebPage node, or $null when it has none or several.
    .DESCRIPTION
        Ambiguity is Test-PageIdentity's finding to report, so this returns
        $null rather than guessing and lets the caller stay silent about a
        page that is already being reported.
    #>
    [OutputType([System.Collections.IDictionary])]
    param(
        [Parameter(Mandatory)][AllowNull()]$Graph
    )

    if ($null -eq $Graph) { return $null }
    $nodes = @($Graph | Where-Object { (Get-TypeName -Node $_) -contains 'WebPage' })
    if ($nodes.Count -ne 1) { return $null }
    return $nodes[0]
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

        # A page is indexable unless a robots meta tag says noindex. Several
        # robots tags are merged the way a crawler merges them: the most
        # restrictive directive on the page wins.
        $meta = @(Get-MetaTag -Html $html)
        $isIndexable = $true
        $robotsLine = 0
        foreach ($tag in @($meta | Where-Object { $_.Key -eq 'robots' })) {
            if ($robotsLine -eq 0) { $robotsLine = $tag.Line }
            if (($tag.Value -split '[,\s]+') -contains 'noindex') {
                $isIndexable = $false
                $robotsLine = $tag.Line
            }
        }

        [pscustomobject]@{
            File          = $page.FullName
            Directory     = $page.DirectoryName
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
            Meta          = $meta
            IsIndexable   = $isIndexable
            RobotsLine    = $robotsLine
        }
    }
}

# ----------------------------------------------------------------- checks ---

function Test-JsonLdBlock {
    <#
    .SYNOPSIS
        Check 1: exactly one JSON-LD block per page, parseable, schema.org
        @context, top-level @graph array.
    .DESCRIPTION
        A noindex page is allowed to carry no block at all - 404.html has no
        entity to declare and no URL a crawler should keep - but a block it
        does carry still has to be well formed, or the page ships broken JSON
        that a crawler reports against the whole site.
    #>
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][pscustomobject[]]$Page
    )

    foreach ($page in $Page) {
        if ($page.BlockCount -eq 0) {
            if (-not $page.IsIndexable) { continue }
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
    .DESCRIPTION
        Skipped for a noindex page: it must not claim a canonical at all, and
        Test-NoindexPage owns that rule. Asking a page to name the URL it wants
        indexed while it is telling crawlers to index nothing is a demand it
        can only satisfy by contradicting itself.
    #>
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][pscustomobject[]]$Page
    )

    foreach ($page in $Page) {
        if ($null -eq $page.Graph -or -not $page.IsIndexable) { continue }

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

function Test-PreviewMetadata {
    <#
    .SYNOPSIS
        Check 7: every indexable page carries the whole og:/twitter: preview
        set, with non-empty values, an og:url equal to its canonical, and
        images that exist in the published tree.
    .DESCRIPTION
        The required set lives in $PreviewMeta. A tag with a literal there must
        repeat it exactly, because those four values (og:type, og:site_name and
        the image dimensions) describe the site rather than the page and a page
        that disagrees is simply wrong about where it lives. The rest only have
        to be present, non-empty and, for og:url, equal to the canonical: a
        preview card that names a different URL than the page claims as its own
        splits the shares between two addresses.

        Duplicates are reported rather than merged. Two og:title tags do not
        average; the consumer picks one, and which one is not something the
        site gets to decide.
    #>
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][pscustomobject[]]$Page,
        [Parameter(Mandatory)][string]$SiteRoot
    )

    foreach ($page in $Page) {
        if (-not $page.IsIndexable) { continue }

        $byKey = @{}
        foreach ($tag in @($page.Meta)) {
            if ($tag.Key -notlike 'og:*' -and $tag.Key -notlike 'twitter:*') { continue }
            if ($byKey.ContainsKey($tag.Key)) {
                New-Finding -Severity 'error' -Path $page.Relative -Line $tag.Line `
                    -Message ("""{0}"" is declared twice: a consumer keeps one of the two values and the page has no say in which" -f $tag.Key)
                continue
            }
            $byKey[$tag.Key] = $tag
            if ($tag.Value -eq '') {
                New-Finding -Severity 'error' -Path $page.Relative -Line $tag.Line `
                    -Message ("""{0}"" has an empty content attribute: an empty tag is worse than a missing one, because a consumer stops looking for a fallback" -f $tag.Key)
            }
        }

        $headLine = if ($page.CanonicalLine -gt 0) { $page.CanonicalLine } else { 1 }

        foreach ($key in @($PreviewMeta.Keys)) {
            if (-not $byKey.ContainsKey($key)) {
                New-Finding -Severity 'error' -Path $page.Relative -Line $headLine `
                    -Message ("<head> has no ""{0}"" tag: every page on this site carries the same preview set, and a page missing part of it is shared and quoted as a bare link with no card" -f $key)
                continue
            }

            $expected = $PreviewMeta[$key]
            if ($null -eq $expected) { continue }
            $actual = $byKey[$key].Value
            if ($actual -cne $expected) {
                New-Finding -Severity 'error' -Path $page.Relative -Line $byKey[$key].Line `
                    -Message ("""{0}"" is ""{1}"" but every page on this site says ""{2}"": that value describes the site, not the page" -f $key, $actual, $expected)
            }
        }

        if ($byKey.ContainsKey('og:url') -and $null -ne $page.Canonical -and $byKey['og:url'].Value -cne $page.Canonical) {
            New-Finding -Severity 'error' -Path $page.Relative -Line $byKey['og:url'].Line `
                -Message ("og:url is ""{0}"" but the canonical link is ""{1}"": shares of this page would accumulate against a URL the page itself does not claim" -f
                    $byKey['og:url'].Value, $page.Canonical)
        }

        foreach ($key in @('og:image', 'twitter:image')) {
            if (-not $byKey.ContainsKey($key)) { continue }
            $tag = $byKey[$key]
            if ($tag.Value -eq '') { continue }

            $resolved = Resolve-SiteReference -Reference $tag.Value -PageDirectory $page.Directory -SiteRoot $SiteRoot
            if ($null -eq $resolved) {
                New-Finding -Severity 'error' -Path $page.Relative -Line $tag.Line `
                    -Message ("""{0}"" points at ""{1}"", which is not served from this site, so nothing here can tell whether the preview image still exists" -f $key, $tag.Value)
                continue
            }
            if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
                New-Finding -Severity 'error' -Path $page.Relative -Line $tag.Line `
                    -Message ("""{0}"" names ""{1}"", which is not in the published tree: the card renders blank wherever the page is shared" -f $key, $tag.Value)
            }
        }
    }
}

function Test-FreshnessLine {
    <#
    .SYNOPSIS
        Check 8: one <time datetime="YYYY-MM-DD"> in the footer of every
        indexable page, equal to that page's WebPage.dateModified.
    .DESCRIPTION
        A missing dateModified is Test-PageIdentity's finding, so a page
        without one is only asked for a well-formed visible date here; it is
        not reported twice for the same defect.
    #>
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][pscustomobject[]]$Page
    )

    foreach ($page in $Page) {
        if (-not $page.IsIndexable) { continue }

        $webPage = Get-WebPageNode -Graph $page.Graph
        $dateModified = $null
        if ($null -ne $webPage -and $webPage.Contains('dateModified')) {
            $dateModified = "$($webPage['dateModified'])"
        }

        $times = @(Get-FooterTime -Html $page.Html)
        if ($times.Count -eq 0) {
            New-Finding -Severity 'error' -Path $page.Relative `
                -Line (Get-TextLine -Text $page.Html -Needle @('<footer') -Fallback 1) `
                -Message 'the footer carries no <time datetime="YYYY-MM-DD">: the page''s only freshness signal is inside the JSON-LD, which is a claim with nothing visible behind it'
            continue
        }
        if ($times.Count -gt 1) {
            New-Finding -Severity 'error' -Path $page.Relative -Line $times[1].Line `
                -Message ("the footer carries {0} <time> elements: the page states more than one last-updated date and a reader cannot tell which one is the page's" -f $times.Count)
            continue
        }

        $time = $times[0]
        if (-not $time.HasDateTime) {
            New-Finding -Severity 'error' -Path $page.Relative -Line $time.Line `
                -Message 'the footer <time> has no datetime attribute, so the date is prose a crawler cannot read'
            continue
        }
        if ($time.Value -notmatch '^\d{4}-\d{2}-\d{2}$') {
            New-Finding -Severity 'error' -Path $page.Relative -Line $time.Line `
                -Message ("footer <time datetime=""{0}""> is not shaped YYYY-MM-DD" -f $time.Value)
            continue
        }
        if ($null -ne $dateModified -and $time.Value -cne $dateModified) {
            New-Finding -Severity 'error' -Path $page.Relative -Line $time.Line `
                -Message ("the footer shows {0} but the WebPage node says dateModified {1}: the page tells a reader one date and a crawler another" -f $time.Value, $dateModified)
        }
    }
}

function Test-MarkdownLink {
    <#
    .SYNOPSIS
        Check 9: no href in the site points at a .md file under the site root.
    .DESCRIPTION
        Only references that resolve inside the published tree are reported.
        A Markdown file on github.com is someone else's document served by
        someone else's renderer; a Markdown file here is served as
        text/markdown with no title, no canonical and no navigation, so a link
        to it hands a reader - and a crawler following it - a dead end that
        cannot rank.
    #>
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][pscustomobject[]]$Page,
        [Parameter(Mandatory)][string]$SiteRoot
    )

    foreach ($page in $Page) {
        foreach ($match in [regex]::Matches($page.Html, '\bhref\s*=\s*["''](?<href>[^"'']*)["'']', 'IgnoreCase')) {
            $href = $match.Groups['href'].Value.Trim()
            $target = ($href -split '[#?]')[0]
            if (-not $target.EndsWith('.md', [System.StringComparison]::OrdinalIgnoreCase)) { continue }

            $resolved = Resolve-SiteReference -Reference $href -PageDirectory $page.Directory -SiteRoot $SiteRoot
            if (-not (Test-PathUnderRoot -FullPath $resolved -SiteRoot $SiteRoot)) { continue }

            New-Finding -Severity 'error' -Path $page.Relative `
                -Line (Get-LineNumber -Text $page.Html -Offset $match.Index) `
                -Message ("href=""{0}"" points at a Markdown file in the published site: Pages serves it as text/markdown, so it has no title, no canonical and no nav and can never rank. Link the HTML page, or the file's GitHub blob URL" -f $href)
        }
    }
}

function Test-PictureFallback {
    <#
    .SYNOPSIS
        Check 10: every <source srcset> target exists, every <picture> has one
        <img> fallback, and that fallback carries alt, width and height.
    .DESCRIPTION
        A <source> is chosen before the <img> is ever consulted, so a missing
        WebP is not a graceful degradation: the browsers that accept WebP -
        most of them - get nothing while the JPEG sits unused beside it. The
        dimensions are checked on the fallback because that is the element
        that reserves the box; without them the page reflows as each photo
        arrives, and layout shift is scored, not merely noticed.
    #>
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][pscustomobject[]]$Page,
        [Parameter(Mandatory)][string]$SiteRoot
    )

    foreach ($page in $Page) {
        foreach ($source in [regex]::Matches($page.Html, '<source\b[^>]*>', 'IgnoreCase')) {
            $srcset = [regex]::Match($source.Value, '\bsrcset\s*=\s*["''](?<value>[^"'']*)["'']', 'IgnoreCase')
            if (-not $srcset.Success) { continue }
            $sourceLine = Get-LineNumber -Text $page.Html -Offset $source.Index

            foreach ($candidate in ($srcset.Groups['value'].Value -split ',')) {
                # A srcset entry is a URL plus an optional 2x or 800w descriptor.
                $reference = @($candidate.Trim() -split '\s+')[0]
                if ($reference -eq '') { continue }

                $resolved = Resolve-SiteReference -Reference $reference -PageDirectory $page.Directory -SiteRoot $SiteRoot
                if ($null -eq $resolved) { continue }
                if (Test-Path -LiteralPath $resolved -PathType Leaf) { continue }

                New-Finding -Severity 'error' -Path $page.Relative -Line $sourceLine `
                    -Message ("<source srcset> names ""{0}"", which is not in the published tree: a browser that picks this source gets no image at all, and the fallback next to it is never tried" -f $reference)
            }
        }

        foreach ($picture in [regex]::Matches($page.Html, '<picture\b[^>]*>(?<body>.*?)</picture>', 'Singleline, IgnoreCase')) {
            $pictureLine = Get-LineNumber -Text $page.Html -Offset $picture.Index
            $body = $picture.Groups['body'].Value
            $images = @([regex]::Matches($body, '<img\b[^>]*>', 'IgnoreCase'))

            if ($images.Count -ne 1) {
                New-Finding -Severity 'error' -Path $page.Relative -Line $pictureLine `
                    -Message ("<picture> contains {0} <img> elements: it needs exactly one fallback, or browsers that take none of the sources show nothing" -f $images.Count)
                continue
            }

            $image = $images[0].Value
            $imageLine = Get-LineNumber -Text $page.Html -Offset ($picture.Groups['body'].Index + $images[0].Index)

            $alt = [regex]::Match($image, '\balt\s*=\s*["''](?<value>[^"'']*)["'']', 'IgnoreCase')
            if (-not $alt.Success -or $alt.Groups['value'].Value.Trim() -eq '') {
                New-Finding -Severity 'error' -Path $page.Relative -Line $imageLine `
                    -Message 'the <img> fallback inside <picture> has no alt text: every photograph here carries information a reader who cannot see it still needs, and it is the only description of the image a crawler ever gets'
            }

            foreach ($attribute in @('width', 'height')) {
                $found = [regex]::Match($image, ('\b{0}\s*=\s*["''](?<value>[^"'']*)["'']' -f $attribute), 'IgnoreCase')
                $value = if ($found.Success) { $found.Groups['value'].Value.Trim() } else { '' }

                if ($value -eq '') {
                    New-Finding -Severity 'error' -Path $page.Relative -Line $imageLine `
                        -Message ("the <img> fallback inside <picture> has no {0}: the browser reserves no box for it and the page jumps as the photograph arrives, which is layout shift and is scored" -f $attribute)
                    continue
                }
                if ($value -notmatch '^[1-9]\d*$') {
                    New-Finding -Severity 'error' -Path $page.Relative -Line $imageLine `
                        -Message ("the <img> fallback inside <picture> has {0}=""{1}"": the attribute must be a pixel count, or the browser reserves no box and the page shifts as the image arrives" -f $attribute, $value)
                }
            }
        }
    }
}

function Test-NoindexPage {
    <#
    .SYNOPSIS
        Check 11: a noindex page is absent from sitemap.xml and carries no
        canonical link.
    .DESCRIPTION
        Whether the sitemap lists every indexable page is
        scripts/check-site.ps1's question. This one is the opposite direction
        and only this script can answer it, because only this script reads the
        page's robots meta: a URL submitted for crawling that then refuses to
        be indexed spends crawl budget to produce nothing, and a canonical on
        such a page aims the noindex at whatever URL it names.
    #>
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][pscustomobject[]]$Page,
        [Parameter(Mandatory)][string]$SiteRoot,
        [Parameter(Mandatory)][string]$RepoRoot
    )

    $locations = @(Get-SitemapLocation -SiteRoot $SiteRoot)
    $sitemapPath = Get-RepoRelativePath -FullPath (Join-Path $SiteRoot 'sitemap.xml') -RepoRoot $RepoRoot

    foreach ($page in $Page) {
        if ($page.IsIndexable) { continue }

        foreach ($location in @($locations | Where-Object { $_.Url -ceq $page.PublishedUrl })) {
            New-Finding -Severity 'error' -Path $sitemapPath -Line $location.Line `
                -Message ("sitemap.xml submits {0} for crawling, but {1} carries meta robots noindex: the site asks a crawler to fetch a page it is then told to drop" -f
                    $location.Url, $page.Relative)
        }

        if ($null -ne $page.Canonical) {
            New-Finding -Severity 'error' -Path $page.Relative -Line $page.CanonicalLine `
                -Message ("a noindex page must not carry <link rel=""canonical"" href=""{0}"">: canonical says ""index that URL instead"" while robots says ""index nothing"", and the conflict is resolved by the crawler, sometimes by carrying the noindex over to the canonical target" -f $page.Canonical)
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
$pages = @(Get-JsonLdPage -File $htmlFiles -SiteRoot $siteRoot -RepoRoot $repoRoot)

$findings = [System.Collections.Generic.List[pscustomobject]]::new()
$findings.AddRange([pscustomobject[]]@(Test-JsonLdBlock -Page $pages))
$findings.AddRange([pscustomobject[]]@(Test-IdReference -Page $pages))
$findings.AddRange([pscustomobject[]]@(Test-EntityConsistency -Page $pages))
$findings.AddRange([pscustomobject[]]@(Test-PageIdentity -Page $pages))
$findings.AddRange([pscustomobject[]]@(Test-FaqParity -Page $pages))
$findings.AddRange([pscustomobject[]]@(Test-VersionCoherence -Page $pages))
$findings.AddRange([pscustomobject[]]@(Test-PreviewMetadata -Page $pages -SiteRoot $siteRoot))
$findings.AddRange([pscustomobject[]]@(Test-FreshnessLine -Page $pages))
$findings.AddRange([pscustomobject[]]@(Test-MarkdownLink -Page $pages -SiteRoot $siteRoot))
$findings.AddRange([pscustomobject[]]@(Test-PictureFallback -Page $pages -SiteRoot $siteRoot))
$findings.AddRange([pscustomobject[]]@(Test-NoindexPage -Page $pages -SiteRoot $siteRoot -RepoRoot $repoRoot))

$errors = @($findings | Where-Object { $_.Severity -eq 'error' })
$warnings = @($findings | Where-Object { $_.Severity -eq 'warning' })

foreach ($finding in $findings) {
    $location = "file=$($finding.Path)"
    if ($finding.Line -gt 0) { $location += ",line=$($finding.Line)" }
    Write-Output "::$($finding.Severity) $location::$($finding.Message)"
}

Write-Output ("Answer-engine checks: {0} pages, {1} errors, {2} warnings" -f $pages.Count, $errors.Count, $warnings.Count)

if ($errors.Count -gt 0) { exit 1 }
exit 0
