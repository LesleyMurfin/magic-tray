# Making search engines describe Magic Tray correctly

Two days after launch, Google's AI Overview described Magic Tray as "shows your Apple Magic Mouse
battery percentage," cited **github.com** and **Etsy**, and offered wooden desk trays as an
alternative meaning. Three separate problems:

| Problem | Cause | Fixed by |
| --- | --- | --- |
| Called it a mouse-battery-only app | Old repo description was the only text Google had | Repo description + `docs/index.html` meta + JSON-LD `featureList` |
| Said "Windows 11" only | Old repo description said Windows 11 | `operatingSystem: "Windows 10, Windows 11"` in JSON-LD, meta description, README |
| Confused with Etsy desk trays | No structured data declaring a software product | `SoftwareApplication` + `disambiguatingDescription` + FAQ entry |
| Named the repo instead of the site | Sitemap listed `github.com`; README buried the site link | `sitemap.xml` now lists only site URLs; README links the site in the first lines |

## What is already done in the repo

- `docs/index.html` — `SoftwareApplication`, `WebSite`, and `FAQPage` JSON-LD. Declares free, MIT,
  Windows 10 **and** 11, mouse **and** keyboard **and** trackpad, and explicitly states it is not a
  physical desk tray.
- `docs/drivers.html` — `HowTo` for reading the hardware id, so "how do I tell which Magic Mouse I
  have" can be answered directly from the site.
- `docs/v3.html` — `FAQPage` for "is the 2024 mouse the same as Magic Mouse 2" and the sleep/scroll
  question.
- `docs/sitemap.xml` — site URLs only. A sitemap must not point at another host.
- `docs/robots.txt` — internal `DESIGN-*.md` and `STRIPE-SETUP.md` are no longer crawlable. They
  contradict shipped behaviour and were competing with the real docs.
- `README.md` — the website is linked in the first three lines with descriptive anchor text.

## What only the repo owner can do

1. **Google Search Console** — add `https://lesleymurfin.github.io/magic-tray/` as a URL-prefix
   property. GitHub Pages cannot serve a DNS TXT record, so verify with the HTML file method: drop
   the `google*.html` file Google gives you into `docs/`, commit, then click Verify.
2. **Submit the sitemap** — Search Console → Sitemaps → `sitemap.xml`.
3. **Request indexing** — URL Inspection, one request each for `/`, `/drivers.html`, `/v3.html`.
   This is what replaces the stale cached description; editing the repo alone does not force a
   recrawl.
4. **Bing Webmaster Tools** — same two steps. Bing feeds several AI answer engines.
5. **Consider a custom domain.** `lesleymurfin.github.io/magic-tray/` is a path on a shared
   subdomain, which is why `github.com` outranks it. A domain such as `magictray.app` with a
   `docs/CNAME` file would own its own authority. Update `canonical`, `og:url`, JSON-LD `@id`s, and
   `sitemap.xml` if this happens.
6. **Get real inbound links.** Structured data tells Google *what* the page is; links decide whether
   the site or the repo ranks. The honest places to post: r/apple, r/windows, r/MacOSBootCamp,
   Hacker News, and the existing Magic Mouse scroll threads that currently only link Magic Utilities.

## Do not

- Do not put keyword lists in the page. It reads as spam and Google ignores it.
- Do not claim WHQL, Microsoft signing, or Secure Boot support until the driver is actually signed.
- Do not fight the Etsy results. Different product category. The `disambiguatingDescription` and the
  footer line are the correct answer.

## Re-check after Google recrawls

- Rich Results Test: <https://search.google.com/test/rich-results?url=https%3A%2F%2Flesleymurfin.github.io%2Fmagic-tray%2F>
- Ask the AI Overview again: "magic tray windows app". Correct answer names the site, says Windows 10
  and 11, and lists mouse, keyboard, and trackpad battery plus the scroll driver.
