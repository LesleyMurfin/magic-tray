# Outreach: verified targets and paste-ready drafts

Internal. `specs/` is not published by Pages. This is the actionable half of the "Still the
biggest lever" section of `docs/SEARCH.md`: the threads are real, checked, and dated, and the
drafts are written so that posting is a ten-minute job.

Everything below was verified on **2026-09-15**. Every target URL was fetched and its title,
date and activity read off the live page. Nothing has been posted anywhere.

---

## How to use this

**Five drafts, five jobs.**

| Draft | Goes where | Form |
| --- | --- | --- |
| A — Reddit self-post | r/apple only, on a Sunday (see the rules table) | Title + body, text post |
| B — Thread reply | Reddit, Apple Community, Stack Exchange, any existing thread | One paragraph with a marked slot |
| C — Show HN | news.ycombinator.com, once, ever | Title + own first comment |
| D — GitHub issue comment | The nine GitHub issues in the target list | Comment |
| E — Photo request | v3 driver repo issues, the test-report thread, Reddit threads where the poster clearly owns a 2024 mouse | One paragraph, appended to a reply |

**Order.** Work bottom-up in risk. GitHub issue comments first — they are on-topic by
construction and the maintainers there are peers, not moderators. Then Stack Exchange answers,
then Apple Community replies, then Reddit thread replies, then the r/apple self-post, and Show HN
last. Show HN is a single shot; spend it once the site has been stable for a couple of weeks and
the winget manifest has landed, so the post has something new to say.

**Spacing.** One post per day, maximum. Never two posts in the same subreddit in the same week.
Never the same wording twice — identical text across threads is exactly what spam filters key on,
so the slot in Draft B is not optional decoration, it is the part that makes the comment a comment.
Leave two weeks between the last thread reply and the r/apple self-post.

**The rule that matters most: a thread reply answers the question before it mentions the site.**
The first sentence must be a direct answer to what that specific person asked, in their own terms,
and it must be useful even if they never click the link. One link, at the end, once. If you cannot
answer their question truthfully — if they want gestures, or key remapping, or 2024 scrolling
today — do not reply at all. A post that reads as an advert earns a removal and a negative signal,
which is worse than no post.

**Disclose authorship every time.** "I wrote it" or "disclosure: my project", in the body, not
buried. Every venue below either requires it or treats its absence as spam.

### Venue rules, read off the live sidebars on 2026-09-15

These were checked because they change what is possible, not as trivia.

| Venue | The rule that binds us |
| --- | --- |
| **r/apple** | Rule 9: self-promotion by developers is allowed **on Sundays, California time, self-post only**, and "activity requirements are enforced" — comment organically there first. Rule 8: no tech-support questions, so the self-post must be about the thing, not a help request. |
| **r/windows** | Rule 6: "Do not advertise your software or website without permission." Rule 10: no tech-support posts. So: message the mods, or do not post. No self-post without a reply from them. |
| **r/Windows11** | Rule 7: same permission requirement. Rule 2: no tech-support posts. |
| **r/WindowsHelp** | Rule 6: "Do not advertise a 3rd party software without permission." Replies that answer the question are the sub's purpose; a link needs mod permission, so lead with the answer and be ready to drop the link. |
| **r/applehelp** | Rule 6: "No spam / No self-promotion." Rule 2: top-level comments must answer the question. Replies only, answer-first, disclosure — and accept that a mod may still remove the link. |
| **r/software** | Rule 3: Spam/Self-Promotion. Rule 6: karma requirement. Replies only. |
| **r/mac**, **r/bootcamp**, **r/macsysadmin** | No self-promotion rule found in the sidebar. Still reply-only and still disclose. |
| **Stack Exchange** (Super User, Ask Different) | Self-promotion in an answer is allowed only with explicit disclosure of the affiliation, and the answer must stand on its own without the link. |
| **Apple Community** | Replies only. Links to third-party software are removed at moderator discretion; write the reply so it survives the link being stripped. |
| **Hacker News** | Show HN is for something people can try now. Magic Tray 1.1.0 is downloadable, so it qualifies. Post it, then answer in the comments; do not post a second one. |

**`r/MacOSBootCamp` does not exist.** `docs/SEARCH.md` names it as a venue; fetching
`reddit.com/r/MacOSBootCamp/` returns "We couldn't find that community" (checked both casings).
The live equivalent is **r/bootcamp**, which is where target 4 below lives. Worth fixing in
`SEARCH.md` next time that file is touched.

**Nobody has linked the site anywhere yet.** A GitHub search for `magictray.app` across all
issues returns 8 hits, all of them inside our own two repos. So none of the targets below is a
duplicate post, and none of them has already been answered by us.

---

## How the targets were ordered

Five questions, in this order. A target only beats another if it wins on an earlier question.

1. **Can we answer the exact question truthfully, today?** A thread where the honest answer is
   "here are the eight steps and they work" beats a thread where it is "here is why it doesn't
   work yet". This is why the keyboard-battery and v1/v2-scroll threads are at the top and the
   2024-mouse threads are in the middle, despite the 2024 mouse being the more distinctive query.
2. **Will anyone else ever read it?** Measured, not guessed: Stack Exchange view counts, Apple
   Community "Me too" counts, Reddit comment counts. 31,331 views beats 63 views.
3. **Is it alive?** Activity in the last twelve months, and open to new replies.
4. **Does the venue permit a link at all?** See the rules table.
5. **Is the current best answer paid-only or a dead end?** A thread whose only answer is "buy
   Magic Utilities" has room for a free, honest second answer. A thread that already has a working
   free answer does not need us.

"Helps?" below is deliberately graded. **Yes** = the site answers their question and the problem
goes away. **Partly** = we can explain the cause and solve a different part of their problem
(usually battery percent), but not the thing they asked about. Nothing is listed as Yes on the
strength of a feature that does not ship.

---

## The targets

### Tier 1 — we solve their problem, and the thread has traffic

**1. Super User 1254856 — "Can I check the battery life of my Apple Magic Keyboard on Windows 10?"**
<https://superuser.com/questions/1254856/can-i-check-the-battery-life-of-my-apple-magic-keyboard-on-windows-10>
Asked 2017-09-29, last activity 2020-10-02. **30,960 views**, 2 answers, answered.
Asks: Magic Keyboard on Windows 10, no way to tell how much battery is left short of waiting for
it to die. Helps? **Yes** — this is exactly `keyboard.html`. Highest-value single target on the
list: the question is evergreen, the view count is real, and the existing answers predate the
unlock approach. Answer with the mechanism, not just the download.

**2. r/applehelp 10a8gym — "Unable to see battery level on Windows (Magic Keyboard)"**
<https://www.reddit.com/r/applehelp/comments/10a8gym/unable_to_see_battery_level_on_windows_magic/>
Posted 2023-01-12, score 4, **13 comments**.
Asks: bought a Magic Keyboard with Numeric Keypad for Windows 11 Pro, cannot see the percentage,
found Magic Utilities and Bluetooth Battery Monitor "but they're both paid… Is there any other
free way/software?" Helps? **Yes** — the question is literally "is there a free one". Note the
model: Numeric Keypad variants are `029F`/`0322` in our catalogue, recognised but unconfirmed, so
this reply must say "should work, nobody has confirmed it yet" and ask for a report.

**3. Ask Different 335276 — "Apple Magic Mouse 2 not scrolling - Magic Mouse 1 works! (Windows 10 / PC)"**
<https://apple.stackexchange.com/questions/335276/apple-magic-mouse-2-not-scrolling-magic-mouse-1-works-windows-10-pc>
Asked 2018-08-31, last activity 2020-05-04. **31,331 views**, 3 answers, answered.
Asks: v1 scrolls on a Windows PC, the Lightning v2 does not. Helps? **Yes** — `0269` takes the
same Apple INF as `030D`; `drivers.html#v1v2` is the eight-step route, and `#stuck` covers the
case where the Driver tab still says Microsoft. Highest-traffic mouse thread we can answer
honestly. The one caveat to state: `0269` is in the catalogue but no tester has confirmed it.

**4. r/bootcamp 1qo95t9 — "Using the Apple Magic Keyboard with Windows 11"**
<https://www.reddit.com/r/bootcamp/comments/1qo95t9/using_the_apple_magic_keyboard_with_windows_11/>
Posted 2026-01-27, score 2, **22 comments**. Live and busy.
Asks: can a Magic Keyboard be used "fully and without limitations" on Windows 11 alongside a Mac.
Helps? **Yes, partly by design** — the honest answer is a split: typing and battery percent yes
(battery after the one-time unlock), media keys and remapping no, buy Magic Utilities for those.
This is the thread where the comparison table earns its keep.

**5. r/WindowsHelp 1ru6gla — "Magic Mouse and Magic Keyboard Setup for Windows 11"**
<https://www.reddit.com/r/WindowsHelp/comments/1ru6gla/magic_mouse_and_magic_keyboard_setup_for_windows/>
Posted 2026-03-15, score 1, 5 comments.
Asks: mouse and keyboard pair over Bluetooth but scroll does not work; found Magic Utilities, is
put off that it is third-party and asks for money. Helps? **Yes**, if the mouse is a v1/v2 — ask
the PID in the first line. Rule 6 binds here: answer fully, and treat the link as optional.

**6. r/mac 1ru6iat — "Magic Mouse and Magic Keyboard Setup for Windows 11"**
<https://www.reddit.com/r/mac/comments/1ru6iat/magic_mouse_and_magic_keyboard_setup_for_windows/>
Posted 2026-03-15, score 2, **8 comments**. Same person's crosspost of target 5, three minutes
apart. Helps? **Yes**, same answer. Reply to whichever of 5/6 has more traffic when you get there,
not both with the same text — the same comment under both crossposts is the clearest possible spam
signal.

**7. Ask Different 451649 — "Apple Magic Mouse 2 scrolling with Windows 11 PC"**
<https://apple.stackexchange.com/questions/451649/apple-magic-mouse-2-scrolling-with-windows-11-pc>
Asked 2022-12-08, last activity 2022-12-09. **0 answers**, 63 views, unanswered.
Asks: MM2 will not scroll on a non-Mac Windows 11 machine; Boot Camp itself will not install, the
driver "installs but does nothing", the GitHub driver will not install, and they explicitly ask
whether it is possible on 11 at all. Helps? **Yes**. Low traffic, but an unanswered question with
a specific, answerable failure mode is the cheapest durable answer on the list — and "does it work
on Windows 11" is the doubt the whole site exists to remove.

**8. Apple Community 254906129 — "Magic mouse scroll is not working in windows"**
<https://discussions.apple.com/thread/254906129>
Posted 2023-06-05. **105 "Me too"** — the largest audience signal of any thread here.
Asks: MM2 on a Windows laptop, already installed `AppleWirelessMouse64.exe` from Boot Camp,
already re-paired and reinstalled, still no scroll. Helps? **Yes** — this is `drivers.html#stuck`
almost line for line. Do not restate the install steps they have already done; go straight to the
Driver-tab check and the unpair/re-pair order.

**9. Apple Community 256185094 — "Enabling scrolling on Magic Mouse with Windows 11 Pro"**
<https://discussions.apple.com/thread/256185094>
Posted 2025-11-10. **64 "Me too"**. Recent.
Asks: Magic Mouse paired to a Windows 11 Pro laptop, tried the drivers linked in the FAQ, no
scroll. Helps? **Yes**, once the model is established — ask for the four hex digits after `PID_`
in the first line, because the answer forks completely at `0323`.

### Tier 2 — we explain the cause and solve the other half

These are the 2024 USB-C mouse threads. We cannot give these people scrolling today. What we can
give them is the reason (Apple's Windows package has no entry for `0323`), the fact that their
battery percent works with no driver at all, and an honest statement that the community driver is
not published. That is still the best answer in every one of these threads. Never imply a download
exists.

**10. r/applehelp 1tw4b9r — "Apple Mouse 3 (USB C, A3204) doesn't scroll in Windows 11"**
<https://www.reddit.com/r/applehelp/comments/1tw4b9r/apple_mouse_3_usb_c_a3204_doesnt_scroll_in/>
Posted 2026-06-03, score 1, **6 comments**. The most recent A3204 thread found.
Asks: MM2 worked on Windows 11 with `AppleWirelessMouse64.exe`; swapped to the USB-C model and
scroll died; tried many Boot Camp versions. Helps? **Partly** — they have already proved the
cause themselves, so the reply is confirmation plus the battery half. Best single candidate for
Draft E as well: this person definitely owns the mouse.

**11. Apple Community 255985309 — "How do I fix Magic Mouse scrolling issue on Windows 11 with bootcamp?"**
<https://discussions.apple.com/thread/255985309>
Posted 2025-02-25. 14 "Me too". Names **Model A3204** in the question.
Asks: newly purchased A3204 for a Windows 11 Home laptop, scroll does not work. Helps? **Partly**,
as above. The thread page also lists several "Similar questions" with the same problem, which is a
fair indicator of how often this is asked.

**12. r/applehelp 1hmre4r — "Scroll on new Magic Mouse not working"**
<https://www.reddit.com/r/applehelp/comments/1hmre4r/scroll_on_new_magic_mouse_not_working/>
Posted 2024-12-26, score 1, 6 comments. Names **Model A3204**.
Asks: bought an A3204 for a Windows 11 laptop, spent an afternoon on forum threads and videos that
made the driver install look easy, got nowhere. Helps? **Partly**. The value here is specifically
telling them the afternoon was not their fault.

**13. `sbagirici/apple-magic-mouse-scroll-fix-windows` #1 — "Support Apple Magic Mouse model A3204 (USB-C) 2024"**
<https://github.com/sbagirici/apple-magic-mouse-scroll-fix-windows/issues/1>
Opened 2026-03-05, last activity 2026-04-18, open, 1 comment.
Asks: add A3204 support, "since it's not supported by the latest bootcamp drivers". Helps?
**Partly**. Read the comment before replying: `Amirrasa` reports on 2026-04-18 that the script
worked on their black USB-C mouse. Do not contradict that — our claim is narrower (Apple's INF has
no `0323` entry, and *our* KMDF driver is not published). `v3.html` already credits and links this
repo, so this is a reply between two projects, not a pitch.

**14. `vitoplantamura/MagicTrackpad2ForWindows` #33 — "Support Apple Magic Mouse model A3204 (USB-C) 2024"**
<https://github.com/vitoplantamura/MagicTrackpad2ForWindows/issues/33>
Opened 2026-03-05, open, **3 comments**, maintainer replied.
Asks: same request. The maintainer says he has an MM2 lying around and does not rule out future
support; the requester clarifies that the problem is specifically the USB-C version. Helps?
**Partly**. This is the single best-placed link on the list — an active, well-regarded Windows
driver repo whose maintainer is engaged with the exact model. Reply with the `0323` detail
(`COL01`/`COL02`, the `LowerFilter` slot) because it is genuinely useful to him, and mention the
battery half once.

**15. `Rain9333/MagicMouse2DriversWin10x64` #12 — "2024 Magic Mouse (USB-C) Compatability"**
<https://github.com/Rain9333/MagicMouse2DriversWin10x64/issues/12>
Opened 2025-02-06, last activity 2026-03-05, open, 1 comment ("i hope they will add it soon
aswell"). Repo has **273 stars** and is one of the two the Reddit threads keep pointing at.
Asks: add USB-C model compatibility; tried Boot Camp and the MM2 drivers. Helps? **Partly**. High
authority host, two people waiting in the thread.

### Tier 3 — real questions, smaller or narrower

**16. `Rain9333/MagicMouse2DriversWin10x64` #2 — "Installed driver but scrolling doesn't work"**
<https://github.com/Rain9333/MagicMouse2DriversWin10x64/issues/2>
Opened 2020-07-07, last activity 2024-08-24, open, 3 comments.
Asks: disabled driver signing, installed from the INF, rebooted, MM2 multi-touch scroll still dead
on a Windows 10 Pro laptop that is not a Mac. Helps? **Yes** — and worth noting that disabling
signature enforcement is not needed for `0269` at all, which is a correction they will value.

**17. `Rain9333/MagicMouse2DriversWin10x64` #1 — "Error when Installing the AppleWirelessMouse"**
<https://github.com/Rain9333/MagicMouse2DriversWin10x64/issues/1>
Opened 2020-04-24, last activity 2021-03-10, open, **10 comments** — the busiest thread in that
repo.
Asks: right-click → Install gives "The hash for the file is not present in the specified catalog
file." Helps? **Partly** — we do not own that error, but the eight-step route on
`drivers.html#v1v2` uses the packaged download rather than a bare INF, which is a different path
to the same result. Only reply if you can say something specific about the catalogue mismatch;
otherwise skip.

**18. `vitoplantamura/MagicTrackpad2ForWindows` #25 — "Battery indicator"**
<https://github.com/vitoplantamura/MagicTrackpad2ForWindows/issues/25>
Opened 2025-10-24, last activity 2026-06-25, open, **8 comments**, maintainer active.
Asks: show trackpad battery status; a later commenter asks specifically for battery over USB.
Helps? **Partly** — Magic Tray reads Magic Trackpad 2 (`0265`) and the 2024 trackpad (`0324`)
battery and ships no trackpad driver at all, so this is complementary rather than competing, and
those two PIDs are unconfirmed, so it doubles as a tester ask. Say plainly that we offer no
gestures and no trackpad driver.

**Also live, lower value, same treatment if time allows:**

- **r/mac 1tt7ebg** — "Scrolling doesn't work on Magic Mouse on windows 11. Fix exists?", posted
  2026-05-31, 3 comments. <https://www.reddit.com/r/mac/comments/1tt7ebg/scrolling_doesnt_work_on_magic_mouse_on_windows/>
  Helps? **Yes or Partly** — no model stated, so the reply has to ask for the PID first.
- **r/macsysadmin 14guoqe** — "Magic Mouse", posted 2023-06-23, **40 comments**. Installed Apple's
  drivers, no scroll, no middle button, wants to know how to uninstall.
  <https://www.reddit.com/r/macsysadmin/comments/14guoqe/magic_mouse/> Helps? **Yes** — including
  the uninstall half, which is the `Stock Windows` menu item.
- **r/Keyboard 12dxecx** — "magic keyboard with windows 11 ( can't find battery level )", posted
  2023-04-06, 1 comment. <https://www.reddit.com/r/Keyboard/comments/12dxecx/magic_keyboard_with_windows_11_cant_find_battery/>
  Helps? **Yes**, but the thread is nearly dead; it is Touch ID with numeric keypad, unconfirmed.
- **r/software 9d6643** — "Free or one time purchase alternative to Magic Utilities", posted
  2018-09-05, **57 comments**. <https://www.reddit.com/r/software/comments/9d6643/free_or_one_time_purchase_alternative_to_magic/>
  Helps? **Partly** — they want "support for apple input devices", which for us means battery and
  the v1/v2 scroll route and nothing else. Old, but it still ranks for the query. Karma rule
  applies.
- **`vitoplantamura/MagicTrackpad2ForWindows` #14** — "Battery level reporting support", opened
  2024-12-27, 1 comment. <https://github.com/vitoplantamura/MagicTrackpad2ForWindows/issues/14>
  Helps? **Partly**, same as #18; reply to one of the two, not both.
- **`Rain9333/MagicMouse2DriversWin10x64` #8** — "Does not work with first generation of Magic
  Mouse", opened 2021-12-15. <https://github.com/Rain9333/MagicMouse2DriversWin10x64/issues/8>
  Helps? **Yes** for the uninstall question (`030D` stock restore); the scroll half is confirmed
  working on `030D` by a tester, so this is answerable.

### Discarded, and why

Dead, off-question, or we would have to overstate:

- **r/mac 1tyjwwr** (2026-06-06), a rant about paying for Magic Mouse *gestures* on Windows.
  We ship no gestures. Replying would be an advert for something we do not have.
- **r/software 17mz0gu** (2023-11-03), wants a free way to swap Fn and Ctrl on a Magic Keyboard.
  We do no key remapping. PowerToys is the right answer and it is not ours.
- **`vitoplantamura` #51** (2026-08-16), wants the Magic Mouse to present itself to Windows as a
  trackpad so scrolling feels native. We ship no such driver, and the poster opens with "open
  source alternatives are always better" — which is tempting, and still not a reason to reply.
- **`Rain9333` #13** (2026-01-11), scroll fails on an Arm-based Copilot+ PC. Magic Tray ships x64
  only. We have nothing true to say.
- **r/applehelp 1rhp50k** (2026-03-01), another developer's launch post for a free Magic Keyboard
  battery tool, 9 comments. Turning up under someone else's launch with our own is hijacking.
  If we ever want a relationship with that project, open an issue on their repo instead.
- **r/Windows10 15xt1wu** (2023-08-22), "Updated for 2023: Magic Mouse Scroll problem solved" — a
  solution write-up, not a question. Nobody there is asking anything, so a link is pure ad.
- **Super User 495124**, "Magic Mouse scrolling on Windows 8" — 78,764 views, and answered, about
  an OS we make no claims for.
- **`kanishkkmalik/MagicMouseWindows` #1** — a bug report about someone else's app, not a question
  we can answer.

### Search queries used

Reproduce the list with these. GitHub was queried through the API, Stack Exchange through
`api.stackexchange.com/2.3`, Reddit and Apple Community by loading the pages in a real browser
(both block plain HTTP clients — Reddit returns 403 to curl, Apple Community serves a "Security
Verification" interstitial, so `curl` alone will make you think live threads are dead).

```
# web search
reddit Magic Mouse not scrolling Windows 11 fix site:reddit.com
"Magic Keyboard" battery percentage Windows 11 site:reddit.com
Magic Utilities free alternative open source site:reddit.com
Magic Mouse scroll Windows site:superuser.com OR site:apple.stackexchange.com
Magic Mouse Windows 11 scrolling site:discussions.apple.com

# GitHub API
gh api -X GET search/repositories --raw-field q='magic mouse windows' -f sort=stars
gh api -X GET search/repositories --raw-field q='magic keyboard battery windows' -f sort=stars
gh api -X GET search/issues --raw-field q='"Magic Mouse" in:title scroll' -f sort=updated
gh api -X GET search/issues --raw-field q='magictray.app'       # duplicate-post check
gh api repos/<owner>/<repo>/issues -f state=all                 # per-repo enumeration

# Stack Exchange API
/2.3/search/advanced?q=magic+mouse+windows+scroll&site=apple.stackexchange&sort=activity
/2.3/search/advanced?q=magic+mouse+windows+scroll&site=superuser&sort=activity
/2.3/search/advanced?q=magic+keyboard+battery+windows&site=superuser&sort=activity
```

---

## Draft A — Reddit self-post (r/apple, Sunday only)

Post as a text post. Do not post this to r/windows, r/Windows11 or r/WindowsHelp without a
moderator's written permission first.

**Title**

```
I wrote a free tray app that shows Apple Magic Mouse, Keyboard and Trackpad battery percent on Windows 10 and 11
```

**Body**

```
Disclosure up front: I wrote this, it is free, MIT licensed, and there is nothing to buy.

I use a Magic Keyboard and two Magic mice on a Windows desktop. Windows pairs them and then tells
you nothing about the battery — the Bluetooth settings page shows the device as connected and
leaves the percentage out. The tools that fix it were all paid, so I built one.

Magic Tray is one exe. It puts an icon per device next to the clock with the battery percent, and
warns you at 10, 5 and 1 percent. It runs on 64-bit Windows 10 1809 and up, and on Windows 11.

What it does:

- Battery percent for Magic Mouse v1, Magic Mouse 2, the 2024 USB-C Magic Mouse, the Apple
  Wireless Keyboard, the Magic Keyboard family, and Magic Trackpad 1, 2 and 2024. No driver is
  swapped for any of that.
- Magic Keyboards need a one-time unlock before Windows can read the battery at all. The keyboard
  keeps the standard Windows Bluetooth driver — nothing Apple gets installed — but the keyboard
  declares its battery report as input-only, so an on-demand read returns nothing. There is a menu
  item that patches the Bluetooth service cache so the read works. It asks for admin permission,
  and re-pairing the keyboard undoes it.
- For a Magic Mouse v1, a Magic Mouse 2 or the older Apple Wireless Mouse, there is a menu item
  that opens the download page for Apple's own Windows mouse driver, which is what gives you
  one-finger scrolling. You download and run that file yourself; the app installs nothing.

What it does not do, so nobody wastes an evening:

- No gestures, no tap-to-click, no media-key remapping. If you want those, Magic Utilities is the
  paid tool that does them properly and I am not trying to replace it.
- No scrolling fix for the 2024 USB-C Magic Mouse. Apple's Windows driver package has no entry for
  that model's device code, so Windows never loads a scroll driver for it. A community driver is
  being worked on and is not published, so there is nothing to download today. Battery percent on
  that mouse does work, with no driver at all.
- It cannot turn off Windows driver-signature enforcement or Memory integrity for you, and it
  never tries.

The honest state of testing: three devices have been confirmed by a tester — the 2024 Magic Mouse,
the Magic Mouse v1, and the 2011 Apple Wireless Keyboard. Everything else is recognised by the app
and confirmed by nobody, so it should work and I cannot promise it does. Bluetooth Magic Keyboards
and Magic Trackpads are what I am shortest of. If you own one, the report form is linked from the
site and "it didn't work" is as useful as "it worked".

https://magictray.app/
```

---

## Draft B — Reply to an existing thread

One paragraph. The bracketed line is the whole point: it is the sentence that makes this a reply
rather than a broadcast, and it goes **first**. Delete any bullet that is not about their device.

```
[ONE SENTENCE ANSWERING THIS PERSON'S ACTUAL QUESTION — e.g. "Your v2 takes the same Apple
driver the v1 does, so the install is the part that failed, not the mouse" / "Windows can't read
a Magic Keyboard's battery until you patch the Bluetooth service cache, which is why the
percentage is missing rather than wrong" / "The USB-C model reports a device code Apple's Windows
package never listed, so no scroll driver ever loads — that one isn't your install."]

[If the model is unclear: Which one is it? Device Manager, open the device, Details, Hardware Ids,
and read the four hex digits after PID_. 030D and 0269 have a working answer; 0323 does not.]

The part that is fixable today: [pick one]
- 030D / 0269 / 0310: Apple wrote a Windows driver for these and it still works on 10 and 11. You
  download and run it yourself, nothing about Windows changes, and no warning appears. The eight
  steps with pictures: https://magictray.app/drivers.html#v1v2
- Magic Keyboard battery: one-time unlock, the keyboard keeps the standard Windows Bluetooth
  driver, no Apple software, no Test Mode. Re-pairing undoes it:
  https://magictray.app/keyboard.html#unlock
- 0323 (the 2024 USB-C mouse): battery percent works with no driver at all. Scrolling does not,
  because Apple's package has no entry for 0323. The community driver isn't published, so there
  is nothing to download for it yet: https://magictray.app/v3.html

Disclosure: I wrote the free app on that site, so take the recommendation with that in mind. It is
MIT licensed, there is no subscription and no trial. It shows the battery percent in the tray and
it opens that download page for you; it does not do gestures or key remapping, and if you need
those, Magic Utilities is the paid tool that does.
```

**Per-venue trims.** On Stack Exchange, the answer must work with the link deleted: write the
eight steps out. On Apple Community, drop the bullets to one and keep it under a short paragraph.
On r/WindowsHelp and r/applehelp, be ready for the link to be removed and make sure the answer
still stands.

---

## Draft C — Show HN

Post once. Then answer questions in the thread all day; that is where Show HN earns anything.

**Title**

```
Show HN: Magic Tray – Apple Magic Mouse and Keyboard battery percent on Windows
```

**URL**: `https://magictray.app/`

**First comment, posted by me immediately after submitting**

```
Author here. I use Apple input devices on a Windows desktop. Windows pairs a Magic Keyboard and
then shows no battery percentage for it, and the tools that fixed that were all subscriptions, so
I wrote one. It is a single self-contained exe, MIT, no installer, no admin needed to run it, and
it puts one tray icon per device next to the clock.

Two things in it were more interesting than I expected.

The keyboard. A Magic Keyboard's battery is unreadable on Windows, and not because of a missing
driver — it keeps the standard Windows Bluetooth driver throughout. It declares HID report 0x47 as
input-only, so an on-demand feature read returns nothing at all. I have 6,859 consecutive read
timeouts and a sweep of all 255 report ids with zero hits to prove it. The fix is to patch the
Bluetooth SDP cache so 0x47 is exposed as a Feature report, after which HidD_GetFeature returns
the percent. The app does it from a menu item, with a dialog and an admin prompt first, and
re-pairing the keyboard erases it.

The 2024 USB-C mouse. It reports PID 0323. Apple's Windows driver package lists the older Magic
Mouse PIDs and never listed this one, so Windows finds no match and never loads a scroll driver.
Battery percent works with no driver at all — it arrives in byte 2 of input report 0x90 on the
second HID collection — but one-finger scrolling needs a filter driver, and the community one is
not published yet. So: if you have the 2024 mouse, this gives you a battery percentage and no
scrolling, and I would rather say that here than have you find out after downloading.

What it deliberately does not do: gestures, tap-to-click, media-key remapping, or any trackpad
driver. Magic Utilities is paid and does those; I am not trying to clone it. For the older mice
the app does not even install the scroll driver — it opens Apple's driver download page and you
run the file, because that route needs nothing else from me.

Three devices have been confirmed by an actual tester: the 2024 Magic Mouse, the Magic Mouse v1,
and the 2011 Apple Wireless Keyboard. Nine more are recognised by the app and confirmed by nobody.
If you have a Bluetooth Magic Keyboard or any Magic Trackpad, a report either way is the most
useful thing anyone can send me.
```

---

## Draft D — GitHub issue comment

For the nine GitHub targets. Read the whole thread first; several of these have a maintainer who
has already answered part of it.

```
[ONE OR TWO SENTENCES THAT ARE ACTUALLY ABOUT THEIR ISSUE — the device code, the error, or the
thing the maintainer already said. If you have nothing technical to add, do not comment.]

Data point in case it is useful here: the 2024 USB-C Magic Mouse reports PID_0323, and Apple's
Windows mouse package has no entry for it, which is why Boot Camp drivers of any version make no
difference on that model. The mouse exposes two HID collections — movement and finger position on
COL01, battery on COL02 — and battery percent arrives in byte 2 of input report 0x90 on COL02, not
in feature report 0x47. Apple's applewirelessmouse.sys claims the LowerFilter slot for 030D and
0269; a scroll fix for 0323 has to claim the same slot.

Notes on the parts people usually conflate:

- Battery percent on 0323 needs no driver at all, on Windows 10 and 11.
- Scrolling on 0323 has no published fix. Our KMDF driver is not released — the installer is not
  on the branch — so there is nothing to download, and I would rather say that than imply
  otherwise.
- 030D, 0269 and 0310 take Apple's own signed INF, so no signature-enforcement changes are needed
  for those three. That trips people up in both directions.

Disclosure: I maintain Magic Tray, a free MIT tray app that reads the battery percent for these
devices, and the driver research above is from that work. Write-up with the register-level detail:
https://magictray.app/v3.html — happy to answer anything about the HID side here instead if that
is more useful.
```

---

## Draft E — Asking an owner for a CC0 photo of the 2024 mouse underside

Append to a reply, or post as a short comment in the v3 driver repo issues, under the test-report
thread, or under any Reddit thread where the poster clearly owns a 2024 mouse — target 10 is the
best candidate. One paragraph. It is an ask, not a pitch, so it goes after their answer and it
does not repeat the link.

```
Unrelated small favour, only if you have thirty seconds: do you have the 2024 USB-C Magic Mouse in
front of you? The identification page that tells people which Magic Mouse they own currently
illustrates that model with Apple's own product image, which is all rights reserved — so it cannot
be reused by the driver site or uploaded to Wikimedia, and every other photo on that page is
freely licensed and can be. A single overhead shot of the underside, showing the etched A3204 and
the port, released CC0 — ideally uploaded to Wikimedia Commons so anyone else can use it too —
would replace it. Phone camera is fine; it does not need to be good, it needs to be the right
mouse, because a v1 or v2 photo must never be captioned as a 2024 model on a page whose whole job
is telling those three apart.
```

---

## Claim ledger

Every factual claim in the drafts, and where it comes from. If a claim is not here, it is not in
the drafts.

| Claim | Source |
| --- | --- |
| Free, MIT, no subscription, no trial that switches scrolling off | `docs/llms.txt` "Not this"; `README.md` "Magic Tray vs Magic Utilities" |
| 64-bit Windows 10 1809 (build 17763) and up, and Windows 11 | `docs/llms.txt`; `README.md` "Install" |
| Battery percent in the tray, one icon per device, next to the clock | `docs/llms.txt` summary; `README.md` "Features" |
| Warnings at 10, 5 and 1 percent | `README.md` "Features" |
| One self-contained `MagicMouseTray.exe`, no installer, no admin to run | `README.md` "Install", "Features" |
| Battery percent works for mice, keyboards and trackpads with no driver swapped | `docs/llms.txt` "Scope", point 1 |
| Magic Keyboard keeps the standard Windows Bluetooth driver; no Apple software; no Test Mode | `docs/llms.txt` keyboard section; `README.md` "Keyboard battery" |
| The keyboard declares report `0x47` input-only, so on-demand reads return nothing | `docs/llms.txt` keyboard section |
| 6,859 consecutive read timeouts and a 255-report-id sweep with zero hits | `docs/llms.txt` keyboard section |
| The unlock patches the Bluetooth SDP cache; then `HidD_GetFeature(0x47)` returns the percent | `docs/llms.txt` keyboard section |
| Menu item is "Fix battery reads"; it finds the address itself and asks for admin | `docs/llms.txt` keyboard section; `README.md` "Keyboard battery" |
| Re-pairing the keyboard erases the unlock | `docs/llms.txt`; `README.md` |
| v1 `030D`, v2 `0269`, Apple Wireless Mouse `0310` take Apple's own signed Boot Camp INF, no Test Mode | `docs/llms.txt` "Scope", point 3 |
| For those three the tray only opens the download page and installs nothing | `docs/llms.txt` "What the app does and does not do"; `README.md` driver table |
| Eight numbered steps for that route live on `drivers.html#v1v2` | `docs/drivers.html` `HowTo`, eight `HowToStep`s |
| Nothing about Windows changes and no desktop warning appears on that route | `docs/drivers.html` body |
| 2024 USB-C mouse is PID `0323`, model A3204; Apple's package never listed `0323` | `docs/llms.txt` device names; `docs/v3.html` FAQ |
| Battery percent on `0323` works with no driver at all | `docs/v3.html` FAQ and page body |
| The community KMDF driver for `0323` is not published; nothing to download | `docs/v3.html`; `README.md` driver table; `docs/drivers.html` FAQ |
| Two HID collections, `COL01` movement/finger, `COL02` battery | `docs/v3.html` technical details |
| Battery is byte 2 of input report `0x90` on `COL02`, not feature `0x47` | `docs/v3.html`; `docs/llms.txt` |
| `applewirelessmouse.sys` uses the LowerFilter slot for `030D`/`0269`; the community driver claims it for `0323` | `docs/v3.html` technical details |
| The KMDF installer is not on the driver repo's branch, so that path errors | `README.md` "Building from source" |
| The app cannot turn off signature enforcement or Memory integrity; no `bcdedit` anywhere | `docs/llms.txt`; `README.md` |
| No gestures, no tap-to-click, no media-key remapping, no trackpad driver | `README.md` comparison table; `docs/llms.txt` "Scope", point 2 |
| Buy Magic Utilities if you need gestures or the trackpad suite | `README.md` comparison table |
| Three devices tester-confirmed: `0323`, `030D`, `0239`; everything else "should work, nobody has confirmed it yet" | `docs/llms.txt` testers section; `README.md` "Help us test" |
| Bluetooth Magic Keyboards and Magic Trackpads are the priority for testers | `docs/llms.txt`; `README.md` |
| Identify a model by the four hex digits after `PID_` in Device Manager, or the etched model number | `docs/llms.txt` "How to tell the models apart" |
| Magic Trackpad `0265` and `0324` get battery percent and no driver change | `README.md` trackpad table; `docs/llms.txt` |
| Site photographs are freely licensed (Wikimedia Commons, CC0 / CC BY / CC BY-SA) | `docs/llms.txt` photographs section; `THIRD-PARTY-NOTICES.md` |
| Version 1.1.0, released 2026-09-02 | `docs/llms.txt`; `README.md` |

### Left out on purpose

Each of these is either true-but-overstating, or unproven. None of it appears in any draft.

- **"Magic Tray installs the driver."** True for the 2024 mouse, false for v1/v2, where it only
  opens a page. `docs/llms.txt` says not to write it unqualified, so the drafts always name which
  device they mean.
- **The patched-Apple route for `0323`.** It exists, and it trades scrolling against battery, needs
  signature enforcement disabled and Memory integrity off, and nobody has confirmed it works. An
  unconfirmed route that costs you a security setting is not something to recommend to a stranger,
  so the drafts say "the community driver isn't published" and stop.
- **WHQL, Microsoft signing, Secure Boot.** Explicitly forbidden by `docs/SEARCH.md` until the
  driver is actually signed.
- **The word "supported" for any of the nine unconfirmed devices.** The drafts use "recognised by
  the app and confirmed by nobody".
- **A single number for how many people this affects.** The thread activity counts above are real;
  any aggregate would be invented.
- **Logitech rows.** Shipped but experimental and off by default. Irrelevant to every thread here.
- **The Etsy desk-tray and MagicWindow disambiguation.** Correct and load-bearing for answer
  engines, meaningless to a human in a scroll thread.
- **Any comparison claiming Magic Utilities is worse.** It is paid and it does more. The drafts say
  which one to buy and when.
- **Anything about Arm/Copilot+ PCs.** The app ships x64; see the discarded `Rain9333` #13.
