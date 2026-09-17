# Product Requirements Document: Community-Funded Driver Certification Hub

**Version:** 1.0  
**Date:** 2026-09-16  
**Status:** Ready for Development  
**Owner:** Lesley Murfin (@LesleyMurfin)

---

## Executive Summary

Build a transparent, community-funded platform to certify Apple device drivers (Magic Mouse, Magic Trackpad, Magic Keyboard) for Windows. Users fund driver certification via donations; site shows real-time progress toward WHQL certification and code signing milestones.

**Core Innovation:** Public roadmap + funding transparency. Users see exactly what drivers need, how much they cost, and how close we are.

---

## Goals

### Primary
1. **Reduce barrier to certification** – Community funding replaces developer's solo cost burden
2. **Build trust via transparency** – Show exactly where money goes, what it funds
3. **Maximize reach** – WHQL certification → Windows Update → millions of users
4. **Empower community** – Backers feel ownership; drivers exist because *they* funded it

### Secondary
1. **Establish authority** – Compete with Magic Utilities; be the go-to for open drivers
2. **Create sustainable model** – Donors → certified drivers → more users → more funding
3. **Build network effects** – Cross-promote magictray.app ↔ drivers.magictray.app

---

## Out of Scope (For Now)

- Proprietary Magic Utilities features (e.g., macro recording, custom gestures)
- Windows ARM support (later)
- Localization beyond Spanish (Month 2+)
- Mobile app (defer to Year 2)
- OEM partnerships (strategic, Year 2+)

---

## Success Metrics

| Metric | Target | Measurement |
|--------|--------|-------------|
| **Users** | 100K+ in Year 1 | Download counter + GitHub stars |
| **Certified Drivers** | 3+ by Dec 2026 | Public roadmap; WHQL badges live |
| **Community Funded** | 80%+ of cert costs | Donation tracker |
| **Uptime** | 99.9% | Public status page |
| **Page Load** | < 2s (mobile) | Lighthouse CI |
| **Accessibility** | WCAG 2.1 AA | Automated audit + manual testing |
| **SEO** | Top 3 for "Magic Mouse Windows" | Organic traffic via Ahrefs |

---

## Technical Architecture

### Stack (DECIDED 2026-09-16)

**Shipped:** hand-written static HTML plus one shared stylesheet, served by GitHub Pages, with Cloudflare in front. No framework, no build step, no bundler, no Node runtime.

- **Frontend — ADW-001, DECIDED:** no framework. One hand-maintained HTML file per page and a single `site.css`. A content fix is a one-file commit and the deploy is the push.
- **Hosting — ADW-004, DECIDED:** GitHub Pages. The driver hub is `drivers.magictray.app`, served from the `magic-mouse-v3-windows-fix` repository's `docs/` with its own `CNAME`; `magictray.app` keeps its own pages in `magic-tray`. Cloudflare fronts both for DNS, caching and analytics.
- **Anything server-side** (donation webhooks, email) runs in a Cloudflare Worker, not in a framework's API routes, so the pages stay static files.

### Core Services

```
Frontend:
  - drivers.magictray.app (static HTML on GitHub Pages, Cloudflare in front)
  - magictray.app (improvements to existing)

Backend:
  - drivers.json (canonical source; GitHub API-synced)
  - Donation tracking (Stripe + OpenCollective webhooks)
  - Email service (Resend or SendGrid)

Data:
  - GitHub repos (source of truth for drivers)
  - Stripe (donations, invoicing)
  - OpenCollective (backer list)
```

### Security

- No cookies (except session, if needed)
- HTTPS only
- Content-Security-Policy enforced
- GitHub secrets for API keys (no .env in repo)
- Vulnerability disclosure policy (.well-known/security.txt)

---

## Content Pillars

### For Users

1. **Device Picker** – Simple "Which device do you have?" landing
2. **Driver Pages** – Download + specs + changelog + backers
3. **Funding Roadmap** – Public, visual, per-driver goals
4. **Knowledge Base** – Installation guides, troubleshooting, device ID
5. **Community** – Forum, Discord, GitHub Discussions
6. **Blog** – SEO-focused: driver comparisons, Windows compatibility, etc.
7. **Comparison** – vs. Magic Utilities, why choose ours
8. **Credits** – Backers, maintainers, contributors

### For Administrators

1. **Dashboard** – Funding status, downloads, users, errors
2. **Backer Management** – Recognition, tiers, email list
3. **Driver Management** – Upload, version, changelog
4. **Analytics** – Traffic, conversion, SEO rank

---

## Vertical Slices (Development Order)

Each slice = end-to-end feature, testable, deployable.

### Phase 1: Foundation (Week 1-2)
**Goal:** Core site structure + legal compliance

**Slice 1A:** Legal & Security (3 days)
- Privacy policy
- Terms of service
- Security policy + security.txt
- Vulnerability disclosure process
- GDPR compliance check

**Slice 1B:** Site Structure & Navigation (3 days)
- Homepage with device picker
- Navigation system
- Breadcrumbs
- Footer with links

**Slice 1C:** Accessibility Baseline (3 days)
- WCAG 2.1 AA audit
- Keyboard navigation
- Screen reader testing
- Color contrast fixes

**Slice 1D:** Performance Baseline (2 days)
- Lighthouse CI setup
- Core Web Vitals target
- Image optimization
- CSS/JS minification

---

### Phase 2: Driver Hub (Week 2-3)
**Goal:** Functional driver pages + downloads

**Slice 2A:** Driver Data Model (2 days)
- drivers.json schema
- GitHub API sync
- Version management
- Changelog structure

**Slice 2B:** Device Picker Page (3 days)
- 7 device cards (Magic Mouse v3, v2, v1, Trackpad, Keyboard, etc.)
- One-click download
- Device ID helper tooltip
- Responsive design

**Slice 2C:** Driver Detail Page (3 days)
- Driver specs (OS, date, filesize)
- Installation instructions
- Changelog
- Links to GitHub
- Screenshots/badges

**Slice 2D:** Download & Tracking (2 days)
- Stripe/Segment event tracking
- Download counter
- Mirror/CDN setup
- Virus Total badge

---

### Phase 3: Funding System (Week 3-4)
**Goal:** Full funding workflow + transparency

**Slice 3A:** Donation Integration (3 days)
- Stripe payment form
- OpenCollective API
- Webhook handlers
- Receipt emails

**Slice 3B:** Funding Roadmap Page (2 days)
- Visual progress bars
- Per-driver funding goals
- Timeline projections
- Backer tiers display

**Slice 3C:** Backer Recognition (2 days)
- Backers list on driver page
- Credits wall of fame
- Tier badges
- Email to backers on certification

**Slice 3D:** Funding Transparency (2 days)
- Budget breakdown page
- Certification cost explainer
- Monthly expense report
- Profit/loss tracker

---

### Phase 4: Community & Content (Week 4-5)
**Goal:** User engagement + SEO

**Slice 4A:** Knowledge Base (3 days)
- 5 starter articles (installation, troubleshooting, device ID, comparison, FAQ)
- Markdown-to-HTML
- Search functionality
- Related articles

**Slice 4B:** Community Forum Setup (2 days)
- GitHub Discussions integration
- Discord server setup
- Forum guidelines
- Moderation tools

**Slice 4C:** Email Notification System (2 days)
- Signup form on download page
- Email templating
- Unsubscribe link
- GDPR compliance

**Slice 4D:** Blog & SEO Content (3 days)
- Blog post structure
- Author bios
- Comment system
- Open Graph tags

---

### Phase 5: Monitoring & Polish (Week 5-6)
**Goal:** Production readiness

**Slice 5A:** Monitoring & Alerts (2 days)
- Sentry error tracking
- Grafana dashboards
- Uptime monitoring (Statuspage.io)
- Slack alerts

**Slice 5B:** User Feedback System (2 days)
- Feedback form widget
- NPS tracking
- Feature request voting
- Bug report aggregation

**Slice 5C:** Competitive Analysis Page (1 day)
- vs. Magic Utilities comparison
- Feature matrix
- Migration guide

**Slice 5D:** Windows Integration (3 days)
- Winget package
- GitHub releases automation
- Auto-update checks
- Task Scheduler integration

---

### Phase 6: Enterprise & Localization (Week 6-7)
**Goal:** B2B support + broader reach

**Slice 6A:** Enterprise Deployment Guide (2 days)
- IT admin docs
- SCCM/Intune guides
- Group Policy settings
- PowerShell scripts

**Slice 6B:** Spanish Localization (i18n) (3 days)
- Translation workflow
- URL structure (/en/, /es/)
- RTL support structure
- Translation service setup

**Slice 6C:** Public Status Page (1 day)
- Uptime tracking
- Incident history
- Component status
- SLA dashboard

**Slice 6D:** Developer API Docs (2 days)
- drivers.json schema docs
- API endpoints
- Contributing guide
- Architecture diagrams

---

## Test-Driven Development (TDD)

Every slice follows TDD pattern:

### Test Types

1. **Unit Tests** (100+ per slice)
   - Functions, components, utilities
   - Input validation
   - Error handling

2. **Integration Tests** (20+ per slice)
   - API calls
   - Database queries
   - Webhooks
   - Email sending

3. **E2E Tests** (5-10 per slice)
   - User workflows
   - Download process
   - Donation flow
   - Backer display

4. **Accessibility Tests** (10+ per slice)
   - WCAG violations
   - Keyboard nav
   - Screen reader
   - Color contrast

5. **Performance Tests** (per slice)
   - Load time < 2s (mobile)
   - Core Web Vitals
   - Bundle size < 80KB
   - Lighthouse score > 90

### Test Discipline

**BEFORE writing code:**
1. Write failing test (RED)
2. Write minimal code (GREEN)
3. Refactor (REFACTOR)

**BEFORE shipping slice:**
1. All tests pass
2. No new console errors
3. Lighthouse > 90
4. Accessibility audit passes
5. Manual user test (with real user if possible)

---

## Definition of Done (per slice)

- [ ] All tests pass (unit, integration, e2e, a11y, perf)
- [ ] Code reviewed by at least 1 peer
- [ ] No console errors or warnings
- [ ] Lighthouse score ≥ 90
- [ ] WCAG 2.1 AA compliant
- [ ] Deployed to staging
- [ ] Manual testing by QA
- [ ] Documentation updated
- [ ] Commit message references slice ID
- [ ] PR merged to main

---

## ADW Process (Architectural Decision Workflow)

For each major decision:

1. **Decision Request** – What are we deciding? (e.g., "Payment processor?")
2. **Options** – List 3-4 concrete choices with tradeoffs
3. **Research** – Time box to 2 hours; gather evidence
4. **Recommendation** – Pick one; state reasoning
5. **Record** – Document in `DECISIONS.md`
6. **Proceed** – No re-litigating; move forward

### Decided

| # | Decision | Outcome | Reasoning |
|---|----------|---------|-----------|
| 1 | Frontend framework | None — hand-written static HTML + one stylesheet | A dozen content pages that change a few times a release. A framework or a generator would add a build step and a dependency tree to maintain, and a reader would see no difference. |
| 4 | Hosting | GitHub Pages, Cloudflare in front | Already serving both hosts at $0 with no runtime to operate. Cloudflare covers DNS, caching and analytics. |

### Decisions Needed

| # | Decision | Options | Owner | Deadline |
|---|----------|---------|-------|----------|
| 2 | Payment processor | Stripe, Paddle, Gumroad | Reviewer | Day 1 |
| 3 | Email service | Resend, SendGrid, Mailgun | Task | Day 2 |
| 5 | Database | PostgreSQL, SQLite, Airtable | Task | Day 2 |
| 6 | Forum platform | GitHub Discussions, Discord, Discourse | Reviewer | Day 3 |

---

## Risk Mitigation

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| **Donation bot fails** | Medium | High | Manual Stripe fallback; Lesley monitors |
| **GitHub API rate limits** | Low | Medium | Cache aggressively; use PAT |
| **WHQL cert denied** | Low | High | Have legal review submission; appeal ready |
| **Security breach** | Low | Critical | Bug bounty program; security.txt published |
| **Slow page load** | Medium | Medium | Performance budget (80KB); Lighthouse CI |
| **Low donations** | Medium | Medium | Email existing users; PR outreach; press |
| **Backer disputes** | Low | Medium | Clear terms; refund policy; dispute process |
| **Legal issues** | Low | High | Lawyer review; privacy policy audit; GDPR |

---

## Launch Checklist

**Before going live:**

- [ ] All Phase 1-2 slices complete (foundation + drivers)
- [ ] Legal pages live (privacy, terms, security policy)
- [ ] Accessibility audit passed (WCAG 2.1 AA)
- [ ] Performance baseline met (Lighthouse 90+)
- [ ] Domain configured (drivers.magictray.app)
- [ ] SSL certificate valid
- [ ] Email service working
- [ ] Stripe sandbox tested → production keys live
- [ ] GitHub Actions CI/CD working
- [ ] Sentry error tracking live
- [ ] Status page live
- [ ] Community channels live (Discord, GitHub Discussions)
- [ ] Beta testers invited (50+ users)
- [ ] Press release drafted
- [ ] Lesley ready for support emails

---

## Post-Launch Roadmap

### Month 1 (Oct 2026)
- Blog & knowledge base (Slice 4A)
- Community forum (Slice 4B)
- Email notifications (Slice 4C)
- Windows integration (Slice 5D)

### Month 2 (Nov 2026)
- Spanish localization (Slice 6B)
- Enterprise guides (Slice 6A)
- Second driver certification completes (funding milestone)
- User feedback system (Slice 5B)

### Month 3 (Dec 2026)
- Mobile app (scope TBD)
- Advanced analytics
- A/B testing framework
- Third driver certification completes (funding milestone)

---

## Budget & Team

### Estimated Costs

| Item | Cost | Timeline |
|------|------|----------|
| WHQL Certification | $3,500 | Community-funded |
| Code Signing Cert | $500/year | Donation-funded |
| Domain | $12/year | Core budget |
| Hosting | $0 (GitHub Pages) | Core budget |
| Email service | $0-50/month | Freemium tier |
| Monitoring | $0 (Sentry free tier) | Core budget |
| **Total** | **$4,012/year** | |

### Team

| Role | Person | Hours/Week |
|------|--------|-----------|
| **Maintainer** | Lesley Murfin | 35 (volunteer) |
| **Developer** | (contractor/volunteer) | 40 (TBD) |
| **QA** | (contractor/volunteer) | 20 (TBD) |
| **Legal** | (lawyer, once) | 8 |

---

## Success Criteria (Launch)

✅ Site live at drivers.magictray.app  
✅ Device picker works (7 devices, 1-click download)  
✅ First driver (Magic Mouse v3 KMDF) downloadable  
✅ Funding page shows roadmap  
✅ Donation system accepts first $100  
✅ Zero console errors  
✅ Lighthouse 90+  
✅ WCAG 2.1 AA audit passed  
✅ Legal pages live  
✅ 100 users in first week  
✅ 0 critical bugs in first month  

---

## Appendices

### A. Slice Acceptance Criteria Template

```markdown
## Slice 2B: Device Picker Page

### What
Homepage with visual device picker. User selects their device, sees specs, 
downloads driver in one click.

### How
- 7 device cards (Magic Mouse v3, v2, v1, Trackpad, Keyboard, others)
- Responsive grid (3 columns desktop, 1 column mobile)
- One-click download button per device
- Device ID helper (tooltip, link to identification guide)
- Fallback text if device not found ("Don't see your device? Submit a request")

### Tests
- Unit: Device card renders with correct props
- Integration: Download button calls correct API endpoint
- E2E: User can click device → see specs → download
- A11y: Keyboard nav works (Tab to device, Enter to download)
- Performance: Page loads in < 2s; Lighthouse 90+

### Acceptance
- [ ] All tests pass
- [ ] Responsive on mobile/tablet/desktop
- [ ] Keyboard navigation works
- [ ] No console errors
- [ ] Lighthouse score 90+
```

### B. Decisions Log — ADW-001 as recorded (use its shape for the rest)

```markdown
## ADW-001: Frontend Framework

**Status:** DECIDED  
**Date:** 2026-09-16  
**Owner:** @scout

### Decision
No framework. Hand-written static HTML, one shared stylesheet, no build step.

### Options Considered
1. **Hand-written static HTML** – Nothing to compile; edit the page, push, done. Con: shared markup is copied by hand.
2. **Static site generator (Hugo/11ty)** – Templates and partials. Con: a toolchain and a build step for a site this size.
3. **Next.js** – React SSR, API routes. Con: a Node runtime, a bundler, and a dependency tree to keep patched.

### Reasoning
The site is about a dozen content pages that change a few times per release.
Hand-written HTML means:
- No build to break, so a content fix is a one-file commit
- No dependency tree to patch on a solo-maintainer budget
- Pages stay fast and readable with no JavaScript at all
- GitHub Pages serves the repository directory as it stands

Server-side work (donation webhooks, email) goes in a Cloudflare Worker, so it
does not pull a framework into the pages.

### Risks
- Shared markup (nav, footer, JSON-LD) is duplicated across pages by hand
- With no templating, a site-wide change touches every page

### Mitigation
- `scripts/check-site.ps1` enforces the per-page invariants templating would have guaranteed
- Keep the page count small; add a page only when it owns a distinct question

### Decided
Static HTML on GitHub Pages, Cloudflare in front. Record in codebase as
`frontend-framework: none`.
```

---

**End of PRD**
