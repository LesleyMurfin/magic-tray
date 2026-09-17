# Cloudflare edge configuration — magictray.app

`magictray.app` is GitHub Pages behind Cloudflare. Pages cannot emit custom response headers, so
every security header and cache rule on this site is Cloudflare zone configuration. None of it
lives in this repository, which is why a bad value can sit in production for weeks without
appearing in any diff or review. This file is the record. **Change the dashboard, then change
this file in the same pull request.**

The `drivers.magictray.app` hub is served from the `magic-mouse-v3-windows-fix` repository but
sits on this same Cloudflare zone, so zone-level rules below cover both hosts.

---

## 1. Content-Security-Policy

Set via **Rules → Transform Rules → Modify Response Header**, applied to all requests on the zone.

### Required value

```
default-src 'none'; script-src 'self' https://static.cloudflareinsights.com; connect-src 'self' https://cloudflareinsights.com; style-src 'self'; style-src-attr 'unsafe-inline'; img-src 'self' data:; font-src 'self'; form-action 'none'; base-uri 'none'; frame-ancestors 'none'; upgrade-insecure-requests
```

### What was wrong, and why each directive is here

The value in production as of 2026-09-16 was:

```
default-src 'none'; script-src 'none'; style-src 'self'; style-src-attr 'unsafe-inline'; img-src 'self' data:; form-action 'none'; base-uri 'none'; frame-ancestors 'none'; upgrade-insecure-requests
```

Three defects:

1. **`script-src 'none'` killed analytics.** Cloudflare Web Analytics is enabled on this zone and
   the edge injects `static.cloudflareinsights.com/beacon.min.js` into every HTML response. The
   policy then blocked the script it had just injected. The site has therefore collected **zero**
   analytics data for as long as both settings have coexisted, while `PRD.md` lists organic
   traffic and SEO rank as success metrics. Verified over CDP: the beacon is present in the raw
   response body and blocked at execution.

   If you would rather not run analytics, that is a legitimate choice — but then turn Web
   Analytics **off** in the dashboard and delete the traffic metrics from the PRD. Do not leave an
   injected script and a policy that blocks it.

2. **No `font-src` — a latent break, not a live one.** With `font-src` absent it inherits
   `default-src 'none'`, so every `@font-face` fetch is blocked. This did not show up in
   production only because the deployed stylesheet was a stale copy containing zero `@font-face`
   rules. The current `docs/site.css` self-hosts four `.woff2` subsets across twelve `@font-face`
   rules, so **fixing the cache problem in section 2 without adding `font-src 'self'` first would
   swap a stale-CSS bug for a blocked-font bug.** Order matters: CSP first, then purge.

3. **`style-src 'self'` blocked a Google Fonts `@import`.** This one is already fixed in the
   repository — fonts were migrated to self-hosting under `docs/fonts/` — but the fix had not
   reached the edge. See section 2. No CSP change is needed for it; do **not** add
   `fonts.googleapis.com` to the policy, as that would undo the self-hosting migration.

`connect-src` is required alongside `script-src` because the beacon POSTs its payload back to
`cloudflareinsights.com`; allowing the script without the connection yields a silent reporting
failure that looks like working analytics.

### Verification

```sh
curl -sI https://magictray.app/ | grep -i content-security-policy
```

Then load the page in a browser with devtools open. Expected: **zero** console errors, and a
request to `static.cloudflareinsights.com` with status 200 rather than `(blocked:csp)`.

---

## 2. Cache rule for `site.css`

Set via **Caching → Cache Rules**.

| Field | Value |
|---|---|
| Match | `URI Path` equals `/site.css` |
| Edge TTL | 5 minutes |

### Why

`site.css` is served at an unversioned URL with `cache-control: max-age=604800` — seven days.
There is no build step and no content hash in the filename, so the edge had no way to learn the
file had changed. On 2026-09-16 the edge was serving the **7 September** stylesheet against
**16 September** HTML:

```
GET /site.css          -> last-modified: Sep 07   cf-cache-status: HIT   age: 45620   @font-face: 0
GET /site.css?v=bust   -> last-modified: Sep 16   age: 0                              @font-face: 12
```

The consequence was that the entire site rendered without its webfonts, and 286 lines of CSS —
including sticky table headers and horizontal scroll shadows — were simply absent from
production. Every CSS change was invisible to returning visitors for up to a week.

A five-minute edge TTL was chosen over hashing the filename deliberately. This is eleven
hand-maintained HTML files with no build pipeline; content-hashed asset names would require a
build step or eleven manual edits per stylesheet change, and would rot the first time someone
forgot. A short TTL costs one extra origin fetch every five minutes and needs no discipline from
anyone.

### One-time purge

After the rule is saved, **Caching → Configuration → Purge Everything**, once. The rule governs
future revalidation; it does not evict the copy already sitting at the edge.

### STILL OUTSTANDING: browser TTL

Applied and verified 2026-09-16. The edge is now correct:

```
GET /site.css -> last-modified: Wed, 16 Sep 2026   cf-cache-status: MISS   age: 0   @font-face: 12
```

With the browser cache bypassed the homepage renders with **zero console errors**, twelve
`@font-face` rules registered and the self-hosted fonts loading. The analytics beacon now
returns 200 instead of being CSP-blocked.

But the response still carries `cache-control: max-age=604800`. The Cache Rule set the **edge**
TTL; it did not change what the origin tells **browsers**. A returning visitor therefore keeps
the old stylesheet in their own cache for up to seven days, and on a normal (non-bypassed) load
today that is exactly what happens — the stale CSS with its Google Fonts `@import` is still
served from local disk and still throws the CSP error.

Fix, in the same Cache Rule: set **Browser TTL** to 5 minutes, or override the `cache-control`
response header to `max-age=300`. Without it the edge is fresh and returning visitors are not,
which is the harder version of the bug to notice because it never reproduces on a hard refresh.

---

## 3. Headers already correct

Verified present on 2026-09-16, no action needed. Listed so a future audit does not re-derive them:

| Header | Value |
|---|---|
| `strict-transport-security` | `max-age=31536000; includeSubDomains; preload` |
| `x-content-type-options` | `nosniff` |
| `x-frame-options` | `DENY` |
| `referrer-policy` | `strict-origin-when-cross-origin` |
| `permissions-policy` | `geolocation=(), camera=(), microphone=(), payment=(), usb=(), midi=(), serial=(), bluetooth=()` |
| `cross-origin-opener-policy` | `same-origin` |

`access-control-allow-origin: *` is set on all responses. Harmless for a fully public static site
with no credentialed endpoints, and there are no dynamic endpoints on this zone. If that ever
changes, narrow it before they ship.

---

## 4. API token scope

For automating the above, a token restricted to the `magictray.app` zone needs:

- Zone → Transform Rules → **Edit**
- Zone → Cache Rules → **Edit**
- Zone → Cache Purge → **Purge**
- Zone → Zone Settings → **Read**

Do not commit the token and do not add it to this repository. This is a static site with no
build step and no server-side code, so nothing here consumes Cloudflare credentials — the token
belongs in the operator's environment only.
