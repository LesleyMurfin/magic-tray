# Domain, search, and how the site gets described

Two days after the 1.1.0 launch, Google's AI Overview described Magic Tray as an app that "shows
your Apple Magic Mouse battery percentage," said it required Windows 11, cited **github.com** and
**Etsy**, and offered wooden desk trays as an "alternative meaning." It never named this site.

Four separate causes, all now fixed in the repo:

| Problem | Cause | Fix |
| --- | --- | --- |
| Called it a mouse-battery-only app | The old repo description was the only text Google had | `SoftwareApplication` JSON-LD `featureList` naming mouse, keyboard, trackpad, and both scroll drivers |
| Said Windows 11 only | Old repo description said Windows 11 | `operatingSystem: "Windows 10, Windows 11"` in JSON-LD, meta, README |
| Confused with Etsy desk trays | Nothing declared this a software product | `disambiguatingDescription` plus an FAQ entry and a footer line |
| Named the repo, not the site | `sitemap.xml` listed `github.com`; README buried the site link | Sitemap lists only this host; README links the site in the first three lines |

## The domain

The site runs on **magictray.app**. `.app` is a Google Registry TLD on the HSTS preload list, so
HTTPS is mandatory and browsers will not fall back to plain HTTP. That is good for trust, and it has
one consequence during setup: between pointing DNS and GitHub issuing the certificate, the site is
unreachable rather than merely insecure.

`lesleymurfin.github.io/magic-tray/` was a path on a shared subdomain, which is why `github.com`
outranked it regardless of markup quality. GitHub Pages redirects the old `github.io` URLs to the
custom domain, so existing links keep working.

### DNS records

Apex `magictray.app` needs four A and four AAAA records. Addresses confirmed against
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

### Cloudflare specifics

The domain is registered at Cloudflare, so it uses Cloudflare nameservers and the records go in the
Cloudflare DNS tab.

**Set every record to DNS only (grey cloud), not Proxied (orange cloud).** Cloudflare proxies new
records by default. A proxy in front of a Pages site that has no certificate yet prevents GitHub's
Let's Encrypt validation from completing, and the DNS check on the Pages settings page will not pass.

Once HTTPS is enforced on GitHub you may switch the proxy back on, with SSL/TLS mode
**Full (strict)**. Leaving it DNS-only is also fine; GitHub already serves the site over a CDN.

### Order of operations

`docs/CNAME` must not reach `main` before DNS resolves, or the live site breaks.

1. Register the domain. Confirm the registrant email is correct first — ICANN verification goes
   there, and an unverified domain is suspended after 15 days.
2. Add the DNS records above, DNS-only.
3. Merge the custom-domain pull request. That commits `docs/CNAME`.
4. Repo → Settings → Pages → Custom domain → `magictray.app` → Save. It should report that the DNS
   check passed.
5. Wait for the certificate. Usually minutes, occasionally up to an hour. The site is down during
   this window because `.app` forbids plain HTTP.
6. Tick **Enforce HTTPS** once it becomes available.
7. `gh repo edit LesleyMurfin/magic-tray --homepage https://magictray.app`

## Search Console

The old `github.io` property does not carry over; a new domain needs a new property.

1. Add `magictray.app` as a **Domain** property and verify with the DNS TXT record Google provides.
   This is possible now that the domain is ours — it was not possible on `github.io`.
2. Submit `sitemap.xml`.
3. URL Inspection → **Request indexing** for `/`, `/drivers.html`, `/v3.html`. Editing files does not
   force a recrawl. Google keeps serving the stale description until it re-reads the pages.
4. Repeat in Bing Webmaster Tools. Bing feeds several AI answer engines.

## Structured data on the site

- `docs/index.html` — `SoftwareApplication`, `WebSite`, `FAQPage`
- `docs/drivers.html` — `WebPage` plus a `HowTo` for reading the hardware id
- `docs/v3.html` — `WebPage` plus `FAQPage` for the 2024-versus-Magic-Mouse-2 question

Validate after any edit: <https://search.google.com/test/rich-results?url=https%3A%2F%2Fmagictray.app%2F>

## Still the biggest lever

Structured data tells Google *what* a page is. Links decide whether it ranks. The Magic Mouse scroll
threads on r/apple, r/MacOSBootCamp, and the GitHub issues about 2024 Magic Mouse scrolling currently
link only Magic Utilities.

## Do not

- Do not stuff keywords into the pages.
- Do not claim WHQL, Microsoft signing, or Secure Boot support before the driver is actually signed.
- Do not fight the Etsy results. Different product category; `disambiguatingDescription` is the answer.

## Verify the description is fixed

Search "magic tray windows app" again after a recrawl. A correct answer names magictray.app, says
Windows 10 and 11, and lists mouse, keyboard, and trackpad battery plus the Magic Mouse scroll driver.
