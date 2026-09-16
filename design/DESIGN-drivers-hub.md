# DESIGN — Community-funded driver certification hub

Status: **Proposal.** Nothing here is built, nothing is deployed, and no donation has been taken.
Date: 2026-09-16. Owner: Lesley Murfin (@LesleyMurfin).
No code accompanies this document — it is a written proposal for work that has not started.

Supersedes the root `PRD.md` (deleted with this document), along with the `wrangler.toml`,
`package.json`, and `.env.example` that shipped beside it. Deploy configuration lands with the
`src/` it configures, not before: the Worker entrypoint does not exist yet, and the Workers KV
identifiers in that file were namespace names rather than the hex ids Cloudflare issues, which
only the account owner can read.

## The problem

The 2024 Magic Mouse (`0323`) needs a driver that Windows will load without Test Mode and without
turning HVCI off. `README.md:345-356` sets out why: signing secrets are empty, the app ships
unsigned, and the driver route costs money the project does not have. The current honest answer on
the site is `docs/funding.html:177` — "Magic Tray has no sponsor button yet. GitHub hasn't approved
the page" — and `README.md:19` offers the funding page as "help without paying, because there is
nothing to pay into yet."

So there are two gaps, and only one of them is a code gap:

1. A visitor who wants the driver signed has no way to contribute, and no way to see what signing
   would cost or how far along it is.
2. The driver content is spread over one page (`docs/drivers.html`) that is already carrying more
   than one device's worth of instructions.

This design covers both: a small set of pages under `magictray.app/drivers/`, and the one piece of
server-side machinery a donation total needs.

## Goals

1. **Let the community pay for signing instead of the maintainer paying alone.** A donor can pay
   and can see the total.
2. **Make the money legible.** What a certificate costs, what has come in, what it was spent on.
   Legible means sourced — see "Costs", below.
3. **Keep every claim true.** The site never says a driver is signed before it is signed.
4. **Split the driver content by device without splitting the site.** One host, one deployment.

## Not in scope

- Magic Utilities' proprietary features (gestures, remaps, custom click areas) — see
  `design/DESIGN-mu-free-parity.md`.
- Windows on ARM.
- Localization. The site is English only; there is no i18n workflow in the repo and this design does
  not add one.
- A mobile app.
- OEM partnerships.
- A forum, a Discord, a blog, a public status page, a feedback widget, or an admin dashboard. Each
  is a standing support obligation for a volunteer project; GitHub Issues and Discussions already
  exist and cost nothing to keep.

## Architecture

One host, one deployment, one Worker. This is the repo's existing decision, not a fresh choice:
`docs/SEARCH.md:129-132` records that if the driver content outgrows one page, the move is
`magictray.app/drivers/mouse-2024.html`-style paths on this host.

Static pages are served by GitHub Pages out of `docs/`, exactly as every page ships today. A
Cloudflare Worker answers `/api/*` and receives the Stripe webhook, because GitHub Pages serves
static files only and cannot receive a webhook or hold a running total. Nothing else is added: no
Node server, no database, no second hosting provider, no framework build step. The pages are
hand-written HTML in the same style as `docs/drivers.html`.

```
  visitor
    |
    v
+----------------------------------------------------+
|  GitHub Pages - magictray.app (served from docs/)  |
|  one custom domain, one CNAME, one sitemap         |
|                                                    |
|    /drivers.html              (ships today)        |
|    /drivers/mouse-2024.html   (new)                |
|    /drivers/fund.html         (new)                |
+--------------------------+-------------------------+
                           |
                           |  fetch GET /api/funding
                           v
              +-------------------------------+        +--------------------+
              |  Cloudflare Worker            | -----> |  Workers KV        |
              |    GET  /api/funding          | <----- |  totals, backers,  |
              |    POST /api/stripe-webhook   |        |  last-updated      |
              +---------------+---------------+        +--------------------+
                              ^
                              |  signed webhook: checkout.session.completed
                              |
                      +-------+--------+
                      |     Stripe     | <--- donor pays on a Stripe-hosted page
                      +----------------+
```

Request flow, stated plainly:

- The donate button is a link to a Stripe-hosted payment page. Card details never touch
  `magictray.app` and never touch the tray app. That Payment Link already exists; it is simply not
  referenced from the repo yet — see "What is already set up, and what is not", below.
- Stripe posts `checkout.session.completed` to the Worker. The Worker verifies the Stripe signature,
  rejects anything unsigned, and increments a total in KV.
- `/drivers/fund.html` loads static, then fetches `/api/funding` and fills in the total. With
  JavaScript off or the Worker down, the page still reads correctly — the goal, the cost lines, and
  the "no figure published yet" statement are in the HTML.

### Prerequisite: the apex must be proxied

A Worker route on `magictray.app/api/*` only fires if the apex records are Proxied. Today every
record is DNS-only by deliberate choice (`docs/SEARCH.md:53-61`), because a proxy in front of a
Pages site without a certificate blocks GitHub's certificate validation. That same section says the
proxy may be switched on once HTTPS is enforced on GitHub, with SSL/TLS mode Full (strict). So the
sequence is: certificate issued and HTTPS enforced first, then flip the apex to Proxied, then add
the Worker route. Until the flip, the Worker is only reachable on its `workers.dev` hostname, which
makes the `fetch` cross-origin and needs CORS — usable for development, not the shipping shape.

### What goes in KV

Three keys, all written by the Worker and only ever read by `GET /api/funding`:

| Key | Holds |
|---|---|
| `total` | Sum of completed contributions, in cents, as an integer. |
| `updated` | ISO timestamp of the last webhook the Worker accepted. |
| `backers` | Count only. No names, no emails, no amounts per person, unless and until a donor-facing consent line exists. |

No cookies, no analytics, no third-party tracker on any page. That is the existing site's posture
and this design does not change it.

## Pages

The work extends the pages that already ship. It does not build a parallel site.

| Page | State | Work |
|---|---|---|
| `docs/drivers.html` | Ships | Becomes the index for the device routes; keeps its URL and its ranking history. |
| `docs/devices.html` | Ships | Already answers "which device do I own"; the new pages link into it rather than repeating it. |
| `docs/tested.html` | Ships | Stays the record of what has actually been tested. Certification status links here. |
| `docs/funding.html` | Ships | Keeps the plain-language "why is it unsigned" explanation. Gains the donate link when there is one. |
| `docs/magic-mouse-2024.html` | Ships | Already the device page for `0323`; the driver route links to it, no duplicate copy. |
| `docs/drivers/mouse-2024.html` | New | Per-device driver route, in the pattern `docs/SEARCH.md:131` names. |
| `docs/drivers/fund.html` | New | Cost lines, current total from `/api/funding`, what the money buys. |

A route earns a URL when there is something true to put on it, not when it completes a pattern
(`docs/SEARCH.md:129-130`). Keyboard and trackpad routes wait until they have content; `0265` and
`0324` have no installer at all (`design/DESIGN-trackpad-where.md:68-72`).

## The one open decision: drivers.magictray.app

This needs the owner and cannot be settled inside this document.

`docs/SEARCH.md:109-132` is a shipped, written decision titled "Why not drivers.magictray.app". A
`drivers.` subdomain "was considered and rejected, on two counts":

1. "It splits the site in two. Google evaluates a subdomain as its own host. The trust that
   `magictray.app` has accumulated does not transfer in full, and a brand-new host starts cold."
2. "GitHub Pages allows one custom domain per site. `docs/CNAME` holds exactly one hostname, so a
   second host means a second repository, a second Pages deployment, a second certificate, a second
   Search Console property, and a second sitemap — for the same handful of pages."

Adopting the subdomain would mean superseding that decision in writing, with reasons that answer
both counts, and accepting the second repository and second deployment that count 2 describes.
That is the owner's call.

Until it is superseded, this design follows the recorded decision: apex host, `/drivers/` paths,
one deployment. Every page and URL above assumes that. Nothing in the Worker design depends on the
answer — a Worker route can sit on either hostname — so the decision can be deferred without
blocking the funding work.

## What this project may not claim

`docs/SEARCH.md:355`: "Do not claim WHQL, Microsoft signing, or Secure Boot support until the
driver is actually signed." That holds for every page, every metric, and every donor-facing
sentence here. There is no "signing achieved" milestone to display, because none has happened.

The honest description of the ladder is the one in `README.md:349-354`:

| Level | What it takes | What it removes |
|---|---|---|
| Unsigned (today) | nothing | nothing. SmartScreen warns on the app; the 2024-mouse driver needs test signing on and HVCI off |
| Authenticode OV or EV | a code-signing certificate from a public CA in a verified name, renewed yearly | the app's SmartScreen warning. EV clears it immediately, OV needs reputation to build |
| Driver attestation | an EV certificate plus a Microsoft Partner Center account | lets the driver load with no test signing and no HVCI change |
| WHQL | attestation plus passing the Hardware Lab Kit test suite | Windows Update distribution |

The funding pages describe the ladder as what donations are *for*, in the future tense, and state
which rung the project is on: the first one.

## Funding transparency

The concept is the product. A donor should be able to answer three questions from one page: what
does the next rung cost, how much has come in, and what was the last thing the money paid for.

- The total on the page is the Worker's KV total, timestamped, and labelled as gross before Stripe
  fees.
- Spending is recorded as it happens, with the receipt amount, after it happens. No forecast
  spending, no projected dates.
- Until a cost line has a written quote behind it, the page says what the line is and that it has no
  figure yet. It does not show a placeholder number.

## Costs

The previous draft published a budget that had no source in the repo and did not add up: it treated
a one-time submission fee as annual and omitted its own email line from the total. No figure in
this design is published to donors without a written vendor quote behind it.

The cost lines that genuinely exist:

| Line | Shape | Source needed before publishing a figure |
|---|---|---|
| Code-signing certificate (EV) | Recurring, yearly | Written quote from a public CA, in the verified name that will hold the certificate. |
| Microsoft Partner Center / attestation | Account plus per-submission | Quote or published fee schedule captured at the time, with the date. |
| Hardware Lab Kit test pass | One-time per submission, only if the project goes past attestation | Quote. Not a goal yet; attestation is the near rung. |
| Domain | Recurring, yearly | Registrar renewal price, already known to the owner. |
| Hosting | GitHub Pages, no charge at this scale | None. Worker and KV are within free-tier limits at this traffic; if that changes, it becomes a line. |
| Email sending | Only if receipt or notification email is added | Quote, or drop the feature. |

Hours are not budgeted. This is volunteer work by one maintainer and cannot be scheduled against a
staffing table.

## Risks

| Risk | Mitigation |
|---|---|
| Webhook missed or replayed, so the total is wrong | Verify the Stripe signature, key the increment by event id, and treat Stripe's dashboard as the source of truth. The page total is a display, and says so. |
| Donations arrive and signing never happens | Say so before taking money: what happens to funds that do not reach a submission is an open question below, and must be answered before the donate link goes live. |
| A claim outruns reality | The claim ban above. Any page text asserting signing status is reviewed against `docs/tested.html`. |
| Apex proxy flip breaks the live site | The flip happens after the certificate is issued and HTTPS enforced, per `docs/SEARCH.md:53-61`, and is reversible. |
| Donor dispute or chargeback | Stripe handles the mechanism. The terms it is disputed against do not exist yet — open question below. |
| Worker or KV unavailable | The pages render fully without `/api/funding`. No uptime target is promised to anyone; a volunteer project cannot honour one, so no status page and no service commitment. |

## What is already set up, and what is not

Stripe itself is done: the account, identity verification and the Payment Link exist. That covers
steps 1-5 of `design/STRIPE-SETUP.md`, and it settles the question that file left open at line 9 —
a Stripe account cannot activate without a verified identity, so the entity that receives the money
is decided.

What never happened is step 6: **the link is not in the repo.** `.github/FUNDING.yml` still sets
`custom:` to `https://magictray.app/funding.html`, and `docs/funding.html:177` still tells visitors
"Magic Tray has no sponsor button yet." So the shipped site cannot take a contribution even though
Stripe can. Wiring it is a two-file edit — `custom:` in `.github/FUNDING.yml` and the primary CTA on
`docs/funding.html` — needing only the `buy.stripe.com/...` URL pasted from the Dashboard. It needs
no design and does not belong to the hub work; it is worth doing on its own, before any of this.

## Open questions

These are policy, not plumbing, and none of them can be answered by this document.

1. **Refunds.** Whether contributions are refundable, and until when. No policy is invented here.
2. **Donor terms.** What a contributor is told they are buying, what happens if a submission fails
   or never happens, and what happens to a surplus.
3. **Receipts.** Stripe's own receipt is the only receipt this design assumes. Nothing about tax
   deductibility is claimed and no tax identifier is collected or displayed; the project is not a
   registered charity.
4. **The subdomain**, as above.

## Launch criteria

These are the conditions for the funding pages going live. **None of them is met today** — no page
exists, no Worker exists, no donation has been taken.

- Open questions 1 and 2 answered in writing.
- The `buy.stripe.com` link is wired into `.github/FUNDING.yml` and `docs/funding.html`, and one
  test-mode payment has run end to end into KV.
- The Worker verifies webhook signatures and rejects an unsigned POST.
- `/drivers/fund.html` renders correct and complete with JavaScript disabled.
- Every cost figure shown has a quote behind it, or is shown as a line with no figure.
- No page asserts signing, attestation, or Secure Boot support.
- Every new page is in `sitemap.xml`, keyboard-navigable, and readable at mobile widths, matching
  the existing pages rather than a new standard.

## Recording decisions

Architecture decisions in this repo go in `design/adr/` as `ADR-NNNN-<slug>.md` with a matching
`# ADR-NNNN — Title` heading and an index entry in `design/adr/README.md`; the numbering and index
are enforced by `.ai/validators/adr-number-check.py`. That directory does not exist yet, so the
first ADR written creates it along with its index. This design does not create it, and the
subdomain question is the obvious candidate for ADR-0001 — written by whoever decides it, not here.

## Work order

Three slices, in dependency order. No dates: there is no anchor date and no staffing to schedule
against.

1. **Pages, static only.** `docs/drivers/mouse-2024.html` and `docs/drivers/fund.html`, both fully
   readable with no Worker and no JavaScript. `docs/drivers.html` becomes the index. Sitemap
   updated. This slice ships value on its own and is safe to land before any money question is
   settled, provided the fund page shows cost lines without figures.
2. **Worker and KV.** `GET /api/funding` and the signed Stripe webhook, `wrangler.toml` written
   against the real KV namespace ids from the owner's account, deployed to a `workers.dev` hostname
   first and exercised with Stripe test mode. No production key, no live link.
3. **Go live.** Apex proxied, Worker route added, production Stripe key, donate link on
   `docs/funding.html` replacing the "no sponsor button yet" line — only once the launch criteria
   above hold.

## Non-goals for this document

- Creating `design/adr/` or any ADR file.
- Committing deploy configuration ahead of the `src/` it configures.
- Publishing any cost figure that has no written quote behind it.
- Promising an availability percentage, a support response time, or a delivery date.
