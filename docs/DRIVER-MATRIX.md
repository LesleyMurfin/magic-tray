# Driver matrix: what makes scroll work, and what makes battery work

One place for the two questions people actually ask. Scroll and battery are **separate
mechanisms** on every Apple device here, and on the Magic Mouse 2024 they are separate per
driver as well, so a single "does it work" answer would be a lie.

- Scroll, per model: [Per model](#per-model-pointer-scroll-battery-test-mode)
- Battery, per model: [Battery mechanisms](#battery-the-mechanism-differs-per-model)
- What each Magic Mouse 2024 driver does: [Per driver](#magic-mouse-2024-0323-what-each-driver-actually-does)

Every cell below is either **MEASURED** on hardware in this project, or **DOCUMENTED** from a
source that is named, or **UNMEASURED** and said so. See
[How to read the labels](#how-to-read-the-labels). Nothing here is a prediction dressed up as a
reading.

---

## Short version: which driver do I pick, and what do I get

Find your mouse by its PID (Device Manager -> the device -> Details -> Hardware Ids -> the four
hex digits after `PID_`). Then:

- **Magic Mouse 2024, USB-C, PID `0323`.** **KMDF** is the only driver that gives you scroll and
  battery percent at the same time, but there is nothing to install yet: the package has never
  been published, so the tray
  [cannot install it](../README.md#what-magic-tray-does-about-drivers) and picking the item
  installs nothing. The cost will be real when it does land: that driver is self-signed, so the
  PC has to run in Test Mode with Memory integrity off. Until then you get battery percent with
  no driver at all, and no scroll.
- **Magic Mouse v1 (AA batteries) `030D`, Magic Mouse v2 (Lightning) `0269`, Apple Wireless
  Mouse (AA) `0310`.** Install **Apple's own mouse driver** (`applewirelessmouse.sys`). Scroll
  works, battery percent works, and **no Windows security setting has to change** - Apple signed
  the file and Microsoft countersigned it. You do **not** run Apple's Boot Camp installer; see
  [the two install routes](#the-two-install-routes-for-the-apple-filter).
- **Magic Keyboard / Apple Wireless Keyboard.** There is nothing to scroll and no driver to
  install. Battery percent needs one **registry patch** you run once:
  [`scripts/kbd-patch-cachedservices.ps1`](../scripts/kbd-patch-cachedservices.ps1).
- **Magic Trackpad.** Pointer and battery work on Windows' own driver. This project ships **no**
  trackpad scroll or gesture driver, and there is no radio to pick.

If you only remember one thing: **scroll on a Magic Mouse always comes from a vendor filter
driver sitting under Windows' HID stack. Battery percent never does - it comes off the mouse
itself, through a HID report.** That is why the two can be in different states at once.

---

## Per model: pointer, scroll, battery, Test Mode

| Model (PID) | What makes the POINTER work | What makes SCROLL work | What makes BATTERY work | Test Mode needed |
|---|---|---|---|---|
| Magic Mouse 2024 / v3 (`0323`) | Windows' own Bluetooth HID stack (`HidBth` + `mouhid` on the COL01 pointer collection). No vendor driver involved. **MEASURED** | A vendor filter driver bound as `LowerFilters` on the Bluetooth instance. Two exist: the **KMDF** driver, which generates scroll from the touch surface and has a tunable speed, or **Apple's `applewirelessmouse` filter**, which gives Apple-style scroll. Stock Windows gives **no** scroll at all. **MEASURED** | Depends on the driver. KMDF: direct read of HID **Input report `0x90` on COL02**. Apple filter: the same report, but only reachable in Mode A, so it needs a temporary [Mode A/B flip](#magic-mouse-2024-on-the-apple-filter-same-report-mode-a-only). Stock Windows: the percent usually still reads. **MEASURED** | Only for the **KMDF** driver, and for the legacy byte-patched Apple binary. Apple's unmodified filter needs none. See [signing](#magic-mouse-2024-0323-what-each-driver-actually-does) |
| Magic Mouse v1, AA batteries (`030D`) | Windows' own Bluetooth HID stack, on the mouse's **single collection-less** HID node. **MEASURED** | Apple's own `applewirelessmouse.sys`, bound as a lower filter. Apple's INF lists `030D`, so the INF route binds it automatically; the manual service route reaches the same end state. **MEASURED** | HID **Feature report `0x47`** (UsagePage `0x0006`, Usage `0x0020`, logical 0..100) on that same single collection. Read by the tray off HID directly, so it does **not** depend on which scroll filter is bound. **MEASURED: 97% on live hardware** | No, on either install route |
| Magic Mouse v2, Lightning (`0269`) and Apple Wireless Mouse, AA (`0310`) | Windows' own Bluetooth HID stack. **DOCUMENTED** | Apple's own `applewirelessmouse.sys` as a lower filter. Apple's INF lists both PIDs. **DOCUMENTED** | **UNMEASURED.** No v2 hardware has been read by this project, and the v1 result may not be copied across - different mouse, different report descriptor. Driver-repo [issue #22](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix/issues/22) stays open here. Unknown means untested, never broken | No, on either install route |
| Magic Keyboard / Apple Wireless Keyboard (`0239` and the other keyboard PIDs) | **Not applicable - a keyboard has no pointer.** The keys themselves work on Windows' own Bluetooth HID keyboard driver with nothing installed | **Nothing - a keyboard has no scroll concept at all.** There is no scroll driver for a keyboard and none is needed. Not a fault, not a gap | The one-time **SDP cache patch**, not a driver: `scripts/kbd-patch-cachedservices.ps1` rewrites the `BTHPORT` pairing record so Battery Strength (`0x47`) is exposed as a **Feature** report. **MEASURED** | No. There is no kernel driver on this path |
| Magic Trackpad (`030E`, `0265`, `0324`) | Windows' own Bluetooth HID stack. **DOCUMENTED** | **Nothing applies.** No vendor scroll filter in this project covers a trackpad, and no gesture driver is shipped | Windows exposing the percent; the tray reads it like any other battery. **UNMEASURED here** - no trackpad has been hardware-tested in this project | No |

---

## Magic Mouse 2024 (`0323`): what each driver actually does

Three drivers can hold this mouse. They are not three grades of the same thing - they change
the HID stack in different ways and cost different things.

| Driver | What it does to the HID stack | What you get | What you give up | Signing and Test Mode |
|---|---|---|---|---|
| **KMDF** (`MagicMouseDriver204Scroll`, file `MagicMouseDriver-kmdf-204-scroll.sys`) | Registers a kernel service and inserts itself as a **lower filter** under Windows' HID stack on the Bluetooth instance. Measured live stack, three drivers deep: `\Driver\HidBth`, then `\Driver\MagicMouseDriver204Scroll`, then `\Driver\BthEnum` (quoted verbatim in [ENABLE-DISABLE.md](ENABLE-DISABLE.md#which-dead-wheel-fault-is-this---read-the-stack-before-you-chase-filters)). The mouse keeps its split shape - COL01 pointer, COL02 vendor - and the filter translates the touch-surface ACL traffic into wheel movement | Pointer, **scroll and battery together** - the only choice that gives both at once. Scroll speed is tunable. Battery is a direct read of Input `0x90` on COL02, continuously, with no flip and no elevation | Nothing about the mouse. What it costs is the **PC**: Test Mode on and Memory integrity off, machine-wide, with a desktop watermark. Also not installable from the tray yet | **Self-signed.** One Authenticode signature, `CN=MagicMouseFix`, self-issued. Test Mode **required** and Memory integrity **off**. Public signing is tracked in [#89](https://github.com/LesleyMurfin/magic-tray/issues/89) |
| **Apple filter** (`applewirelessmouse`, Apple's own `applewirelessmouse.sys`) | The same lower-filter position, but Apple's binary. Because Apple's shipped INF carries no `0323` entry, it is bound by service name plus a `LowerFilters` value rather than by hardware-id match. The filter exposes the mouse in one of two shapes and cannot do both: **Mode A** keeps the `col02` collection (battery readable, scroll dead), **Mode B** is the unified path with no `&col0x` (scroll works, battery unreadable) | Apple-style scroll, in Mode B - the resting state | **Battery, while scroll works.** They are mutually exclusive on this driver. A percent has to be fetched by flipping to Mode A and back, which pauses pointer and wheel for a few seconds and needs one administrator approval | **Depends on the file, not on this choice.** Apple's unmodified binary keeps its Microsoft countersignature and loads with **nothing switched off**. The legacy byte-patched, re-signed variant (`CN=MagicMouseFix`) is self-signed and does need Test Mode. Both bind under the same service name, so the tray reads the **signatures on the bound file** instead of guessing |
| **Stock Windows** (`HidBth`, no vendor filter) | No vendor filter at all. Windows' own Bluetooth HID driver owns the stack, and picking this **removes** our driver from it | Pointer, and a battery percent that usually still reads. No Test Mode, no watermark, no security change | **Scroll.** The wheel does nothing, and that is documented behaviour rather than a fault. The tray's dedicated **Battery reads** path also needs KMDF | Not applicable. `HidBth.sys` is catalog-signed with no embedded signature, which the tray reports as **unknown** and never as self-signed |

Sources for the capability cells: `MagicMouseTray/DriverAdvisor.cs` (the per-driver expectation
table and its citations), `docs/ENABLE-DISABLE.md` (the measured live stack, the Mode A/B
contract, and the signing readings), and the driver repo
[magic-mouse-v3-windows-fix](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix).

---

## Battery: the mechanism differs per model

This is the part that surprises people. There is no single "Apple battery report". Four
different mechanisms are in play, and one of them is not a driver at all.

### Magic Mouse 2024 on KMDF: direct Input `0x90` on COL02

The percent arrives as a **pushed HID Input report**, report id `0x90`, on the mouse's **COL02**
vendor collection, with the percent in byte 2. **MEASURED:** `Input 0x90` on COL02 returned 43%
and 46% live on 2026-09-01, bytes `[90 04 2E]`; a `Feature 0x90` request on the same collection
**fails** (`MagicMouseTray/MouseBatteryDevice.cs:3-8`). A healthy pushed series was logged on the
reference PC up to 2026-09-15 01:58:49: 72, 71, 61, 54, 47, 42 percent.

Two consequences worth knowing, both measured:

- Because the report is **pushed**, an idle mouse legitimately sends nothing. No reading is not a
  fault, and the tray never reports it as one.
- A usermode `GET_REPORT(Input, 0x90)` **pull** on COL02 returned `[90 00 00]` on 10 of 10 polls
  over 5 s while the wheel was dead, and 10 of 10 again with multitouch confirmed flowing. Those
  zeros therefore prove nothing at all and are never used as a signal.

Never Feature `0x47` on this model, never WMI, never the iPhone Hands-Free service.

### Magic Mouse 2024 on the Apple filter: same report, Mode A only

Same report `0x90` on the same COL02 collection - but that collection **only exists in Mode A**,
and Mode B (the resting state, where scroll works) has no `&col0x` at all. So the percent is not
readable while scroll is working. That is the entire reason the temporary **Mode A/B flip**
exists: one elevated cycle flips to Mode A, the tray reads the percent unelevated, and the
restore to Mode B runs from a `finally` and is **verified, not assumed**. Full contract and every
failure mode: [ENABLE-DISABLE.md, Mode A/B battery flip](ENABLE-DISABLE.md#mode-ab-battery-flip-patched-apple-driver-only).

### Magic Mouse v1 (`030D`): Feature `0x47` on its one collection - MEASURED 97%

A completely different channel from the v3, on completely different hardware topology. The v1 has
**no COL02 and no Input `0x90`**. Its descriptor declares the battery on its single unified mouse
collection: one feature value cap, **report id `0x47`, UsagePage `0x0006`, Usage `0x0020`, 8 bits,
logical 0..100, `FeatureReportByteLength` 2**, and no `0xFF00` / `0x0014` top level anywhere.

**MEASURED** on the reference PC's paired `030D`, tray running with `enabled_030d=true`,
2026-09-15 20:39:26:

```
MOUSE_BATTERY_OK device=Magic Mouse v1 pct=97% (unified Feature 0x47)
REPAIR_SNAPSHOT pid=030d bt=2 usb=0 bound=applewirelessmouse svc=running ... batt=97
```

Two things follow. First, **no mode flip occurs or is needed** on a v1: a Feature report is
something the device answers on request, not a collection that appears and disappears with a
filter. Second, the read does **not depend on which scroll filter is bound**, so the percent works
on Apple's driver and on stock Windows alike.

This answers driver-repo
[issue #22](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix/issues/22) for the v1 - but
not the way the issue was framed. The question was whether the pre-`0323` mice can be read at all
given they pre-date the COL02 vendor collection. They can; through a different report entirely.

### Magic Mouse v2 (`0269`) and Apple Wireless Mouse (`0310`): UNMEASURED

**Not measured. No v2 hardware has been read by this project.** Driver-repo
[issue #22](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix/issues/22) remains open for
this model.

The v1's `0x47` result is deliberately **not** copied across: it is a different report descriptor
on a different mouse. The v2 may well read exactly like the v1 - but "may well" is not a reading,
so both v2 battery cells stay Unknown in the tray and in this document. **Unknown here means
untested hardware, never a missing or broken capability.** Nothing in this project claims a v2
battery works, and nothing claims it fails.

### Magic Keyboard (`0239` and the other keyboard PIDs): the SDP cache patch, not a driver

No kernel driver is involved anywhere on this path. Apple's wireless keyboards declare Battery
Strength as **Input-only**, so Windows cannot poll it: natively `FeatureReportByteLength` is 0,
`HidD_GetFeature` / `GetInputReport` return nothing, and `ReadFile` never fires because the device
only pushes on Bluetooth connect. **MEASURED exhaustively:** 6,859 consecutive read timeouts and a
255-report-id sweep with zero hits (`MagicMouseTray/KeyboardBatteryDevice.cs:6-16`).

[`scripts/kbd-patch-cachedservices.ps1`](../scripts/kbd-patch-cachedservices.ps1) fixes that by
rewriting the pairing record's Bluetooth SDP cache under
`HKLM\SYSTEM\...\BTHPORT\Parameters\Devices\<MAC>`: it inserts `09 20 B1 02` at the COL02 close,
which declares `0x47` as a **Feature** report. Afterwards `FeatureReportByteLength` reads 2 and
`HidD_GetFeature(0x47)` returns `[0x47, pct]` - **MEASURED live: `[47 0E]`**.

Two operational facts: the script **requires `-Mac`** (there is no default), and **re-pairing the
keyboard erases the patch**. Details: [README, Keyboard battery](../README.md#keyboard-battery).

### Magic Trackpad

Pointer and battery, on Windows' own driver. No vendor scroll filter in this project applies to a
trackpad, and no gesture driver is shipped. **The trackpad battery path has not been
hardware-tested in this project** - the tray reads it like any other Apple battery, but no
measurement is on record here.

---

## Why v1/v2 and v3 differ: one HID node against three

The v1 and the v3 are not the same device with a different PID. They present **different HID
topologies to Windows**, which is why their battery channels differ and why the scroll story
differs too.

**Magic Mouse 2024 (`0323`) splits its collections and also keeps a collection-less parent.** All
three keys were enumerated live on 2026-09-15 while the mouse was in use, verbatim
(`MagicMouseTray/DeviceDiagReader.PointerKeyKind`):

```
HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&0323&COL01\A&31E5D054&2A&0000
    Status OK,      class Mouse    - the POINTER collection
HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&0323&COL02\A&31E5D054&2A&0001
    Status OK,      class HIDClass - vendor / battery, NOT the pointer
HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&0323\A&31E5D054&2A&0000
    Status Unknown, class Mouse    - the collection-less parent, an aggregate
```

**Magic Mouse v1 (`030D`) exposes exactly ONE collection-less node**, with no `&Col01` / `&Col02`
sibling under `Enum\HID` at all - Windows writes no collection suffix when a device does not
split. Measured on the reference PC, 2026-09-15, pointer and scroll both working
(`MagicMouseTray/DriverAdvisor.cs:231-236`):

```
HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&000205AC_PID&030D\A&137E1BF2&9&0000
    device key "{00001124-...}_VID&000205AC_PID&030D", instance "A&137E1BF2&9&0000"
```

That single difference explains the rest of this document:

- The v3's battery lives on **COL02**, a vendor collection that exists only in some driver modes.
  So on the v3 the battery reading can be taken away by a driver choice.
- The v1 has nowhere to put a vendor collection, so its battery is declared on the **one unified
  mouse collection** as a Feature report the device answers on request. Nothing a filter does can
  make it appear or disappear.
- Pointer diagnosis has to handle both shapes. On a splitting device COL01 is the pointer
  collection by construction and the collection-less parent is ignored (its presence reads
  Unknown, and it is the wrong devnode to restart); on a single-collection device the sole node
  **is** the pointer child. Measured log lines: `shape=col01 col01=1 nocol=1` for the v3, and
  `DEVICE_DIAG_POINTER pid=030d live=true keys=1 present=1 shape=sole col01=0 nocol=1` for the v1.

---

## The two install routes for the Apple filter

Two routes put Apple's `applewirelessmouse.sys` on a mouse. **They converge on the same binary in
the same lower-filter position**, so neither is second-class and the capabilities are identical.
The driver file is byte-identical across both: SHA256 `08F33D7E3ECE2C73...`, 78,424 bytes - the
same hash and byte count as the file installed on the reference PC.

**Route 1 - the INF package.** Declarative. The reference PC's `C:\Windows\INF\oem8.inf` does it:

```
[Apple.NTamd64]
...=AppleWirelessMouse, BTHENUM\{00001124-...}_VID&000205ac_PID&030d
...=AppleWirelessMouse, BTHENUM\{00001124-...}_VID&0001004c_PID&0323
[AppleWirelessMouse.NT.HW.AddReg]
HKR,,"LowerFilters",0x00010000,"applewirelessmouse"
```

The `0323` line above is a **local addition** on that PC. Apple's shipped INF lists only `030D`,
`0310` and `0269`. Packaging used for this route:
[tealtadpole/MagicMouse2DriversWin11x64](https://github.com/tealtadpole/MagicMouse2DriversWin11x64).

**Route 2 - manual service registration.** No INF at all. It matches the device by
`BTHENUM\{00001124-...}` plus VID `004C|05AC`, runs `sc.exe create applewirelessmouse`, writes
`LowerFilters` as a MultiString on `HKLM\SYSTEM\CurrentControlSet\Enum\<instanceId>`, then
`Disable-PnpDevice` / `Enable-PnpDevice`. Source:
[sbagirici/apple-magic-mouse-scroll-fix-windows](https://github.com/sbagirici/apple-magic-mouse-scroll-fix-windows).
This is the route that reaches a `0323`, because it never needs the mouse to match a hardware id
in Apple's INF.

**Why they are the same end state.** `HKR` inside a `.NT.HW` section writes the device's
**hardware key** - the instance key - which is the exact location route 2 sets by hand. Both
routes therefore write the same registry value, naming the same Apple binary as a lower filter on
the same device.

**Neither route uses Apple's Boot Camp installer, and that is deliberate.** "Boot Camp" names two
different things:

- the Boot Camp **driver** - `applewirelessmouse.sys`, inside Apple's Windows support package.
  This is what makes the wheel scroll, and it works on ordinary PC hardware.
- Apple's Boot Camp **installer**, which **refuses to run when the machine is not a Mac.**

Nobody needs the installer for scroll. The driver repo states the trap as its third problem:
"Boot Camp installer checks for Mac hardware" -> "Skip the installer entirely - install only the
driver". Same repo on signing and system state: "It is digitally signed by Apple and countersigned
by Microsoft (WHQL)", "Secure Boot | Compatible (driver is Microsoft-signed)", "Does not modify
system boot configuration". That is the basis for the no-Test-Mode answer on both routes, and the
reference PC confirms it: that `.sys` carries a self-issued `WDKTestCert` primary signature **plus
two valid `CN=Microsoft Windows Hardware Compatibility Publisher` signatures**, and Windows loads
a file when **any** of its signatures is trusted.

**What Magic Tray itself does here is less than either route.** For v1/v2 the tray only opens the
documented download page. It runs no installer, no script, no `pnputil`, and binds nothing.

---

## Rival driver packages: several can claim one PID

Windows keeps every driver package that was ever installed, and **more than one of them can
declare the same hardware id**. This is the mechanism behind "my scroll was fine, then Windows put
me back on the wrong driver": nothing broke, PnP simply re-ranked a device that several packages
match.

**MEASURED on the reference PC, read-only and non-elevated, 2026-09-15/16.** Four installed
packages declare the v3 hardware id
`BTHENUM\{00001124-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&0323`, verbatim:

```
INF        Package                                   DriverVer   SignerName     SignerScore
oem50.inf  MagicMouseDriver-kmdf-204-scroll.inf 2.0.4.3  09/15/2026  MagicMouseFix  0x0F000000  <- BOUND NOW
oem26.inf  MagicMouseDriver.inf 23.14.8.22          08/30/2026  MagicMouseFix  0x0F000000
oem16.inf  MagicMouseDriver.inf 4.47.14.717         04/27/2026  (empty)        0x80000000 unsigned
oem8.inf   Apple AppleWirelessMouse 6.2.0.0         04/21/2026  MagicMouseFix  0x0F000000
```

**How Windows picks one, measured rather than assumed.** Both live BTHENUM parents read
`DEVPKEY_Device_DriverRank` = 16711680 = `0x00FF0000`, the best exact-hardware-id rank. All four
claimants match by **exact** hardware id at list index 0, so they all **tie** on rank - rank cannot
pick a winner. The tie is broken by **`DriverVer` date first, then version**. `oem50.inf` wins
today only because it is the newest-dated: it is *not* the higher version number (`oem26.inf` is
version 23.14.8.22 against `oem50.inf`'s 2.0.4.3), and it is not better signed (three of the four
share the same local publisher, `MagicMouseFix`, so signer trust gives no differential at all).
Only `oem8.inf` claims `030D`, so **the v1 has no rival today.**

**The tray reports this**, as an advisory and never as a fault: a rival claimant is not a problem
in itself - millions of PCs carry several packages for one device and never rebind - so it can
never reach Blocking, and an unreadable reading is treated as no evidence. It also never guesses:
the bound package must be identifiable **and** dated before anything can be said to outrank it,
because "we could not read a date" must not render as "something newer is sitting there"
(`MagicMouseTray/DriverClaimReader.cs`, `MagicMouseTray/SystemConfigChecker.cs`).

One related consequence, stated as a possibility rather than a certainty: installing Apple's INF
route on a PC whose `0323` is on KMDF **can** displace KMDF, because a WHQL-signed package outranks
a test-signed one. If it happens you lose what KMDF gives - the tunable scroll speed and the direct
battery read - and there is no way back through the tray: it cannot install KMDF (see
[the short version](#short-version-which-driver-do-i-pick-and-what-do-i-get)), and the KMDF package
has never been published, so KMDF can only be put back from wherever that PC got it in the first
place, by hand.

---

## How to read the labels

| Label | Means |
|---|---|
| **MEASURED** | Read off hardware in this project, with the reading quoted or cited above. Dates and log lines are the evidence |
| **DOCUMENTED** | Stated by a named source - Apple's INF contents, the driver repo, or this repo's own code comments - but not read off hardware here |
| **UNMEASURED** | Nobody has measured it in this project. It is never a claim that the thing is broken, and it is never upgraded by inference from a different model |

Two honesty rules this document follows, the same ones the tray follows: **unknown renders as
unknown and is never a fault**, and **a reading is never claimed that was not taken**. That is why
the v2 battery cells say Unknown instead of copying the v1's 97%, and why a battery row on a mouse
that is switched off in the tray says it is switched off rather than "no reading".

---

## See also

- [README, Scrolling](../README.md#scrolling) - install routes in plain language, and
  [Mice it knows](../README.md#mice-it-knows) for the catalog with tested-hardware status.
- [ENABLE-DISABLE.md](ENABLE-DISABLE.md) - what is wrong right now and how it is repaired,
  including the full Mode A/B flip contract and the dead-wheel triage.
- [TESTED.md](TESTED.md) - the hardware reports behind the MEASURED labels.
- [magic-mouse-v3-windows-fix](https://github.com/LesleyMurfin/magic-mouse-v3-windows-fix) - the
  KMDF driver and the Apple-filter path for `0323`.
