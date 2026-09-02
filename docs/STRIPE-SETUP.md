# Stripe Payment Link (optional, off GitHub Sponsors)

GitHub Sponsors is submitted and waiting on staff. You do not need a separate Stripe Payment Link for Sponsors to go live.

Use this file only if you also want a <code>buy.stripe.com</code> link. Cards never go through the tray app.


1. Open [https://dashboard.stripe.com/register](https://dashboard.stripe.com/register) (real `stripe.com` only). Enable 2FA.
2. Complete identity (you or Revive Business Solutions) and a payout account.
3. Product catalog → add product: **Magic Mouse v3 driver signing**  
   Description: one-time contribution toward an EV code-signing certificate and Microsoft Hardware Dev Center attestation. MIT driver, no scroll kill.
4. **Payment Links** → New → that product (one-time; optional recurring later).
5. Copy `https://buy.stripe.com/…`.
6. Tell an agent: replace `.github/FUNDING.yml` `custom:` with that URL, and put the same href on `docs/funding.html` as the primary CTA.

Until step 5 exists, FUNDING.yml points at https://lesleymurfin.github.io/magic-tray/funding.html (stars + goal). Do not commit Stripe secret keys.
