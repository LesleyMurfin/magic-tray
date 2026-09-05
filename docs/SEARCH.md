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

## Search Console

The old project-path property on the shared `github.io` subdomain does not carry over; a new
domain needs a new property. GitHub redirects the old URLs, so nothing is lost for visitors, but
the search history and the verification do not follow.

1. Add `magictray.app` as a **Domain** property and verify with the DNS TXT record Google provides.
   This is possible now that the domain is ours — on `github.io` it was not, because GitHub Pages
   cannot serve a DNS TXT record, and only the HTML-file method worked.
2. Submit `sitemap.xml`.
3. URL Inspection → **Request indexing** for `/`, `/drivers.html`, `/v3.html`. Editing files does not
   force a recrawl; Google keeps serving the stale description until it re-reads the pages.
4. Repeat in Bing Webmaster Tools. Bing feeds several AI answer engines.

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
- `docs/robots.txt` — explicit `Allow: /` stanzas for 19 AI and answer-engine crawlers (GPTBot,
  ClaudeBot, PerplexityBot, Applebot, CCBot, and the rest). Each stanza repeats the two `Disallow:`
  lines, because a matched user-agent group replaces the wildcard group instead of adding to it.
- Every page — a `WebPage` plus `BreadcrumbList`, and an `ItemList` of PID → driver path on
  `docs/drivers.html`, all referencing the `#app` and `#site` ids declared on the homepage.
- Outbound links — keyword-bearing anchors from the homepage, `v3.html`, `drivers.html`, and
  `devices.html` to the Magic Mouse v3 driver site, which is the other half of this entity.

Validate after any edit:
<https://search.google.com/test/rich-results?url=https%3A%2F%2Fmagictray.app%2F>

### Device photographs

The identification cards on `docs/drivers.html` and `docs/devices.html`, and the model cards on
`docs/index.html`, used to be CSS-drawn grey rectangles labelled BOTTOM, which told a reader
nothing. They are now **real licensed photographs of the actual hardware**, shipped under
`docs/img/` (nine files) plus the pre-existing `docs/magic-mouse-v2-lightning.jpg`. Sources are
Wikimedia Commons, under CC0, CC BY 4.0 and CC BY-SA 4.0.

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

**The one outstanding gap: no free photo of the Magic Mouse v3 (2024, USB-C) underside.**
`Category:Magic Mouse` on Wikimedia Commons was enumerated in full and every file checked;
searches for A3204, "Magic Mouse USB-C" and "Magic Mouse 2024" returned nothing usable. Apple's
own renders are all-rights-reserved. That slot therefore shows a labelled placeholder, and a v1
or v2 photo must never be captioned as a v3 — the page that tells people how to identify their
mouse cannot afford a wrong picture.

The cheapest fix is to **ask an owner for one**: a single overhead shot of the underside,
released CC0, from anyone with the 2024 mouse. Worth asking for in the v3 driver repo's issues,
in the TESTED.md reports thread, and in the Reddit and Hacker News posts listed below. Upload it
to Wikimedia Commons under CC0 so it is reusable, then drop it in beside the others.

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
