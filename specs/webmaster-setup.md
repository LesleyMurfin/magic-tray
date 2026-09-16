# Webmaster setup: Google Search Console & Bing Webmaster Tools

Internal documentation. `specs/` is not published by GitHub Pages.

This records the state of search engine registration for `https://magictray.app/` following
the custom domain migration and AEO structured-data updates on **2026-09-16**.

---

## 1. Google Search Console

### Domain property verification (Completed)
- **Property:** `magictray.app` (Domain property covering all protocols and subdomains)
- **Status:** **Verified** via Cloudflare DNS integration.
- **Verification method:** Domain name provider (Cloudflare automated DNS integration).
- **DNS TXT record value written to Cloudflare:**
  ```text
  google-site-verification=VAknLZ-wOn5FE1cPEJhrK6sBREdYee3sybGMtwF6_hc
  ```
  *Note:* Do not delete or proxy this TXT record in Cloudflare DNS settings; keeping it active prevents verification lapses.

### Sitemap submission (Completed)
- **Submitted URL:** `https://magictray.app/sitemap.xml`
- **Submission Date:** 2026-09-16
- **Status in Search Console:** **Success**
- **Discovered URLs:** 10 URLs indexed from the sitemap
- **Errors:** 0

### Priority Indexing Requests
- **`https://magictray.app/`:** **Requested** via URL Inspection. Google confirmed:
  > *"URL was added to a priority crawl queue. Submitting a page multiple times will not change its queue position or priority."*

### Remaining Manual URL Inspection Requests (5-minute task)
Google limits automated/rapid URL inspection submissions. To expedite fresh crawling of the remaining key landing pages:
1. Open [Google Search Console](https://search.google.com/search-console?resource_id=sc-domain%3Amagictray.app).
2. Ensure property `sc-domain:magictray.app` is selected in the top-left dropdown.
3. Paste each of the following URLs into the top search bar ("Inspect any URL in 'magictray.app'"), press Enter, wait for the test to complete, and click **REQUEST INDEXING**:
   - `https://magictray.app/tested.html` (Newly published hardware confirmation table)
   - `https://magictray.app/drivers.html` (Driver routing and installation guides)
   - `https://magictray.app/v3.html` (2024 USB-C Magic Mouse deep-dive)
   - `https://magictray.app/battery.html` (Battery reading fixes and alert policy)

---

### Additional Managed Properties

#### `riley.team` (Domain property)
- **Status:** **Added and auto-verified** on 2026-09-16 via existing Cloudflare DNS integration.
- **Property:** `sc-domain:riley.team`
- **Sitemap Submitted:** `https://riley.team/sitemap.xml` (Submitted on 2026-09-16).
- **Priority Crawl:** URL Inspection run for `https://riley.team/`; live indexability test passed; indexing requested. Google confirmed:
  > *"URL was added to a priority crawl queue. Submitting a page multiple times will not change its queue position or priority."*

#### `revivebusiness.ca` (Domain property)
- **Status:** **Verified** Domain property (`sc-domain:revivebusiness.ca`).
- **Sitemap Submitted:** `https://revivebusiness.ca/sitemap.xml` (Submitted on 2026-09-16).
- **Priority Crawl:** URL Inspection run for `https://revivebusiness.ca/`; live indexability test passed; indexing requested. Google confirmed:
  > *"URL was added to a priority crawl queue. Submitting a page multiple times will not change its queue position or priority."*
---

## 2. Bing Webmaster Tools & Answer Engines

### Automated IndexNow (Active)
- Bing, Microsoft Copilot, and several AI answer engines consume site updates via **IndexNow**.
- `.github/workflows/indexnow.yml` is active and triggers on any push to `main` touching `docs/**`.
- It dynamically parses `docs/sitemap.xml` and submits all canonical URLs to `https://api.indexnow.org/indexnow` using the verified key at `docs/862b894a281ea8cdca5c46c788285279.txt`.
- No manual per-URL submission is required for Bing; pushes to `main` notify Bing within minutes.

### Optional Bing Webmaster Console Import
If you want to view impressions and queries in the Bing Webmaster dashboard:
1. Go to [Bing Webmaster Tools](https://www.bing.com/webmasters/).
2. Click **Import from Google Search Console**.
3. Sign in with the same Google account that owns `magictray.app`.
4. The verified domain property and sitemap will import automatically without needing extra DNS records.
