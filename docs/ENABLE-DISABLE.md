# Enable, disable, pair, and recover

Three different tools. Using the wrong one is why a mouse "will not come back".

This document is about what is wrong **right now** and how it gets fixed. For what each driver is supposed to give you in the first place - what makes scroll work and what makes battery work, per model and per driver, with the measured evidence - see [DRIVER-MATRIX.md](DRIVER-MATRIX.md).

| Job | Tool | What it actually does |
|---|---|---|
| Something is wrong and you do not know what | Tray -> problem row at the top of the menu, or **Diagnostics -> Check for problems now** | Reads the live BT HID stack, names the cause in plain language, and fixes it if the fix is safe. Start here. |
| Stop this mouse on **this PC** so a Mac can use it | Tray -> device row -> **Enabled on this PC** | Elevated `pnputil /disable-device` on **Bluetooth / HID** nodes for that PID. Stays paired. |
| Start it again on this PC | Same checkbox, on | `pnputil /enable-device` on those same nodes. **Only works if Windows still has the device.** |
| Forget / re-pair | Windows **Bluetooth & devices** | Unpair / Add device. Tray Enable cannot recreate a removed pairing. |
| Pointer works, wheel dead after USB-C charge | Tray problem row (auto-fix), or `diagnose-and-recover.ps1 -Repair` as the CLI fallback | `pnputil /restart-device` on the live **BTHENUM** parent, which makes PnP rebuild the stack and load the bound lower filter. Does not unpair. |
| Wheel dead **and** battery percent shows unavailable, two scroll drivers registered | Tray problem row (auto-fix) | Removes only the leftover vendor filter name from the `LowerFilters` value that carries it, keeps the working filter, then `pnputil /restart-device` on the live **BTHENUM** instance. Does not unpair. |
| Wheel dead after a Windows restart while the tray shows the correct driver | Tray problem row (auto-fix) | Reads `DEVPKEY_Device_Stack`, and when the bound filter is registered and RUNNING but missing from the live stack, runs `pnputil /restart-device` on the live **BTHENUM** instances. Does not unpair. |
| Get a battery percent while the mouse is on the **patched Apple** driver (v3 `0323`) | Tray -> device row -> the battery read offered on that driver | One elevated cycle: flips the mouse into its battery mode, reads the percent unelevated, then restores the scroll mode and verifies it. One UAC prompt for the whole cycle. Pointer and wheel pause for a few seconds. Never changes which driver is bound. See "Mode A/B battery flip" below. |
| The tray says an earlier battery flip was never confirmed finished, or scroll is dead right after one | Tray -> the **restore scroll mode** offer (shown at startup while that is outstanding) | One elevated restore of the `LowerFilters` value the tray recorded before it flipped, then a re-read to confirm the scroll mode is back. Does not unpair. |
| Wheel dead while `DEVPKEY_Device_Stack` **already names** the vendor filter | Tray -> device row -> **Scroll is not working** (guidance only), after reading "Which dead-wheel fault is this" below | Not a tray fault and not a registration fault. The mouse never received the Apple multi-touch enable report `{0xF1, 0x02, 0x01}`, which the **driver package's** F1 watcher sends. No tray read can prove that state, so the tray never raises it on its own: you assert the symptom, it names the cause and the two steps. Restarting, reinstalling and re-pairing all do nothing here. |
| Capture everything after an incident | Tray -> Diagnostics -> **Run diagnose-and-recover.ps1** | Capture + diagnose only. No `-Repair`. |

## In-app repair (default path)

No PowerShell. The app does the diagnosis, explains the cause, and only then acts.

1. Open the tray menu. When something is wrong, the **top row is the problem row** (for example "Scroll wheel is dead - the scroll driver stopped"). When nothing is wrong the row reads **No problems found**.
2. Nothing at the top and you still suspect trouble? **Diagnostics -> Check for problems now** re-reads the live stack on demand.
3. Click the problem row. A **guided dialog** states, before anything runs: which device (PID), what the app found, what caused it, and exactly what the fix will do.
4. If the problem is auto-fixable, the dialog's action button runs the fix (one UAC prompt, because restarting a device node is an elevated operation). Otherwise the dialog lists the manual steps and opens the right Windows page for you.

A toast fires once per problem per session, so a known problem does not nag you.

### What the app fixes for you

| Problem | Plain-language cause | Automatic fix |
|---|---|---|
| Scroll filter stopped but still bound (v3 `0323` after a USB-C charge) | The filter service named in the device's `LowerFilters` - whatever it is called on this PC, for example `MagicMouseDriver204Scroll` - is not running. Pointer and battery do not need the filter, so only the wheel dies. | Yes. `pnputil /restart-device` on the **live BTHENUM** instances only, which is what makes PnP load the bound lower filter again. The app does not `sc start` the filter. |
| Device is off in the app but present in Windows (`enabled_<pid>=false` while nodes are live) | The tray row was left unchecked. Windows still has the device. | Yes. Re-enables the device for this PC and turns the row back on. |
| Two scroll drivers registered on one mouse (v3 `0323`: wheel dead, battery percent unavailable) | An older install left a second vendor filter name in the **device-key** `LowerFilters` while the current filter sits on the **instance key**. Windows applies both keys to the same stack, so PnP is told to load a stopped leftover filter next to the running one. | Yes. Removes only the leftover name from the `LowerFilters` value that carries it, keeps the running filter, then `pnputil /restart-device` on the live **BTHENUM** instance so PnP rebuilds the stack with exactly one vendor filter. |
| Bound filter registered and RUNNING but not in the device stack (v3 `0323` after a Windows restart: wheel dead, driver reads correct) | Windows rebuilt the BTHENUM stack from a cached HID layout without the lower filter. `LowerFilters` still names the filter and `sc query` still says RUNNING, so only `DEVPKEY_Device_Stack` shows it is not attached. | Yes. `pnputil /restart-device` on the live **BTHENUM** instances, then the tray re-reads the stack: attached is success, still absent is reported as a failure with the re-pair steps. |
| Bluetooth pointer child registered but not present (`PointerChildMissing`) | The PID's Bluetooth-transport COL01 HID child is still registered under `Enum\HID`, but PnP will not resolve the devnode, so Windows has no pointer to feed. USB charge-cable `HID\VID_05AC...&COL01` phantoms are excluded from that read, so a charging leftover can neither mask the fault nor trigger it. | Yes. The same single elevated `pnputil /restart-device` on the live **BTHENUM** instances, so PnP rebuilds the HID children. No key at all, or an unreadable one, is no evidence and is never reported. |
| Keyboard battery blocked (`BatteryReadBlocked`: a connected Magic Keyboard reading `-2`) | The pairing record's SDP cache does not expose Battery Strength (RID `0x47`) as a **Feature** report, so the percent cannot be read at all (`MagicMouseTray/KeyboardBatteryDevice.cs:6-22`). Not a driver fault and not a device fault. | Yes, through the offer the tray already has: **Fix battery reads**, one user-confirmed elevated run of `scripts/kbd-patch-cachedservices.ps1 -Mac <keyboard MAC>`. A **mouse** reading `-2`, `-3` or `-1` is deliberately never reported - see "What the tray reports per capability". |

### Two scroll drivers registered on one mouse

**Cause.** Installing a newer build of the vendor scroll filter without cleaning up the older one leaves two rival filter names registered against the same Bluetooth HID stack. Windows reads `LowerFilters` from the **device key** and from the **instance key** under `HKLM\SYSTEM\CurrentControlSet\Enum\BTHENUM` and applies both to one stack, so PnP is told to load a filter that no longer has a running service alongside the filter that does.

**Symptoms.** The pointer and the clicks are fine, the wheel is dead, and the battery percent reads unavailable because the vendor COL02 report comes back zeroed on every poll. The HID nodes themselves still report Status=OK / `CM_PROB_NONE`, which is why a Device Manager check finds nothing.

**Worked example, reference PC (Magic Mouse 2024, PID `0323`).** Measured under `HKLM\SYSTEM\CurrentControlSet\Enum\BTHENUM`:

```
{00001124-...}_VID&0001004c_PID&0323            <- device key
    LowerFilters = [MagicMouseDriver]            <- leftover, service Stopped
    Service      = (empty)
  9&73b8b28&0&D0C050CC8C4D_C00000000            <- instance key
    LowerFilters = [MagicMouseDriver204Scroll]   <- current filter, service Running
    Service      = HidBth
```

Service states there: `MagicMouseDriver204Scroll` Running, `MagicMouseDriver` Stopped, `applewirelessmouse` Stopped. Both `.sys` files exist and their signatures are Valid, test signing is on and HVCI is off, so signing is not the fault.

**Fix.** Remove only `MagicMouseDriver` from the device-key `LowerFilters` value that carries it, leave `MagicMouseDriver204Scroll` on the instance key, then `pnputil /restart-device` the live BTHENUM instance so PnP rebuilds the stack with exactly one vendor filter.

**A plain restart-device cannot fix this.** Restarting the device makes PnP rebuild the stack from what is registered, and the stale name is still registered, so the rebuilt stack has the same two rival filters. The registry value has to lose the leftover name first. That is also why the repair needs one elevated step.

**Safety rules the repair obeys.** A name is removed only when all of these hold: it is a KMDF-family or Apple-family filter name, its service is **not** running, a **different** family filter on the same stack **is** running, and removal leaves at least one family filter name across the stack. The repair never unpairs, never writes `FLIP:NoFilter`, never toggles the Bluetooth radio, never touches a `USB\` or `HID\VID_` phantom node, never deletes a service key or a `.sys` file, never removes a non-family filter such as `mouhid` or `HidBth`, and never empties the last remaining family filter. The previous `LowerFilters` value is logged verbatim before the write, so the change can be undone by hand.

### Wheel dead after a restart while the driver reads correct

**Symptoms.** After a Windows restart the tray shows the correct driver for the mouse, the pointer moves, clicking works, the battery percent updates, and the scroll wheel is dead. Device Manager reports nothing wrong. Before this release the repair row read **No problems found**, so the app named no cause and offered no fix.

**Why the app used to miss it.** `FilterServiceRunning` comes from `sc.exe query <service>` and is true when that output contains `RUNNING` (`MagicMouseTray/DriverHealthChecker.cs:324-345`). That proves the driver **image** is loaded on this PC. It does not prove the lower filter is **attached** to this mouse's device stack. In this fault the device's `LowerFilters` still names the filter, the filter package is still installed, and the service still reports RUNNING, so every check the app had read healthy - byte-identical to a working mouse.

**The discriminator.** `DEVPKEY_Device_Stack` on the live BTHENUM instance: the ordered list of drivers Windows actually put in the stack. The repo already measured attachment this way in `scripts/capture-state.ps1:152-157`, which joins that property's string list and matches the filter name against it. The tray now reads the same property from C# (`MagicMouseTray/DeviceStackReader.cs`) and carries the answer on the snapshot as `DeviceSnapshot.FilterInStack`:

| `FilterInStack` | Meaning |
|---|---|
| `true` | A live BTHENUM instance's stack names the bound filter - or another build of the same filter family, which is still an attached scroll driver. Attached. |
| `false` | At least one live instance was readable and none of the readable stacks names any family filter. Registered, but not attached. |
| `null` | No evidence: no instance id resolved, or every property read failed. |

**Detection rule, as implemented.** The finding is raised only when all five of these hold at once: there are live BTHENUM instances, the device has a bound filter name, the filter package is present, the filter service reports RUNNING, and `FilterInStack` is `false`. `null` never raises a problem, so a PC where the property cannot be read is never nagged - unknown is treated as no evidence, not as a fault. The shipped title is **Scroll wheel is dead - the driver is not attached to this mouse**.

**Fix.** The same single elevated stack action as the other restart repair: `pnputil /restart-device` on the live **BTHENUM** instances, so PnP tears the cached stack down and rebuilds it with the registered lower filter in place. It needs one administrator approval. It never unpairs, never installs or removes a driver package, never writes a `LowerFilters` value, never toggles the Bluetooth radio, and never touches a `USB\` or `HID\VID_` phantom node.

**Verification is attachment, not the service state.** For this fault `sc query` already said RUNNING **before** the repair, so polling the service afterwards proves nothing. When the restart returns success the tray instead re-reads `DEVPKEY_Device_Stack`. Filter now in the stack: the repair worked and the tray reports success. Filter still absent: the tray does **not** claim success - it reports that Windows kept the cached layout for this mouse and gives the re-pair steps (Bluetooth & devices -> Remove device, power the mouse off and on, Add device), which is the action measured to rebuild the layout (`docs/v3.html:150-156`).

### Which dead-wheel fault is this - read the stack before you chase filters

There are **two** unrelated faults that both look like "the wheel is dead while the driver reads correct". Everything above this point is fault A. Do this one measurement before you restart, reinstall or re-pair anything.

**The one-question triage.** Read `DEVPKEY_Device_Stack` on the live BTHENUM instance (`Get-PnpDeviceProperty -InstanceId <bthenum instance> -KeyName DEVPKEY_Device_Stack`). If the vendor filter name is **not** there, it is fault A and the tray's problem row fixes it. If it **is** there and the wheel is still dead, **stop** - it is not a registration fault, and no amount of restarting, reinstalling or re-pairing is the fix.

| `DEVPKEY_Device_Stack` names the vendor filter | Fault | Who fixes it |
|---|---|---|
| No | **A** - registered and RUNNING but not attached to the live stack | Magic Tray. The problem row **Scroll wheel is dead - the driver is not attached to this mouse** (rule 2b), one elevated `pnputil /restart-device`, verified by re-reading the stack. |
| Yes, and the wheel is still dead | **B** - the mouse was never switched into multitouch mode | The driver package, not this repo. See below. |

**Fault B, named honestly.** The Magic Mouse only emits its multitouch report stream after it receives the Apple multi-touch enable **feature** report `{0xF1, 0x02, 0x01}` on COL01 (`docs/DESIGN-trackpad-tap.md:54`). Until that report lands the mouse speaks plain boot-mouse protocol: the pointer moves, the clicks work, the **wheel does nothing**, and the vendor battery report stays silent. Nothing about the driver, its registration, its signature or the device stack is wrong in that state - the mouse simply was never asked to talk.

**Who sends F1, and why a reboot lost it.** F1 is sent by the **driver package's** watcher, `v2-kmdf-driver/scripts/mm-auto-f1-watcher.ps1` in `LesleyMurfin/magic-mouse-v3-windows-fix`. That watcher subscribed only to WMI device-**arrival** events, so a mouse that was already enumerated by the time the subscription registered - which is what happens at boot - never got F1, and multitouch was silently lost on **every** reboot. The fix is a startup reconciliation inside that watcher (register the subscription, then fire F1 once if the PID_0323 COL01 HID interface already exists; the watcher's existing debounce makes a double fire harmless) and it **belongs to the driver repo**. The driver binary is unchanged at 2.0.4.1. *[Inference: the watcher's arrival-only subscription and the shape of its fix are as reported by the session that owns the driver repo; this repo has not read that script.]*

**Magic Tray does not send F1, and must not start.** Sending the enable feature report is the driver package's job. A second implementation in the tray is an explicit **non-goal**: two independent senders on the same mouse would race, and the tray would be claiming ownership of a device-initialisation step it does not own. The tray's job here is to stay quiet, which is exactly what it does.

**Measured evidence, reference PC `Lesleys-PC` (Magic Mouse 2024, PID `0323`), 2026-09-15.** The live stack, verbatim:

```
DEVPKEY_Device_Stack = \Driver\HidBth | \Driver\MagicMouseDriver204Scroll | \Driver\BthEnum
```

Every read below was taken while the wheel was **dead**, and again **after** scroll was restored by sending F1 by hand. The two sets are byte-identical:

| Read | Measured value (dead and restored - identical) |
|---|---|
| `DEVPKEY_Device_Stack` | The three-driver list above. The vendor filter **is** attached. |
| `DEVPKEY_Device_LowerFilters` | `MagicMouseDriver204Scroll`, a single name. The BTHENUM **device key** carries no values at all; the **instance key** holds that one name - so there is no leftover-filter conflict either. |
| `DEVPKEY_Device_ProblemCode` | `0`. |
| Filter service and image | `MagicMouseDriver204Scroll` **Running**. `.sys` = `C:\Windows\System32\drivers\MagicMouseDriver-kmdf-204-scroll.sys`, 32496 bytes, 2026-09-01 16:13, Authenticode **Valid** `CN=MagicMouseFix`, driver version **2.0.4.1**, `oem50.inf`. Test signing on, HVCI off. |
| HID children | Both **OK**: `...&Col01` (Class=Mouse, `mouhid`) and `...&Col02` (vendor-defined). |
| COL01 report descriptor | Input `RID=0x12` exposes `0x01:0x0038 WHEEL` **and** `0x0C:0x0238 AC_PAN`, InputReportByteLength 8. The wheel usages are advertised; the mouse just never sends them. |
| COL01 feature report | `RID=0xF1`, vendor usage `0xFF00:0x0001`, FeatureReportByteLength **3** - exactly the three bytes `{0xF1, 0x02, 0x01}` the enable report needs. |
| `DEVPKEY_Device_LastArrivalDate` | 2026-09-15 14:29:58, **14 s after** `LastBootUpTime` 14:29:44. The stack was never rebuilt after boot, so no restart-device, no re-pair and no driver reinstall happened before the fix. |

**Why rule 2b stays silent on fault B, and why that is the intended behaviour.** Every input the fault-A rule reads - live BTHENUM instances, a bound filter name, the package present, the service RUNNING, and `FilterInStack` - is identical on this PC before and after scroll came back, and `FilterInStack` is `true` in both. A rule that fired here would have to fire on a mouse whose wheel is working, because the reads cannot tell the two apart. The silence is the rule being correct, not the tray missing a fault. Anything that changes that has to measure something the tray does not currently read - whether the multitouch stream is actually flowing - not the stack.

**Do not use a zeroed `0x90` pull as a signal.** A usermode `GET_REPORT(Input, 0x90)` pull on COL02 returned `[90 00 00]` on **10/10 polls over 5 s while the wheel was dead**, and **10/10 again after** scroll was restored and multitouch was confirmed flowing. A zeroed `0x90` pull therefore proves nothing and MUST NOT be treated as a fault signal. The battery percent does not arrive from that pull: it arrives as a **pushed** input report, and `debug.log` shows a healthy series - 72, 71, 61, 54, 47, 42 percent - up to 2026-09-15 01:58:49. The tray's existing `MOUSE_BATTERY_ZERO` wording ("no battery report sent yet - idle mouse ...") is already the correct reading of those zeros and needs no change.

### What the tray reports per capability - pointer, scroll, battery

Everything above this line answers one question: is the scroll filter registered and attached. Nobody experiences "the filter is attached". They experience three separate things working or not - the **pointer**, the **scroll wheel** and the **battery percent** - and those three fail for unrelated reasons, only some of which are the tray's to fix. The tray now reports all three per device, one signal each, every signal tri-state: `true` proven working, `false` proven faulted, `null` **no evidence**. `null` never raises a problem row, exactly as `FilterInStack = null` never does. A PC the tray cannot measure must not be nagged, and that is the constraint this whole section is shaped around.

| Capability | Signal it reads | What it can see | What it cannot see | Tray response |
|---|---|---|---|---|
| Pointer | The Bluetooth-transport pointer HID child devnode for the PID - `&COL01` on a mouse that splits its collections, the sole collection-less node on one that does not - resolved through `CM_Locate_DevNodeW` (`DeviceDiagReader.PointerChildLive`) | A pointer child still registered under `Enum\HID` that PnP no longer resolves as present | Whether the cursor is actually moving. A mouse that is powered off, idle or out of range is not a fault | `PointerChildMissing` -> the existing single elevated `pnputil /restart-device` on the live BTHENUM instances. Auto-fix. |
| Scroll | Registration and attachment as before (rules 1, 2, 2b), plus **counter movement** in the filter's own `Diag` key: `AclTranslateCount`, or `Rid12Count` when that value is absent (`DeviceDiagReader.MultitouchAdvancing`) | That multitouch **is** flowing right now, whenever a counter advanced between two samples | Whether a still counter means a broken mouse or a mouse nobody is touching. Those two are identical reads | Registration faults keep their automatic repairs. A dead wheel with everything attached is **not** auto-detected: the device row carries a user-initiated **Scroll is not working** item that opens the guidance dialog (`RecommendMultitouchWatcher`). |
| Battery | The last polled percent for the PID on `DeviceSnapshot.LastBatteryPct`: `-1` no reading, `-2` present but blocked, `-3` three consecutive no-readings | A connected Magic Keyboard whose pairing record is missing the SDP Feature `0x47` capability, which is a real and fixable block | Whether a silent mouse is faulted. An idle mouse legitimately sends nothing at all | Keyboard `-2` -> `BatteryReadBlocked`, routed to the tray's existing **Fix battery reads** SDP patch offer. Mouse `-1`, `-2` and `-3` raise **nothing**, deliberately. |

**Pointer: is the Bluetooth pointer child still there.** `DeviceDiagReader.PointerChildLive(pid)` first decides which key under `HKLM\SYSTEM\CurrentControlSet\Enum\HID` even is that PID's pointer child, because two key layouts are real and both were measured on the reference PC (`MagicMouseTray/DeviceDiagReader.cs:463-499`). A v3 splits its collections **and** keeps a collection-less parent, so `&COL01` wins unconditionally and the parent - an aggregate whose presence reads Unknown, and the wrong devnode to restart - is ignored (`shape=col01`). A v1/v2 exposes exactly one collection, so Windows writes no `&COL0x` suffix at all and that single node is the pointer child (`shape=sole`). No COL01 and two or more distinct collection-less candidates has no principled winner, so nothing is selected (`shape=ambiguous`). Whatever was selected is then resolved with `CM_Locate_DevNodeW(CM_LOCATE_DEVNODE_NORMAL)`: resolved is `true`, a key that exists while no instance of it resolves is `false`, and no candidate at all, an ambiguous field, or any failure is `null`. Only the Bluetooth-transport form of the key counts, `{00001124-...}_VID&...`; `HID\VID_05AC...` and `&MI_` keys are excluded outright. The `DEVICE_DIAG_POINTER` line in `debug.log` carries both the verdict and the shape it came from - on the reference PC, 2026-09-15: `pid=030d live=true keys=1 present=1 shape=sole col01=0 nocol=1`.

**Why the USB charge-cable COL01 keys are excluded.** The existing `DeviceSnapshotReader.ReadHidLayer` sets `Col01Present` / `Col02Present` by substring-matching `COL01` / `COL02` across **every** `Enum\HID` subkey for the PID, charge-cable phantoms included. The reference PC carries a phantom `HID\VID_05AC&PID_0323&MI_01&COL01` left over from charging, so that flag reads `true` whether or not the Bluetooth pointer child is still there: it cannot tell the mouse from the cable, which is exactly why the pointer probe could not be built on it. (Neither flag ever had a consumer, so nothing depended on the old reading.) Excluding the phantoms also keeps the new rule inside the standing promise that the tray never enables, restarts or otherwise touches a `USB\` or `HID\VID_` node.

**Pointer fault and fix.** `PointerChildMissing` is raised only on `false` - the child is registered and Windows will not resolve it - and the fix is the same single elevated `pnputil /restart-device` on the live BTHENUM instances that rule 2b uses, so PnP rebuilds the HID children for this mouse. `null` raises nothing. The repair never unpairs, never writes a `LowerFilters` value, never toggles the radio and never touches a phantom node.

**Scroll: the registration faults, and then the stream.** Rules 1, 2 and 2b cover the three registration shapes above. The fourth shape is fault B: the filter is attached, every read is correct, and the mouse was never switched into multitouch mode. The tray has no read that can prove that fault, and it does not guess. Two candidate signals were measured and both were rejected.

**REJECTED SIGNAL: `Diag\LastAclReceived`.** The KMDF filter publishes no IOCTL, no device interface, no WMI class and no ETW provider. Its entire health surface is `HKLM\SYSTEM\CurrentControlSet\Services\<bound filter>\Diag`, which a WDF timer work item rewrites wholesale every 1000 ms. `LastAclReceived` in that key looks like the one value that separates live multitouch (14 to 31, typically 23) from compact boot-mouse traffic (9). It is **not** a fault signal. Measured on the reference PC on a mouse whose **scroll was working while the samples were taken**, four reads 2.5 s apart:

```
t=1  LastAclReceived=23  AclTranslateCount=142271
t=2  LastAclReceived=23  AclTranslateCount=142513
t=3  LastAclReceived=9   AclTranslateCount=142614
t=4  LastAclReceived=23  AclTranslateCount=142827
```

It oscillates. A 9 only means the **last** ACL frame the filter happened to see was short, and healthy traffic contains short frames: the translate counter advanced straight through the `9` sample, so multitouch was flowing the whole time. Mapping 9 to "multitouch is off" would therefore raise a finding on a perfectly good mouse every time a poll landed on a 9 - the same class of error as the zeroed `0x90` pull below. The value is also last-seen rather than last-seen-when: it carries no timestamp and it is registry-persisted across reboot, so it can still read 23 after a boot in which no ACL ever arrived, which is why the driver repo's own watcher refuses to gate on it (`mm-auto-f1-watcher.ps1:140-146`). `LastAclReceived` is deliberately **unused** by the tray. It is written up here so nobody rediscovers it and wires it in.

**The only sound positive proof is counter movement.** `DeviceDiagReader.MultitouchAdvancing(boundFilterName)` reads `AclTranslateCount` from that same `Diag` key, falling back to `Rid12Count` when the first value is absent, remembers the previous value per filter service name, and returns `true` only when the counter **increased** since the previous sample. Everything else is `null`: first sample, unchanged value, missing key or value, a filter name outside the known families, any failure. It never returns `false`, because a counter that is not advancing cannot distinguish a mouse nobody is touching from a mouse whose multitouch stream is off - those are identical reads. So the tray can prove scroll is alive. It can never prove scroll is dead, and absence of movement is therefore **not** a fault by design, not by omission.

**Which is why scroll help is user-initiated, not auto-detected.** The scroll line on a device row reads as working when `MultitouchAdvancing` is `true`, and otherwise carries a neutral not-verified wording - it can only be verified while the mouse is actually in use - never an accusation. The device row also carries **Scroll is not working**, enabled for a v3 mouse whose bound filter is registered, RUNNING and in the stack, which is the state where every registration rule is already satisfied and fault B is the only remaining explanation. The user asserting the symptom is what removes the false-positive risk that made an automatic version unshippable.

**What that dialog says (`RecommendMultitouchWatcher`, guidance only).** First step: power the mouse off and on with the underside switch. It is the no-risk step, and it is what makes the driver package's watcher see the device arrive and send the enable report. *[Inference: the power-cycle step works because that watcher subscribes to device-arrival events, as described above; the step itself was measured to restore scroll, the arrival mechanism is read from the watcher script, not instrumented.]* Permanent fix: install or restart the driver package's multitouch watcher. The dialog reports what the tray can already see about it - `DeviceDiagReader.WatcherState()` reads `C:\ProgramData\MagicMouseDriver\auto-f1-watcher.log` plus the presence of `mm-auto-f1-watcher.ps1` beside it for installed / last heartbeat / last F1 result, with no `schtasks` spawn and no admin - and names that log as the place to check its state by hand. Nothing is elevated, no device is touched, no script is run.

**The tray still does not send F1.** As stated above, the enable feature report `{0xF1, 0x02, 0x01}` is sent by the driver package's `mm-f1-once.ps1` and the `MmAutoF1Watcher` scheduled task around it. Reading that watcher's log and recommending it is the whole of Magic Tray's involvement. Sending the report, or reimplementing the watcher here, remains an explicit **non-goal**.

**Battery: one fault worth reporting.** The sentinels the battery readers already return now reach the planner on `DeviceSnapshot.LastBatteryPct` (`-1` no reading or open failure, `-2` present but blocked, `-3` the poller collapsing three consecutive `-1`s), and exactly one of them raises a finding: a **connected keyboard** reading `-2`. For the Magic Keyboard that value has one known cause and one known fix - the pairing record's SDP cache does not expose Battery Strength (RID `0x47`) as a Feature report, so `HidD_GetFeature` has nothing to read (`MagicMouseTray/KeyboardBatteryDevice.cs:6-22`). That becomes `BatteryReadBlocked`, and it routes to the offer the tray already had: **Fix battery reads** -> one user-confirmed elevated `scripts/kbd-patch-cachedservices.ps1 -Mac <keyboard MAC>`.

**The deliberate battery non-findings, and why they stay non-findings.** A **mouse** reading `-2`, `-3` or `-1` raises **nothing**. An idle mouse legitimately reports no battery: the percent arrives as a **pushed** input report, so no report usually means nobody moved the mouse, and the zeroed `0x90` pull that looks like an answer instead is already a rejected signal - `[90 00 00]` came back 10/10 with a dead wheel and 10/10 again with multitouch confirmed flowing. Nothing the tray can read separates "silent because idle" from "silent because broken", and a mouse is idle for most of the day, so a rule here would nag every healthy PC several times a day. The tooltip keeps reporting `Battery unavailable` / `No reading`, which is an honest statement of an unknown, and the problem row stays empty. Same discipline as unknown pointer evidence and a still multitouch counter: no evidence is not a fault.

### What the app can only guide

| Problem | Plain-language cause | Guided steps |
|---|---|---|
| Zero instances (v1 `030D` after disable plus **Remove device**), for a PID this PC has an `enabled_<pid>` entry for | The pairing is gone. There is no device node, so there is nothing to enable - `pnputil /enable-device` cannot help. | Flip the underside switch off, then on, until the LED blinks. Windows **Settings -> Bluetooth & devices -> Add device**. Then turn **Enabled on this PC** back on if it is still off. |
| USB charge-cable leftovers only, again for a PID with an `enabled_<pid>` entry | `USB\VID_05AC&PID_...` / `HID\VID_...` nodes with Status=Unknown are phantoms from charging. The mouse is not connected over Bluetooth. | Leave the phantoms alone (enabling one is the wrong move). Power the mouse on, or pair it in Settings. |
| Filter driver package missing while the device is live | No filter package is installed for that PID, so nothing can be started. | Install the driver package. For v1/v2 that is the Apple `applewirelessmouse` filter, by Apple's Boot Camp INF or by the sbagirici manual service install - the same Apple-signed binary either way, and no Test Mode on either. For v3 it is the KMDF package. Then re-check. |
| Wheel dead while the bound filter is registered, RUNNING and **in** the device stack - offered only when you click the device row's **Scroll is not working** | Fault B: the mouse never received the Apple multi-touch enable report `{0xF1, 0x02, 0x01}`, so it is still speaking boot-mouse. No tray read can prove this state (see the rejected `LastAclReceived` measurement above), which is why the tray never claims it on its own. | Power the mouse off and on with the underside switch. Then install or restart the driver package's multitouch watcher; the dialog reports what `C:\ProgramData\MagicMouseDriver\auto-f1-watcher.log` says about it. The tray sends no report, runs no script and elevates nothing. |

**Only devices this PC has actually had are reported.** The app sweeps the whole catalog (around two dozen Apple mice, keyboards and trackpads), but a PID can only become a problem when it has live BTHENUM nodes right now, or an explicit `enabled_<pid>` entry in `config.ini` - proof the device was here and the user toggled its row. A catalog device this PC has never owned has neither, so it is never reported as missing, never counted in the problem row, and no `REPAIR_SNAPSHOT` line is written for it. Before this rule a PC with two Apple devices was told it had 22 problems, one per catalog entry it had never seen.

The app **never** unpairs a device, never writes `FLIP:NoFilter`, never toggles the Bluetooth radio, and never enables or restarts a USB or `HID\VID_` phantom node.

### If the fix is blocked

A device lower filter is loaded by PnP when the device stack is built, so it is **not** startable through the SCM: `sc start <filter>` on it legitimately fails with WIN32_EXIT 31 even on a perfectly healthy PC. That exit code on its own is not a diagnosis and not a blocked signature.

If the filter is still not running after the BTHENUM restart, the app checks the two conditions that really can stop a self-signed KMDF driver from loading: test signing off (`SystemStartOptions` has no `TESTSIGNING`) or HVCI / Memory integrity on. Only then does the dialog report a block and give the prerequisites: **Test Mode on**, **Memory integrity off**, then **reboot**, then run the fix again. When test signing is already on and HVCI is already off, the dialog reports a plain failure instead of blaming signing.

On the reference PC test signing was already enabled and HVCI was off, so Test Mode was never the issue there.

### Signing facts follow the driver file, not the PID

**The question the tray asks.** Which device gets a Test Mode or Memory integrity fact is decided by the Authenticode signers of the driver file that device actually depends on - never by the PID, and never by how the driver was installed (`MagicMouseTray/SystemConfigChecker.cs:36-56`). The PID cannot answer it: the same service name and the same file name are used both by Apple's WHQL-signed binary, which loads with nothing switched off, and by a patched, self-signed one. Install provenance cannot answer it either: the sbagirici route puts the genuine Apple-signed file in place by hand, with no INF at all, and needs no Test Mode.

**The reading.** `ReadBoundDriverSelfSigned` (`SystemConfigChecker.cs:760-794`) walks every signature in the file's PE certificate table and returns `true` only when every signer is self-issued, `false` as soon as one signature is CA-issued, and `null` when the file carries no embedded signature, is absent, or the read failed. No chain is built, on purpose: a locally installed root would make chain building succeed while the kernel still refused the driver without Test Mode.

**Worked example, reference PC 2026-09-15.**

| Driver file | Signatures on it | Verdict | What the tray does |
|---|---|---|---|
| `MagicMouseDriver-kmdf-204-scroll.sys` (v3 KMDF) | One, `CN=MagicMouseFix`, self-issued | Self-signed | Test Mode and Memory integrity are in scope. Either of them misconfigured while this driver is bound is **one** blocking fact. |
| `applewirelessmouse.sys` (v1/v2, on either install route) | A `WDKTestCert` self-issued primary signature **plus two valid `CN=Microsoft Windows Hardware Compatibility Publisher` signatures** | Not self-signed | No signing fact at all. Windows loads a file when **any** of its signatures is trusted, so this binary loads with nothing switched off - which is why the primary signature alone is not read as the answer. |
| Stock `HidBth.sys` | None embedded (catalog-signed only) | Unknown | Silent. |

The `SYSTEM_CONFIG` line in `debug.log` carries that verdict per device. On the reference PC, 2026-09-15: `kind=MagicMouseV3 pid=0323 status=PatchedKmdf role=InUse selfsigned=true testsigning=true hvci=false blocking=0` beside `kind=MagicMouseV1 pid=030d status=Ok role=NotRelevant selfsigned=false testsigning=unknown hvci=unknown blocking=0`.

**Unknown is never a problem.** A signature the tray could not read is reported as unknown. It may never claim the driver is self-signed, and it may never reach Blocking - the same tri-state rule the rest of this document runs on. Blocking needs a positive reading: a bound driver measured as self-signed while test signing is off, or while HVCI is on (`SystemConfigChecker.cs:279-281`).

## Mode A/B battery flip (patched Apple driver only)

This applies to one device in one state: a Magic Mouse v3 (`0323`) bound to the **patched Apple** driver (`DriverStatus.PathAPatched`). KMDF reads the battery natively and needs none of it. Stock Windows has no flip.

**Why a flip exists at all.** That driver exposes the mouse in one of two shapes and cannot do both at once. Mode A is the v3 HID path with the `col02` collection present: the battery report is readable and scroll is dead. Mode B is the unified path with no `&col0x`: scroll works and the battery cannot be read at all. Mode B is the resting state, so a percent has to be fetched by going to Mode A and coming back. Nothing in the app can park the mouse in Mode A on purpose.

**The contract (`ModeFlip.ReadBatteryViaFlip`).**

1. Before anything is elevated, the tray writes a sentinel file in its own data directory: a cycle is starting, and for every registry key the cycle will touch, that key's previous `LowerFilters` value verbatim - including whether the value was present at all, so "absent" stays distinct from "empty". That file is what makes a crash recoverable.
2. One elevated PowerShell run owns the whole cycle, so you approve **one** UAC prompt covering flip, read and restore together.
3. The elevated run flips to Mode A and reports ready only once `col02` is actually present. Reaching Mode A has a budget of 45 seconds.
4. The tray reads the percent **unelevated**, with the same battery reader it always uses, and then tells the elevated run to continue.
5. The restore to Mode B runs from a PowerShell `finally`, so it happens whether or not the middle of the cycle worked.
6. Mode B is **verified, not assumed**: the elevated run re-reads `LowerFilters` and the present HID interface shape after the device restart, and the tray independently re-reads `LowerFilters` and confirms the unified v3 path with no `&col0x`. Only then is the cycle reported as restored, and only then is the sentinel deleted.

Through steps 3 to 6 Windows tears the mouse stack down and rebuilds it, so the pointer and the wheel stop responding for a few seconds. That is expected, it is not a fault, and it is over by the time the tray reports the result.

**Failure modes, what you see, and what to do.** Every outcome carries the percent it managed to read, whether Mode B was restored, and a detail string.

| `ModeFlipOutcome` | What you see | What to do |
|---|---|---|
| `Ok` | The percent, and the mouse back in scroll mode. | Nothing. |
| `NotPathA` | The device is not on the patched Apple driver, so nothing runs. | Nothing. On KMDF the percent is read without any flip. |
| `NoInstances` | Nothing runs: there is no live Bluetooth instance for that PID to act on. | Power the mouse on, let it reconnect, then ask again. |
| `Cancelled` | You declined the UAC prompt. No registry write, no device restart, the sentinel deleted again, and the mouse never left scroll mode. | Nothing. Ask again and approve the prompt if you do want the reading. |
| `FlipFailed` | Mode A was not confirmed inside the 45-second budget, so there is no percent. The restore still ran and was still verified. | Scroll is fine. Try again later, or read the battery on KMDF instead. |
| `BatteryUnreadable` | Mode A was reached but no percent came back. The restore still ran and was still verified. | Scroll is fine. An idle mouse legitimately sends nothing - see the battery non-findings above. Move the mouse and ask again. |
| `RestoreFailed` | The one bad case. The tray does **not** claim success: it reports that Mode B could not be verified and that **scroll may be dead**, and it leaves the sentinel in place. | Take the **restore scroll mode** action the tray offers: one more UAC prompt, one more verified re-read. If that fails too, Bluetooth & devices -> Remove device, power the mouse off and on, Add device - the measured way to make Windows rebuild the layout. |

So the restore always runs once the elevated script has started, and `Ok`, `FlipFailed` and `BatteryUnreadable` all end with Mode B confirmed. `RestoreFailed` is the only outcome that leaves scroll in doubt, and it is never reported as success.

**Crash safety.** The sentinel outlives both the app and the PC. `ModeFlip.StaleModeAOnStartup()` is true whenever that file is still present and parses, which means some earlier cycle never had its restore verified: the app was killed, the PC rebooted mid-flip, or a `RestoreFailed` was never cleared. The next start offers the one-click restore - `ModeFlip.RestoreModeB()`, its own single UAC prompt, the same verified re-read, and the sentinel removed only once Mode B is confirmed. Nothing is retried silently, and nothing is retried in the background.

**What the flip never does.** Its only writes are the `LowerFilters` values it recorded plus the device restart, so it never installs, removes or rebinds a driver package and cannot change which driver you chose. It never unpairs the mouse, never toggles the Bluetooth radio, and never touches a `USB\` or `HID\VID_` phantom node. It never runs on its own: the only two entry points are you asking for a reading and you accepting the restore offer. And it **never sends F1** - the multitouch enable report remains the driver package's job, exactly as above.

## Tray: Enabled on this PC

Uncheck -> confirm -> UAC. The pointer (or keyboard) stops **here**. Pairing is unchanged.

Check it again **only if the device is still in Device Manager / Bluetooth**. If you also **removed** it in Settings, Enable fails with "not present" and offers Bluetooth settings. That is correct: there is nothing to start.

Rows are per **model PID**, not per physical mouse. Two Magic Mouse v1 share one checkbox.

USB charge-cable leftovers (`USB\VID_05AC&PID_...`, `HID\VID_...`, `CM_PROB_PHANTOM`) are **not** toggled. Those are not the Bluetooth mouse.

Log: `DEVICE_ENABLE pid=... val=...` then `exit=... sidecar=ok|failed|no-instances`. Sidecar is `%TEMP%\mm-enable-<pid>.status` so the result does not depend on UAC `ExitCode`.

## Windows Settings

Tray -> **Bluetooth** -> **Add or change devices...** / **Turn Bluetooth on or off...** opens `ms-settings:bluetooth`. The tray does not pair, does not flip the radio, and does not rename HID devices.

**Remove** in Settings deletes the pairing. After that:

1. Magic Mouse v1: flip the underside switch **off, then on**, until the LED blinks (pairing mode).
2. Magic Mouse v3: power off/on; Add device while it is on.
3. Settings -> Bluetooth -> **Add device**.
4. If tray `config.ini` still has `enabled_030d=false`, check **Enabled on this PC** (or delete that line; default is enabled).

Hardware on/off reconnects a **still-paired** mouse. It does not re-pair a removed one.

## Scripts (elevated CLI fallback)

The in-app path above covers all three field faults. Reach for the scripts when you want a full capture for a bug report, when the app cannot run, or when you are working over a remote shell.

| Script | Use when |
|---|---|
| `scripts/diagnose-and-recover.ps1` | After an incident. All catalog PIDs, the filter service each device actually binds (KMDF-family names such as `MagicMouseDriver` or `MagicMouseDriver204Scroll`, or Boot Camp `applewirelessmouse`), USB phantom vs BTHENUM, `config.ini`, last 50 `debug.log` lines. Optional `-Repair`. |
| `scripts/capture-state.ps1` | Pre/post-reboot **v3 (0323) only** compare. Not a v1 tool. |
| `diagnose-driver.ps1` | Deep dive on `applewirelessmouse.sys` (v1/v2 Boot Camp). |
| `scripts/mm-bt-stack-snapshot.ps1` | Live BT HID stack dump. No repair. |

Tray Diagnostics launches those **without** `-Repair`. Script repair is CLI-only, elevated:

```powershell
# Capture (no admin required for most fields)
.\scripts\diagnose-and-recover.ps1

# v3 wheel dead, device still paired: restart BTHENUM only
.\scripts\diagnose-and-recover.ps1 -Repair

# One device
.\scripts\diagnose-and-recover.ps1 -DevicePid 0323 -Repair
```

`-Repair` will:

- `pnputil /restart-device` live **BTHENUM** instances whose bound lower filter is not running, so PnP rebuilds the stack and loads it
- **not** unpair, **not** `FLIP:NoFilter`, **not** disable the Bluetooth radio, **not** enable USB phantoms
- if zero instances: print pairing steps and skip

## This incident (2026-09-06)

- **v3 scroll after charge:** our own tooling misdiagnosed this one. It reported `MagicMouseDriver` STOPPED with WIN32_EXIT 31 and called that the cause. Measured truth on the PC: the 0323 BTHENUM instance binds `LowerFilters = MagicMouseDriver204Scroll`, and **that** service is RUNNING. A second, older `MagicMouseDriver` service is installed on the same PC but is **not** bound to the device; our checks hardcoded that name, read the stale service, and cried wolf. A stopped, unbound same-family service is not a fault - only the service actually named in the device's `LowerFilters` counts. Two further corrections: `sc start` on a PnP lower filter always exits 31 (PnP loads it when the stack is built; the SCM cannot), so exit 31 is not evidence of anything; and test signing was already on with HVCI off here, so Test Mode was never the issue. When the wheel really is dead: tray problem row (or elevated `-Repair` on 0323) runs `pnputil /restart-device` on the live BTHENUM parent, then confirm the **wheel**. Pointer + battery (COL02 Input 0x90) do not need the filter. Do not re-pair v3 (MAC `D0C050CC8C4D` is live).
- **v1 not re-enabling / not discoverable:** PID `030D` absent from PnP and Enum after disable + Settings Remove. `enabled_030d=false`. Enable cannot help. Pair in pairing mode, then enable the tray row if it is still off. The tray problem row now says this instead of offering a useless Enable.
