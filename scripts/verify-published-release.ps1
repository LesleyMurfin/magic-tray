#Requires -Version 7

<#
.SYNOPSIS
    Verifies a Magic Tray release AS PUBLISHED, by asking the GitHub API what
    is actually attached to the tag and downloading it from the real asset
    URLs.

.DESCRIPTION
    WHY THIS EXISTS - the v1.1.0 failure.

    scripts/verify-release.ps1 gates the LOCAL artefacts: the publish/ folder
    and the dist/ ZIP, before anything is uploaded. That check is necessary and
    it is not what this script duplicates. It is structurally incapable of
    catching the one thing that went wrong with v1.1.0:

      * v1.1.0 was tagged on 2026-09-02 and the release workflow reported
        SUCCESS.
      * The workflow revision that packages MagicTray-<tag>-win-x64.zip, writes
        the .sha256 sidecar, writes SHA256SUMS and puts a digest table in the
        notes was not committed until 2026-09-16.
      * So the run that "succeeded" was the OLD workflow, and it published
        three assets: a bare 186 MB MagicMouseTray.exe, Install-KeyboardBattery
        .cmd and kbd-patch-cachedservices.ps1. No ZIP. No checksum of any kind.
        Release notes were a bare pull-request list.

    Nothing in the run ever looked at the release it had just created, so
    nothing failed. The damage outlived the run: the winget installer manifest
    still points its InstallerUrl at MagicTray-v1.1.0-win-x64.zip, an asset
    that does not exist, with 64 zeros for its InstallerSha256; and
    https://magictray.app/install.txt tells an AI agent to verify a SHA-256
    against a published checksum that was never published.

    The general rule that follows: a release workflow that only inspects local
    files can succeed while publishing the wrong asset set, because the thing
    it verified and the thing it shipped are two different objects. This script
    verifies the shipped object. It runs AFTER `gh release create`, on the
    published release, and it is wired into .github/workflows/release.yml as a
    hard gate. The release is already public by then, which is the point: a
    failure here is loud, arrives within a couple of minutes, and names the
    exact asset that is missing, while a human is still watching the run and
    can delete the tag and the release.

    It fails, non-zero, when any of these is not true:

      1. The release for -Tag exists and is not a draft.
      2. Its assets include at least MagicTray-<tag>-win-x64.zip, that ZIP's
         .sha256 sidecar, SHA256SUMS and MagicMouseTray.exe, all fully
         uploaded. On failure it prints the complete actual asset list, because
         that list is what tells you whether a stale workflow ran, an upload
         was dropped, or a name drifted.
      3. The SHA-256 computed over the downloaded ZIP equals the digest in the
         downloaded .sha256 sidecar (compared ignoring case: Get-FileHash
         prints upper-case, our generator writes lower-case).
      4. SHA256SUMS parses as `<64 hex>  <filename>` lines, and every file name
         it lists is really attached to the release. A checksum file naming an
         asset nobody can download is the same class of bug as v1.1.0's
         manifest pointing at a ZIP nobody can download.
      5. The SHA-256 computed over the downloaded MagicMouseTray.exe equals the
         digest SHA256SUMS gives for it. docs/install.txt, step 2, tells an AI
         agent to hash that exe and to STOP if it disagrees with the published
         checksum, so a wrong line there is not a cosmetic defect: it turns a
         correct download into a refusal on every agent that follows the
         instructions we publish.
      6. The release body quotes the ZIP's digest. This is the check that
         catches v1.1.0 exactly: its notes were an auto-generated PR list, so
         the "compare it with the line below" instructions the site and
         install.txt give a visitor had nothing to compare against.

    None of that is worth saying when the network merely stumbled. Both network
    paths - the gh api call and every asset download - retry a 408, a 429, a
    5xx or a connection that never produced a response, four attempts with 2s,
    4s and 8s between them. Everything else, a 404 and a 401 and a 403 and a
    digest that does not match, fails the moment it is seen.

.PARAMETER Tag
    Release tag to verify, including the leading v. Defaults to
    GITHUB_REF_NAME, the tag that triggered the release workflow.

.PARAMETER Repo
    OWNER/NAME to query. Defaults to the canonical repository; release.yml
    passes GITHUB_REPOSITORY so a fork verifies its own release.

.EXAMPLE
    pwsh -File scripts/verify-published-release.ps1 -Tag v1.1.1

.EXAMPLE
    pwsh -File scripts/verify-published-release.ps1 -Tag v1.1.0 -Repo LesleyMurfin/magic-tray
#>

[CmdletBinding()]
param(
    [Parameter()]
    [string] $Tag = $env:GITHUB_REF_NAME,

    [Parameter()]
    [string] $Repo = 'LesleyMurfin/magic-tray'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ProblemCount = 0
$script:ProblemLines = @()
# Which checks failed, by -Name, because the remedy differs sharply: a wrong
# asset set can only be fixed by re-cutting, whereas notes that are the sole
# failure can be rewritten in place. See the summary block at the end.
$script:FailedCheck = @()

function Write-Check {
    [CmdletBinding()]
    [OutputType([void])]
    param(
        [Parameter(Mandatory)] [bool]   $Ok,
        [Parameter(Mandatory)] [string] $Name,
        [Parameter()]          [string] $Detail = ''
    )

    $text = if ($Detail) { "${Name}: $Detail" } else { $Name }
    if ($Ok) {
        Write-Host "PASS  $text"
        return
    }

    $script:ProblemCount++
    $script:ProblemLines += $text
    $script:FailedCheck += $Name
    # ::error:: puts the message in the Actions run summary and on the job, not
    # only in the log, so a post-publish failure is visible without opening the
    # step. Write-Error would abort under ErrorActionPreference = Stop and hide
    # every remaining check; the first missing asset is rarely the whole story.
    Write-Host "FAIL  $text"
    Write-Host ('::error::{0}' -f $text)
}

# Statuses that mean "ask again shortly", not "this release is wrong". A 408,
# a 429 or any 5xx from api.github.com or from objects.githubusercontent.com is
# the far end having a bad minute; the bytes it is refusing to serve right now
# are the same bytes it served a moment ago. Everything this script does on a
# failure - a failed check, exit 1, and a summary whose remedy for a wrong asset
# set or a bad digest is "delete the release AND the tag and re-cut" - is
# irreversible and aimed at a release that is already public. So the two places
# that talk to the network retry on these, and on a connection that never
# produced a response at all, and on nothing else: a 404, a 401, a 403, a digest
# that does not match or a file that is not the one named all fail on the first
# attempt, because retrying a real defect only delays the truth.
# Four attempts with 2s, 4s and 8s between them costs 14 seconds on a release
# that is genuinely broken and saves a correct one from being deleted over a
# transport fault.
$script:RetryableStatus = @(408, 429, 500, 502, 503, 504)

# gh writes its diagnostics (and a 404 body) to stderr. Redirecting that stream
# to a file keeps ErrorActionPreference = Stop from turning a plain HTTP error
# into a thrown exception, so the failure can be reported as a check with the
# API's own words attached.
#
# It retries for the same reason the download does: this is the call that
# decides whether the release exists at all, and api.github.com serves a 502 or
# a 503 for a few seconds often enough to matter. A 403 is deliberately not
# retryable even though rate limiting wears that number: that limit resets on
# the hour, so three doublings would change nothing except how long the run
# takes to report the same thing.
function Invoke-GhApi {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [string] $ApiPath,
        [Parameter(Mandatory)] [string] $StderrPath,
        [Parameter()]          [int]    $MaxAttempts = 4
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $global:LASTEXITCODE = 0
        $out = & gh api $ApiPath --header 'Accept: application/vnd.github+json' 2> $StderrPath
        if ($LASTEXITCODE -eq 0) { return (@($out) -join "`n") }

        $detail = ''
        if (Test-Path -LiteralPath $StderrPath -PathType Leaf) {
            $detail = (Get-Content -LiteralPath $StderrPath -Raw).Trim()
        }
        $failure = "gh api $ApiPath exited $LASTEXITCODE. $detail"

        # gh prints the status as '(HTTP 502)'. With no status at all the
        # request never became a response, and gh passes Go's own transport
        # wording through - that is a network fault and is retried. No status
        # and no transport wording is a local problem (bad arguments, no
        # credentials) which would fail identically four times over.
        $status = 0
        $matched = [regex]::Match($detail, '\(HTTP (\d{3})\)')
        if ($matched.Success) { $status = [int]$matched.Groups[1].Value }
        $mayRetry =
            if ($status -ne 0) { $script:RetryableStatus -contains $status }
            else { $detail -match 'dial tcp|no such host|connection reset|connection refused|i/o timeout|TLS handshake|EOF' }

        if (-not $mayRetry -or $attempt -eq $MaxAttempts) {
            throw $(if ($mayRetry) { "$failure (still failing after $MaxAttempts attempts)" } else { $failure })
        }
        $wait = [int][math]::Pow(2, $attempt)
        Write-Host ('RETRY {0}; attempt {1} of {2}, waiting {3}s' -f $failure, $attempt, $MaxAttempts, $wait)
        Start-Sleep -Seconds $wait
    }
}

# Download to disk without ever holding the body in memory: the ZIP is ~190 MB
# and the loose exe is larger still, so the response is read headers-first and
# the content stream is copied straight into a FileStream.
#
# A large download is the likeliest thing here to meet a transient fault, and
# the failure it produces is indistinguishable, to the caller, from an asset
# that is genuinely wrong - which is why it is this function that must tell the
# two apart. See $script:RetryableStatus above for what counts as which.
function Save-AssetFile {
    [CmdletBinding()]
    [OutputType([long])]
    param(
        [Parameter(Mandatory)] [string] $Url,
        [Parameter(Mandatory)] [string] $Path,
        [Parameter()]          [int]    $MaxAttempts = 4
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $failure = ''
        $mayRetry = $false
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $Url)
        try {
            # A token is only needed for a private repository. .NET drops the
            # Authorization header when it follows a redirect to another origin,
            # so sending it here neither leaks the token to the asset CDN nor
            # breaks the pre-signed URL that the CDN redirect hands back.
            if ($env:GH_TOKEN) {
                $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $env:GH_TOKEN)
            }
            $response = $null
            try {
                $response = $script:HttpClient.SendAsync(
                    $request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            } catch {
                # No status line came back at all: DNS, a refused or reset
                # connection, the 15-minute ceiling. Transport by definition,
                # so it says nothing about the release and is worth asking again.
                $failure = 'GET {0} failed with no response: {1}' -f $Url, $_.Exception.Message
                $mayRetry = $true
            }
            if ($null -ne $response) {
                try {
                    if (-not $response.IsSuccessStatusCode) {
                        $status = [int]$response.StatusCode
                        $failure = 'GET {0} returned {1} {2}' -f $Url, $status, $response.ReasonPhrase
                        $mayRetry = $script:RetryableStatus -contains $status
                    } else {
                        $source = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                        try {
                            $target = [IO.File]::Create($Path)
                            try {
                                $source.CopyTo($target, 1048576)
                            } finally {
                                $target.Dispose()
                            }
                        } finally {
                            $source.Dispose()
                        }
                        return (Get-Item -LiteralPath $Path).Length
                    }
                } catch {
                    # Headers were fine and the body came apart underneath the
                    # copy. Half a ZIP hashes to a digest nobody published, so
                    # the bytes are fetched again rather than hashed and blamed
                    # on the release. The next attempt truncates $Path.
                    $failure = 'GET {0} broke off mid-body: {1}' -f $Url, $_.Exception.Message
                    $mayRetry = $true
                } finally {
                    $response.Dispose()
                }
            }
        } finally {
            $request.Dispose()
        }

        if (-not $mayRetry -or $attempt -eq $MaxAttempts) {
            throw $(if ($mayRetry) { "$failure (still failing after $MaxAttempts attempts)" } else { $failure })
        }
        $wait = [int][math]::Pow(2, $attempt)
        Write-Host ('RETRY {0}; attempt {1} of {2}, waiting {3}s' -f $failure, $attempt, $MaxAttempts, $wait)
        Start-Sleep -Seconds $wait
    }
}

if ([string]::IsNullOrWhiteSpace($Tag)) {
    Write-Host '::error::No -Tag given and GITHUB_REF_NAME is empty. Nothing to verify.'
    exit 1
}
$Tag = $Tag.Trim()
# The tag is interpolated into an API path and into an asset file name, so it
# is constrained to what a git tag for this project can legitimately contain
# rather than passed through unexamined.
if ($Tag -notmatch '^v[0-9A-Za-z][0-9A-Za-z.+-]*$') {
    Write-Host ('::error::Tag ''{0}'' does not look like a Magic Tray release tag (v1.1.1, v1.2.0-rc1).' -f $Tag)
    exit 1
}
if ($Repo -notmatch '^[0-9A-Za-z._-]+/[0-9A-Za-z._-]+$') {
    Write-Host ('::error::Repo ''{0}'' is not OWNER/NAME.' -f $Repo)
    exit 1
}
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Host '::error::The gh CLI is not on PATH. It is preinstalled on GitHub-hosted runners; install it locally from https://cli.github.com.'
    exit 1
}
if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
    # Not fatal: run locally, gh falls back to its own stored credentials.
    Write-Host 'NOTE  GH_TOKEN is not set; gh will use whatever credentials it has stored.'
}

$zipName = "MagicTray-$Tag-win-x64.zip"
$sidecarName = "$zipName.sha256"
$sumsName = 'SHA256SUMS'
$exeName = 'MagicMouseTray.exe'
# Minimum set, not the whole set. The release also carries the loose diagnostic
# scripts, and extra assets are not an error - a MISSING one is.
$requiredNames = @($zipName, $sidecarName, $sumsName, $exeName)

Write-Host "Verifying the published release $Repo@$Tag"
Write-Host "Expecting at least: $($requiredNames -join ', ')"

$workDir = Join-Path ([IO.Path]::GetTempPath()) ('magictray-verify-' + [Guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $workDir -Force | Out-Null
$script:HttpClient = [Net.Http.HttpClient]::new()

try {
    # A 190 MB download over a runner's network wants a real ceiling; the
    # default 100 s applies to the whole response, body included.
    $script:HttpClient.Timeout = [TimeSpan]::FromMinutes(15)
    $script:HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd('magic-tray-verify-published-release')

    # 1. The release exists and is not a draft. A draft is invisible to everyone
    # without write access and its assets are not downloadable, so a "published"
    # draft is a failed release however complete its asset set looks.
    $release = $null
    try {
        $release = Invoke-GhApi -ApiPath "repos/$Repo/releases/tags/$Tag" -StderrPath (Join-Path $workDir 'gh-stderr.txt') |
            ConvertFrom-Json
    } catch {
        Write-Check -Ok $false -Name 'release exists' -Detail "$_"
    }

    if ($null -eq $release) {
        # Without release metadata there are no asset URLs, so every remaining
        # check would report the same one fact. Stop here.
        Write-Host ''
        Write-Host ('::error::No usable release metadata for {0}@{1}. The tag may exist without a release, or the release may be private to this token.' -f $Repo, $Tag)
        exit 1
    }

    $fields = @($release.PSObject.Properties.Name)
    $isDraft = ($fields -contains 'draft') -and [bool]$release.draft
    Write-Check -Ok (-not $isDraft) -Name 'release exists' -Detail $(
        if ($isDraft) { "$Tag is a DRAFT release; its assets are not publicly downloadable" }
        else {
            $state = if (($fields -contains 'prerelease') -and [bool]$release.prerelease) { 'prerelease' } else { 'published' }
            "$Tag is $state"
        }
    )

    $body = ''
    if (($fields -contains 'body') -and $release.body) { $body = [string]$release.body }

    # 2. Asset set. An asset whose state is not 'uploaded' is a half-finished
    # upload: the API lists it, a download of it fails, so it counts as missing.
    $assets = @()
    if ($fields -contains 'assets') { $assets = @($release.assets) }
    $byName = @{}
    foreach ($asset in $assets) { $byName[[string]$asset.name] = $asset }

    $assetReport = @(
        $assets | ForEach-Object {
            '{0} ({1:N0} bytes, state {2})' -f $_.name, [long]$_.size, $_.state
        }
    )
    Write-Host "Assets attached to ${Tag}: $(if ($assetReport.Count -gt 0) { $assetReport.Count } else { 'none' })"
    foreach ($line in $assetReport) { Write-Host "  $line" }

    $absent = @($requiredNames | Where-Object { -not $byName.ContainsKey($_) })
    $unfinished = @(
        $requiredNames |
            Where-Object { $byName.ContainsKey($_) -and ([string]$byName[$_].state) -ne 'uploaded' }
    )
    $setReasons = @()
    if ($absent.Count -gt 0) { $setReasons += "missing $($absent -join ', ')" }
    foreach ($name in $unfinished) { $setReasons += "$name state is '$($byName[$name].state)', not 'uploaded'" }
    if ($setReasons.Count -gt 0) {
        # The full list, in the failure detail and not only in the log above:
        # this is the line that says "the old workflow ran" at a glance.
        $setReasons += 'actual assets: ' + $(
            if ($assetReport.Count -gt 0) { $assetReport -join ' | ' } else { '(none)' }
        )
    }
    Write-Check -Ok ($setReasons.Count -eq 0) -Name 'asset set' -Detail $(
        if ($setReasons.Count -gt 0) { $setReasons -join '; ' }
        else { "$($requiredNames.Count) required assets present of $($assets.Count) attached" }
    )

    # 3. The published ZIP against its published sidecar. Both are fetched from
    # browser_download_url - the URL the site, install.txt and the winget
    # manifest all point at - so this proves the public download path serves the
    # bytes, not merely that the API knows a row exists.
    $zipHash = ''
    if ($byName.ContainsKey($zipName) -and $byName.ContainsKey($sidecarName)) {
        $zipPath = Join-Path $workDir $zipName
        $sidecarPath = Join-Path $workDir $sidecarName
        try {
            $zipBytes = Save-AssetFile -Url ([string]$byName[$zipName].browser_download_url) -Path $zipPath
            [void](Save-AssetFile -Url ([string]$byName[$sidecarName].browser_download_url) -Path $sidecarPath)

            $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
            $sidecarText = (Get-Content -LiteralPath $sidecarPath -Raw).Trim()
            $m = [regex]::Match($sidecarText, '^([0-9a-fA-F]{64})\s+\*?(.+)$')
            $zipReasons = @()
            if (-not $m.Success) {
                $zipReasons += "$sidecarName is not '<64 hex>  <filename>': '$sidecarText'"
            } else {
                $declared = $m.Groups[1].Value.ToLowerInvariant()
                if ($declared -ne $zipHash) {
                    $zipReasons += "digest mismatch (sidecar $declared, downloaded $zipHash)"
                }
                if ($m.Groups[2].Value.Trim() -ne $zipName) {
                    $zipReasons += "sidecar names '$($m.Groups[2].Value.Trim())' (expect $zipName)"
                }
            }
            Write-Check -Ok ($zipReasons.Count -eq 0) -Name 'zip matches sidecar' -Detail $(
                if ($zipReasons.Count -gt 0) { $zipReasons -join '; ' }
                else { '{0} ({1:N0} bytes) = {2}' -f $zipName, $zipBytes, $zipHash }
            )
        } catch {
            Write-Check -Ok $false -Name 'zip matches sidecar' -Detail "download failed: $_"
        }
    } else {
        Write-Check -Ok $false -Name 'zip matches sidecar' -Detail "cannot check: $zipName or $sidecarName is not attached to $Tag"
    }

    # Hoisted out of check 4 so the exe check below can read the line
    # SHA256SUMS gives for the exe. Left empty when SHA256SUMS is missing or
    # never parsed, which check 4 will already have reported in its own words.
    $declaredSums = [ordered]@{}

    # 4. SHA256SUMS. It is the file a visitor, and install.txt's AI agent, are
    # told to check a single download against, so it has to parse and it has to
    # describe assets that exist. It never lists itself.
    if ($byName.ContainsKey($sumsName)) {
        $sumsPath = Join-Path $workDir $sumsName
        try {
            [void](Save-AssetFile -Url ([string]$byName[$sumsName].browser_download_url) -Path $sumsPath)
            $sumsReasons = @()
            foreach ($line in (Get-Content -LiteralPath $sumsPath)) {
                if ([string]::IsNullOrWhiteSpace($line)) { continue }
                $sm = [regex]::Match($line.Trim(), '^([0-9a-fA-F]{64})\s+\*?(.+)$')
                if (-not $sm.Success) {
                    $sumsReasons += "unparsable line '$($line.Trim())' (expect '<64 hex>  <filename>')"
                    continue
                }
                $named = $sm.Groups[2].Value.Trim()
                if ($named -match '[\\/]') {
                    # A path component cannot be checked against a bare
                    # download, which is all a visitor has.
                    $sumsReasons += "'$named' carries a directory part; asset names are bare file names"
                    continue
                }
                if ($declaredSums.Contains($named)) {
                    $sumsReasons += "'$named' listed twice"
                    continue
                }
                $declaredSums[$named] = $sm.Groups[1].Value.ToLowerInvariant()
            }
            if ($declaredSums.Count -eq 0 -and $sumsReasons.Count -eq 0) {
                $sumsReasons += 'no checksum lines at all'
            }
            foreach ($named in @($declaredSums.Keys)) {
                if (-not $byName.ContainsKey($named)) {
                    $sumsReasons += "'$named' is listed but is not attached to $Tag"
                }
            }
            # The ZIP's bytes are already on disk and hashed, so the one line
            # that matters most is cross-checked for free: SHA256SUMS naming the
            # ZIP with a digest nobody can reproduce is the same broken promise
            # as having no checksum at all.
            if ($zipHash -and $declaredSums.Contains($zipName) -and $declaredSums[$zipName] -ne $zipHash) {
                $sumsReasons += "$zipName digest is $($declaredSums[$zipName]) in $sumsName but $zipHash when downloaded"
            }
            Write-Check -Ok ($sumsReasons.Count -eq 0) -Name 'SHA256SUMS' -Detail $(
                if ($sumsReasons.Count -gt 0) { $sumsReasons -join '; ' }
                else { "$($declaredSums.Count) lines, every name attached to $Tag" }
            )
        } catch {
            Write-Check -Ok $false -Name 'SHA256SUMS' -Detail "download failed: $_"
        }
    } else {
        Write-Check -Ok $false -Name 'SHA256SUMS' -Detail "cannot check: $sumsName is not attached to $Tag"
    }

    # 5. MagicMouseTray.exe against the line SHA256SUMS gives for it. Check 4
    # proves that line names an asset that exists; it does not prove the digest
    # on it belongs to that asset's bytes. docs/install.txt, step 2, tells an AI
    # agent to compute the SHA-256 of the exe it has just downloaded and to
    # STOP, delete the download and tell the user the checksum did not match if
    # it disagrees with the published digest. So a wrong exe line does no harm
    # here - the name is attached, the ZIP still matches its sidecar - and sends
    # every agent following install.txt into its STOP branch on a download that
    # was perfectly good. install.txt and this script are supposed to be
    # checking the same bytes against the same line; until now only one of them
    # was. The exe is another ~190 MB on the wire and on the runner's disk, and
    # it is the asset most people click, which is what makes it worth them.
    if ($byName.ContainsKey($exeName) -and $declaredSums.Contains($exeName)) {
        $exePath = Join-Path $workDir $exeName
        try {
            $exeBytes = Save-AssetFile -Url ([string]$byName[$exeName].browser_download_url) -Path $exePath
            $exeHash = (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash.ToLowerInvariant()
            $exeDeclared = [string]$declaredSums[$exeName]
            Write-Check -Ok ($exeDeclared -eq $exeHash) -Name 'exe matches SHA256SUMS' -Detail $(
                if ($exeDeclared -eq $exeHash) { '{0} ({1:N0} bytes) = {2}' -f $exeName, $exeBytes, $exeHash }
                else {
                    "digest mismatch ($sumsName says $exeDeclared, the downloaded file is $exeHash). " +
                    'install.txt tells an agent to hash this exact file and STOP when the two disagree, ' +
                    'so every agent that follows it will refuse this release.'
                }
            )
        } catch {
            Write-Check -Ok $false -Name 'exe matches SHA256SUMS' -Detail "download failed: $_"
        }
    } elseif (-not $byName.ContainsKey($exeName)) {
        Write-Check -Ok $false -Name 'exe matches SHA256SUMS' -Detail "cannot check: $exeName is not attached to $Tag"
    } elseif ($declaredSums.Count -eq 0) {
        Write-Check -Ok $false -Name 'exe matches SHA256SUMS' -Detail "cannot check: no usable $sumsName, so there is no published digest for $exeName to compare a download against"
    } else {
        Write-Check -Ok $false -Name 'exe matches SHA256SUMS' -Detail "$sumsName carries no line for $exeName, so an agent told to compare its download against the published checksum has a checksum file with nothing in it to compare"
    }

    # 6. The notes quote the ZIP's digest. Compared against the digest computed
    # from the downloaded bytes rather than the one in the sidecar, so notes
    # that quote a stale-but-self-consistent digest fail too. On v1.1.0 the body
    # was an auto-generated pull-request list and contained no digest at all.
    if ($zipHash) {
        $quoted = $body.ToLowerInvariant().Contains($zipHash)
        Write-Check -Ok $quoted -Name 'notes quote the zip digest' -Detail $(
            if ($quoted) { "$zipHash appears in the release body" }
            else {
                "the release body does not contain $zipHash. Body is $($body.Length) characters; " +
                'the notes written by release.yml carry a | Asset | Bytes | SHA-256 | table. ' +
                'A body without it is the v1.1.0 shape: an auto-generated PR list from a stale workflow.'
            }
        )
    } else {
        Write-Check -Ok $false -Name 'notes quote the zip digest' -Detail "cannot check: $zipName was not downloaded, so there is no digest to look for"
    }
} catch {
    Write-Host "FAIL  unexpected: $_"
    Write-Host ('::error::verify-published-release.ps1 failed unexpectedly: {0}' -f $_)
    exit 1
} finally {
    $script:HttpClient.Dispose()
    # Runner disks are small and the ZIP and the exe are ~190 MB each; never
    # leave them behind, but never let a locked temp file be the reason a
    # verified release fails.
    Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
if ($script:ProblemCount -gt 0) {
    # The release is already public at this point, so say what to do about it,
    # in the log and in the run summary. The advice is keyed off WHICH checks
    # failed: telling someone to attach a missing zip by hand looks helpful and
    # leaves the release still broken, because the notes would carry no digest
    # table and SHA256SUMS would never have covered the file they uploaded.
    # RELEASING.md section 5 documents the same split; this string is the
    # mechanism, that document is the decision.
    $remedy =
        # Draft first: a draft's assets are not downloadable by anyone, so every
        # later failure is a symptom of the draft rather than a second fault,
        # and the digest advice below would send the reader after a packaging
        # bug that is not there.
        if ($script:FailedCheck -contains 'release exists') {
            'The release exists but is a draft, so nothing is downloadable and every other failure above is a symptom of that. Publish it (gh release edit <tag> --draft=false) and re-run this script against the tag; only if it still fails should the release and the tag be deleted and re-cut.'
        } elseif ($script:FailedCheck -contains 'asset set') {
            'The published asset set is wrong, which means the workflow that ran was not the one in this tree. Delete the release AND the tag, then re-cut from a tree whose .github/workflows/release.yml is current. Do not attach the missing files by hand: a hand-attached ZIP still leaves notes with no digest table and a SHA256SUMS that never covered it, and that half-fix is what left v1.1.0 with a winget manifest pointing at an asset nobody can download.'
        } elseif (($script:FailedCheck -contains 'zip matches sidecar') -or ($script:FailedCheck -contains 'SHA256SUMS') -or ($script:FailedCheck -contains 'exe matches SHA256SUMS')) {
            'Every required asset is attached, but the published bytes and the published digests disagree. That is never a notes problem and it cannot be edited away: delete the release AND the tag and re-cut, then work out how the packaging step and the upload came apart.'
        } elseif (($script:FailedCheck -contains 'notes quote the zip digest') -and ($script:FailedCheck.Count -eq 1)) {
            'The asset set and every checksum are good and only the notes are wrong, so this one is repairable in place: rewrite the body to include the digest table (gh release edit <tag> --notes-file <file>) and re-run this script against the tag to confirm.'
        } else {
            'Delete the release AND the tag and re-cut from a tree whose .github/workflows/release.yml is current. Leaving a wrong release published is what left v1.1.0 with a winget manifest pointing at an asset nobody can download.'
        }
    $summary = @(
        "## Published release verification FAILED: $Repo@$Tag",
        '',
        "$script:ProblemCount check(s) failed against the release as published:",
        ''
    )
    $summary += @($script:ProblemLines | ForEach-Object { "- $_" })
    $summary += @('', $remedy)
    foreach ($line in $summary) { Write-Host $line }
    if ($env:GITHUB_STEP_SUMMARY) {
        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summary
    }
    Write-Host ('::error::{0} check(s) failed against the published release {1}@{2}.' -f $script:ProblemCount, $Repo, $Tag)
    exit 1
}

Write-Host "PASS  the published release $Repo@$Tag carries the full asset set, matching checksums and notes that quote the zip digest"
exit 0
