# Domain, search, and how the site gets described

Two days after the 1.1.0 launch, Google's AI Overview described Magic Tray as "shows your Apple
Magic Mouse battery percentage," said it required Windows 11, cited **github.com** and **Etsy**, and
offered wooden desk trays as an "alternative meaning." It never named this site. Four separate
causes, all now fixed in the repo:

| Problem | Cause | Fixed by |
| --- | --- | --- |
| Called it a mouse-battery-only app | Old repo description was the only text Google had | Repo description + `docs/index.html` meta + JSON-LD `featureList` naming mouse, keyboard, trackpad, and both scroll drivers |
| Said "Windows 11" only | Old repo description said Windows 11 | `operatingSystem: "Windows 10, Windows 11"` in JSON-LD, meta description, README |
| Confused with Etsy desk trays | No structured data declaring a software product | `SoftwareApplication` + `disambiguatingDescription` + FAQ entry + footer line |
| Named the repo instead of the site | Sitemap listed `github.com`; README buried the site link | `sitemap.xml` now lists only site URLs; README links the site in the first lines |

## The domain

The site runs on **magictray.app**. `.app` is a Google Registry TLD on the HSTS preload list, so
HTTPS is mandatory and browsers will not fall back to plain HTTP. That is good for trust, and it has
one consequence during setup: between pointing DNS and GitHub issuing the certificate, the site is
unreachable rather than merely insecure.

The previous home was a project path on the shared `github.io` subdomain, which is why
`github.com` outranked it regardless of markup quality. GitHub Pages redirects those old Pages
URLs to the custom domain, so existing links keep working.

### DNS records

The domain is registered at Cloudflare and uses Cloudflare nameservers, so the records live in the
Cloudflare DNS tab. This is the live configuration — apex `magictray.app` has four A and four AAAA
records, and `www` is a CNAME. Every record is **DNS only (grey cloud)**. Addresses match
`https://api.github.com/meta`:

```
A     magictray.app    185.199.108.153
A     magictray.app    185.199.109.153
A     magictray.app    185.199.110.153
A     magictray.app    185.199.111.153
AAAA  magictray.app    2606:50c0:8000::153
AAAA  magictray.app    2606:50c0:8001::153
AAAA  magictray.app    2606:50c0:8002::153
AAAA  magictray.app    2606:50c0:8003::153
CNAME www              lesleymurfin.github.io
```

Resolution is confirmed against both `1.1.1.1` and `8.8.8.8`:

```
dig +short magictray.app A @1.1.1.1
dig +short magictray.app AAAA @1.1.1.1
dig +short www.magictray.app @8.8.8.8
```

### Cloudflare specifics

**Every record must stay DNS only (grey cloud), not Proxied (orange cloud).** Cloudflare proxies new
records by default, so this is a deliberate setting rather than the default one. A proxy in front of
a Pages site that has no certificate yet prevents GitHub's Let's Encrypt validation from completing,
and the DNS check on the Pages settings page will not pass.

Once HTTPS is enforced on GitHub the proxy may be switched back on, with SSL/TLS mode
**Full (strict)**. Leaving it DNS-only is also fine; GitHub already serves the site over a CDN.

### Order of operations

`docs/CNAME` must not reach `main` before DNS resolves, or the live site breaks. Steps 1–3 are done.

1. **Done** — register the domain. The registrant email must be correct first; ICANN verification
   goes there, and an unverified domain is suspended after 15 days.
2. **Done** — add the apex A/AAAA records and the `www` CNAME above, all DNS-only.
3. **Done** — verify resolution *before* going any further: all four A records, all four AAAA
   records, and `www.magictray.app` must answer from at least two public resolvers (`1.1.1.1` and
   `8.8.8.8`). Nothing below this line is safe until that passes.
4. Merge the custom-domain pull request. That commits `docs/CNAME`.
5. Repo → Settings → Pages → Custom domain → `magictray.app` → Save. It should report that the DNS
   check passed.
6. Wait for the certificate. Usually minutes, occasionally up to an hour. **The site is down during
   this window**, because `.app` is HSTS-preloaded and there is no plain-HTTP fallback to serve
   from.
7. Tick **Enforce HTTPS** once it becomes available.
8. `gh repo edit LesleyMurfin/magic-tray --homepage https://magictray.app`

## Page URLs

File names are the one piece of page text that shows up in a result snippet, in a shared link, and
in the citation line of an AI answer. `v3.html` said nothing to any of them: "v3" is our internal
word for the 2024 USB-C mouse, it matches no query anyone types, and it reads like a version of the
app rather than a model of mouse. That page is now **`magic-mouse-2024.html`**, which carries the
two words people actually search with.

Every other page already names its subject in one word (`battery`, `keyboard`, `drivers`,
`devices`, `funding`), so they keep their URLs. A rename costs whatever ranking history the old
URL had earned; it is only worth paying where the old name was meaningless.

`docs/v3.html` still exists as a **redirect stub**: a zero-second `<meta http-equiv="refresh">`, a
`<link rel="canonical">` at the new URL, a `location.replace` that preserves the `#fragment`, and a
visible link for anyone whose browser does neither. GitHub Pages serves static files and cannot
issue a real `301`, so this is the strongest available signal; Google treats an instant meta
refresh as a permanent redirect, which is why the delay is `0` and not `1`. The visible fallback
text does not weaken that. Keep the stub. Old Reddit and GitHub comments link the old URL and
will not be edited.

The stub is deliberately **not** in `sitemap.xml`. A sitemap is a list of URLs you want indexed,
and this one exists only to hand visitors and crawlers on to the new URL. Note for whoever lands
`scripts/check-site.ps1` (branch `ci/full-actions-suite`): its `Test-Sitemap` rule errors on any
`docs/*.html` missing from the sitemap, so it needs an explicit exception for both pages that are
deliberately absent — this redirect stub and `docs/404.html` — or it will fail them on
purpose-built behaviour.

### Why drivers.magictray.app

**Decided 2026-09-16 by Lesley Murfin (CEO). This is settled; do not reopen it.**

The driver hub ships on `drivers.magictray.app`, served from the
`magic-mouse-v3-windows-fix` repository with its own `docs/CNAME`. Driver pages do **not** move
to `magictray.app/drivers/*.html`.

An earlier revision of this file recorded the opposite conclusion. It was never approved and is
overturned. Recording the reasoning so the argument is not relitigated:

1. **The two sites have different jobs.** `magictray.app` sells a tray app that reads battery
   percent. The driver hub is a certification and download project with its own roadmap, funding
   state and release cadence. Separate hosts let them ship on separate schedules without one
   repository's release gate blocking the other's.
2. **Two repositories already exist.** The driver content lives in
   `magic-mouse-v3-windows-fix` today and is deployed from there. A subdomain matches the
   deployment boundary that is already real; subdirectory paths would mean either merging the
   repositories or proxying one through the other.
3. **Search Console cost is nil.** `magictray.app` is verified as a **Domain** property, not a
   URL-prefix property, so `drivers.magictray.app` is covered by the existing DNS TXT
   verification with no second property and no second verification step.

The cost is real and accepted: `drivers.magictray.app` is evaluated by Google as its own host and
starts without the trust `magictray.app` has accumulated. Mitigation is cross-linking — every
driver page links back to the apex, and the apex links out to the hub — plus a shared
`SoftwareApplication` entity in the JSON-LD on both hosts so the two are read as one project.

`docs/trackpad.html` stays on this host. It is written for the "does my trackpad do gestures on
Windows" query and points at `vitoplantamura/MagicTrackpad2ForWindows`, a Microsoft-signed
Precision Touchpad driver for the Magic Trackpad 2 on Windows 11, GPL-2.0, `v2.0` released
February 2026. It is not ours and we have not tested it: the page names it, states what it
targets, and links its repo rather than repeating its steps.

## Search Console

The old project-path property on the shared `github.io` subdomain does not carry over; a new
domain needs a new property. GitHub redirects the old URLs, so nothing is lost for visitors, but
the search history and the verification do not follow.

1. Add `magictray.app` as a **Domain** property and verify with the DNS TXT record Google provides.
   This is possible now that the domain is ours — on `github.io` it was not, because GitHub Pages
   cannot serve a DNS TXT record, and only the HTML-file method worked.
2. Submit `sitemap.xml`.
3. URL Inspection → **Request indexing** for `/`, `/drivers.html` and `/magic-mouse-2024.html`.
   Editing files does not force a recrawl; Google keeps serving the stale description until it
   re-reads the pages. Submit `/v3.html` as well — not because it should be indexed, but because
   the meta refresh only counts once Googlebot re-fetches the old URL and sees it.
4. Repeat in Bing Webmaster Tools. Bing feeds several AI answer engines.

## What is already done in the repo

- `docs/index.html` — `SoftwareApplication`, `WebSite`, and `FAQPage` JSON-LD. Declares free, MIT,
  Windows 10 **and** 11, mouse **and** keyboard **and** trackpad, and explicitly states it is not a
  physical desk tray.
- `docs/drivers.html` — `HowTo` for reading the hardware id, so "how do I tell which Magic Mouse I
  have" can be answered directly from the site.
- `docs/magic-mouse-2024.html` — `FAQPage` for "is the 2024 mouse the same as Magic Mouse 2" and
  the sleep/scroll question.
- `docs/sitemap.xml` — site URLs only. A sitemap must not point at another host. It also no longer
  lists `TESTED.md` and `ALERTS.md`: Pages serves raw Markdown as plain text, with no title, no
  description and no structured data, so those entries could only ever produce a bad result. Both
  files still exist and are still linked from the pages. Every `lastmod` is the rename date, so the
  pages that link the new URL are all re-fetched.
- `design/` — the internal `DESIGN-*.md` files and `STRIPE-SETUP.md` used to sit in `docs/`, which
  publishes them. `robots.txt` disallowed them, but that is a crawl request and not access
  control: anyone could still fetch them, and they contradict shipped behaviour. All thirteen now
  live in a top-level `design/` directory that Pages does not serve, so the `Disallow:` lines are
  gone with them.
- `README.md` — the website is linked in the first three lines with descriptive anchor text.
- `docs/robots.txt` — explicit `Allow: /` stanzas for 19 AI and answer-engine crawlers (GPTBot,
  ClaudeBot, PerplexityBot, Applebot, CCBot, and the rest) plus the wildcard. The stanzas are
  deliberately identical rather than collapsed into one, because a matched user-agent group
  replaces the wildcard group instead of adding to it: a crawler that matches its own name would
  otherwise read no rules at all.
- `docs/404.html` — GitHub Pages serves this for any unknown path, so a mistyped or truncated
  inbound link lands on a real page that routes to the five main destinations instead of GitHub's
  generic one. Every path on it is root-relative, because a 404 is served at arbitrary depth. It
  carries `noindex`, has no `canonical`, and is not in `sitemap.xml`.
- Every page — a `WebPage` plus a `BreadcrumbList`, both referencing the `#app` and `#site` ids
  declared on the homepage. This list used to claim an `ItemList` of PID → driver path on
  `docs/drivers.html`; there has never been one. If it is worth adding, `drivers.html` is where the
  PID table already lives.
- Photographs — every device photo is WebP, resized to twice its rendered width. The set went from
  2,850,283 bytes of JPEG to 844,479, and `devices.html` from 2.17 MB to 553 KB. WebP has no
  fallback here on purpose: every browser the site targets reads it, and a `<picture>` element
  would double the files to maintain. Licensing is unaffected — resizing and re-encoding were
  already being done, and `THIRD-PARTY-NOTICES.md` carries every author, licence and source.
- Icons — `docs/icon.svg` plus a 180px `apple-touch-icon.png`. The favicon used to be
  `screenshot-tray.png`, a 353×201 screenshot of the Windows tray flyout. Google only shows a site
  icon in results if it is square, so the site was showing the generic globe.
- Outbound links — keyword-bearing anchors from the homepage, `magic-mouse-2024.html`,
  `drivers.html`, and `devices.html` to the Magic Mouse v3 driver site, which is the other half of
  this entity.

Validate after any edit:
<https://search.google.com/test/rich-results?url=https%3A%2F%2Fmagictray.app%2F>

### Device photographs

The identification cards on `docs/drivers.html` and `docs/devices.html`, and the model cards on
`docs/index.html`, used to be CSS-drawn grey rectangles labelled BOTTOM, which told a reader
nothing. They are now **real photographs of the actual hardware**, shipped under `docs/img/`
(ten files) plus the pre-existing `docs/magic-mouse-v2-lightning.webp`. All but one come from
Wikimedia Commons, under CC0, CC BY 4.0 and CC BY-SA 4.0; the exception is the 2024 Magic Mouse
underside, which is an Apple product image and is not freely licensed.

- **Attribution lives in `THIRD-PARTY-NOTICES.md`** — one entry per file with the author, the
  licence, the licence URL and the Commons source page. Two authors mandate a verbatim credit
  string; both are recorded there character for character. The pages also render a short credit
  line under each photo grid. Do not ship a photo without both.
- Cropping is done at display time with CSS `object-fit` / `object-position`. The stored pixels
  are unmodified apart from resizing and EXIF stripping, which keeps the CC BY-SA files a
  collection rather than an adaptation — so the site itself stays MIT.
- Photographs are also an answer-engine asset: they give the identification pages something to
  show in image results and in AI answers that quote "how do I tell which Magic Mouse I have".
  Every `<img>` carries a descriptive `alt` and explicit `width`/`height`, so identification is
  possible from the alt text alone and the images cost no layout shift (CLS).

**The one outstanding gap: no *free* photo of the Magic Mouse v3 (2024, USB-C) underside.**
`Category:Magic Mouse` on Wikimedia Commons was enumerated in full and every file checked;
searches for A3204, "Magic Mouse USB-C" and "Magic Mouse 2024" returned nothing usable. That is
still true — no freely licensed photograph of this device has been found.

The slot is no longer a placeholder: it now carries **Apple's own product image** of the A3204
underside, credited on the page as an Apple product image and recorded in
`THIRD-PARTY-NOTICES.md` as all-rights-reserved with no Creative Commons or other free licence.
That is a deliberate decision to ship an unfree file, not a licensing win, and nothing on the
site may describe it as Creative Commons. A v1 or v2 photo must never be captioned as a v3
either — the page that tells people how to identify their mouse cannot afford a wrong picture.

A CC0 replacement is still wanted, so the Apple file can be swapped out. The cheapest route is
to **ask an owner for one**: a single overhead shot of the underside, released CC0, from anyone
with the 2024 mouse. Worth asking for in the v3 driver repo's issues, in the TESTED.md reports
thread, and in the Reddit and Hacker News posts listed below. Upload it to Wikimedia Commons
under CC0 so it is reusable, then drop it in beside the others and delete the Apple image.

## The entity graph, and why the first structured-data pass was not enough

Two weeks after the markup above shipped, Gemini was asked for a "magic tray windows app" and
answered with **MagicWindow**, an unrelated window manager in the Microsoft Store. The markup was
not the problem in the way it looked: the audit found the graph was only whole on the homepage.

Structured data is parsed per page. `battery.html`, `keyboard.html`, `drivers.html`, `devices.html`
and `v3.html` each *referenced* `https://magictray.app/#app` and `#site` through `about` and
`isPartOf`, but never *declared* those nodes, so a crawler that landed on any page except the
homepage — which is most of them, because the subpages are the ones that answer real questions —
read a page about an entity with no type, no name and no operating system. `funding.html` declared
a third, slightly different `#app`. Author was an anonymous inline `Person` repeated on every node,
so nothing tied the site to one identity and there was nowhere for `sameAs` to point.

What changed:

- **Every page declares the same three nodes** — `#person`, `#app`, `#site` — character for
  character, ahead of its own `WebPage`. Any page read alone now names the product, the platform,
  the price and the author.
- **One `Person` node** (`#person`) with `sameAs` to the GitHub profile, this repo and the v3 driver
  repo, referenced by `@id` from every `author` and `publisher`. No anonymous `Person` is left.
- **`#app` on the homepage gained** `softwareRequirements`, `processorRequirements`, `datePublished`,
  `releaseNotes`, `featureList`, and a `sameAs` array; subpages declare a compact `#app` (name,
  type, OS, category, author, version, offers) so every `@id` reference resolves independently
  without duplicating homepage-only release metadata.
- **The homepage H1 names the product.** It read "Apple Magic Mouse and Keyboard battery on Windows
  10 and 11" — the brand query that failed was the one string the page never contained.
- **Magic Utilities is answered, not avoided** (`#mu`). The comparison is honest: no gestures, no
  media-key remapping, buy Magic Utilities if you need them. Answer engines get asked this
  constantly, and the site previously only denied being a clone.
- **FAQ parity is now real.** `battery.html` and `v3.html` had `FAQPage` questions that existed
  nowhere on the page as a question; `index.html` had eight in schema and six on the page. Schema
  that quotes text a reader cannot find is the failure mode Google penalises. Every page now has one
  `dl.faq`, and each answer is condensed rather than a second copy of the body prose.
- **New questions in the words people actually type**: how do I check my Magic Mouse battery on
  Windows 10 or 11, how do I check the trackpad, how do I see keyboard percent on Windows 11, why
  won't my Magic Mouse scroll on Windows 11, does the 2024 USB-C mouse work on Windows 11.
- **`llms.txt` now disambiguates MagicWindow by name**, next to the existing Etsy and Magic
  Utilities lines, and says which queries belong to this project.
- **`SEARCH.md` is `Disallow`ed in `robots.txt`.** This file is the strategy, not the product; it
  was crawlable and absent from the sitemap.

### The gate that keeps it true

`scripts/check-aeo.ps1` (CI job `aeo-checks`) parses every page's graph and fails the build on: a
missing or unparseable `ld+json` block, an `@id` reference with no declaration on the same page,
two pages disagreeing about a shared `@id`, a `WebPage` url that contradicts the canonical link or
the file path, a missing `dateModified`, FAQ text that differs between schema and page, and a
`softwareVersion` that has drifted from the visible download links. It is read-only and runs on
`ubuntu-latest`. It deliberately does not repeat the link, sitemap, robots and CNAME checks in
`scripts/check-site.ps1`.

Validate the rendered result against Google as well — the gate checks the graph, not Google's
eligibility rules:
<https://search.google.com/test/rich-results?url=https%3A%2F%2Fmagictray.app%2F>

### Telling the engines a page changed

`docs/862b894a281ea8cdca5c46c788285279.txt` is an IndexNow key that nothing had ever used.
`.github/workflows/indexnow.yml` now submits every `<loc>` in `sitemap.xml` to
`api.indexnow.org` on any push to `main` that touches `docs/**`. That reaches Bing, which feeds
Copilot and several answer engines, in minutes rather than weeks. Google ignores IndexNow; for
Google, Search Console → URL Inspection → Request indexing is still the manual lever.

## Round two: the parts that are ranking inputs, not entity signals

Structured data decides *how* a page can be shown. It is not a ranking factor. These are, and they
were all measurably wrong:

- **`devices.html` shipped 2.07 MB of photographs** — ten 1600 px JPEGs in cards that render at
  400 px. It is the page a phone user lands on when they search "which magic mouse do I have", so
  it was the worst page on the site to make heavy. Every photo now has a 800 px WebP derivative
  served through `<picture>`, JPEG kept as the fallback: **2072 KB → 426 KB, 79% less**.
  `keyboard.html` went 294 KB → 165 KB. Site-wide image payload is down 74%. Sizes were measured
  in a headless browser, not guessed, and SSIM against the old render confirms the CSS crops still
  frame the hardware feature each card is about.
- **Preview metadata was uneven** — nine `og:` tags on some pages, four on others, one `twitter:`
  tag on three of them. A page with no card is a page nobody reposts. All nine pages now carry the
  same thirteen-tag set with page-specific values.
- **`TESTED.md` was a sitemap URL served as `text/markdown`** — no title, no canonical, no nav, no
  styling. It is also the highest-intent content here: "does my Magic Keyboard work on Windows 11"
  is a question with buying intent and almost no good answers on the web. It is now
  **`tested.html`**, a real page, linked from the homepage rail, `battery.html` and `drivers.html`.
  The Markdown files stay in the repo for GitHub readers and are `Disallow`ed, so one URL owns the
  content.
- **There was no `404.html`** — GitHub's generic page, no way back into the site. There is one now,
  `noindex`, out of the sitemap, linking the five real destinations.
- **No visible freshness date.** `dateModified` lived in JSON-LD, where a reader cannot see it and
  a crawler cannot corroborate it. Every page now shows `Last updated <time datetime="…">`, and CI
  fails if the visible date and the graph disagree.

`scripts/check-aeo.ps1` grew five checks for the new invariants: preview-metadata completeness,
visible date equals `dateModified`, no HTML link to a `.md` inside the site, every `<picture>`
fallback exists with dimensions, and a `noindex` page stays out of the sitemap.

Repo topics gained `windows-10`, `magic-mouse-scroll`, `bootcamp-drivers` and `battery-percentage`;
the repo page is the highest-authority URL this project has, and it was tagged `windows-11` only.

### What is still not done, in order of value

1. **Links.** Nothing above changes authority. See below.
2. **Submit the winget manifest.** `packaging/winget/` holds a complete, unsubmitted 1.1.0 manifest
   set. A merged `microsoft/winget-pkgs` PR is a citation from a Microsoft-owned repository, and
   `winget install` output is text answer engines quote.
3. **Search Console and Bing Webmaster Tools.** Still manual: verify the domain property, submit
   the sitemap, request indexing for `/`, `/tested.html`, `/drivers.html`, `/v3.html`.
4. **A photograph of the 2024 mouse underside**, still the one content gap on `devices.html`.

## Still the biggest lever

Structured data tells Google *what* a page is. Links decide whether the site or the repo ranks. The
honest places to post: r/apple, r/windows, r/MacOSBootCamp, Hacker News, and the Magic Mouse scroll
threads — including the GitHub issues about 2024 Magic Mouse scrolling — that currently link only
Magic Utilities.

## Do not

- Do not put keyword lists in the page. It reads as spam and Google ignores it.
- Do not claim WHQL, Microsoft signing, or Secure Boot support until the driver is actually signed.
- Do not fight the Etsy results. Different product category. The `disambiguatingDescription` and the
  footer line are the correct answer.

## Verify the description is fixed

Search "magic tray windows app" again after a recrawl. A correct answer names **magictray.app**, says
Windows 10 and 11, and lists mouse, keyboard, and trackpad battery plus the Magic Mouse scroll
driver.
