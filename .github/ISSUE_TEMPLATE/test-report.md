---
name: Test report
about: I ran Magic Tray on a device it already knows. Here is what happened.
title: "test: PID xxxx — model name"
labels: device, help wanted
assignees: ''
---

Thanks for testing. Only three devices have ever been confirmed by a tester, so this really helps.

Fill in the table. Skip any row that isn't your device. Guesses are worse than blanks.

Magic Tray shows **nothing at all** for your device? Use the [missing device form](https://github.com/LesleyMurfin/magic-tray/issues/new?template=missing-device.md) instead.

## Your report

| Question | Your answer |
|---|---|
| Model, exactly as printed on the device | |
| Four-character device code | |
| Connected by | Bluetooth / USB cable |
| Windows version | |
| Magic Tray version | |
| Did a battery percent appear, and what did it say? | |
| **Keyboards:** percent before **Fix battery reads** | |
| **Keyboards:** percent after **Fix battery reads** | |
| **Mice:** does scrolling work? | yes / no |
| **Mice:** driver name in the tray menu | KMDF / Boot Camp / Stock / Patched Apple / Not bound |

Trackpads: the battery rows above are all we need. Magic Tray does not offer a driver change for a trackpad, so there is nothing to report there.

## Where to find each answer

- **Four-character device code.** Open Device Manager. Find your device. Right-click it, then Properties. Open the Details tab. In the box, pick **Hardware Ids**. Read the four characters after `PID`, like `024F`.
- **Windows version.** Settings, then System, then About.
- **Magic Tray version.** Bottom of the tray menu, or right-click `MagicMouseTray.exe`, Properties, Details.
- **Battery percent.** Right-click the Magic Tray icon, then **Refresh Now**. Copy what it says.
- **Keyboards.** **Fix battery reads** is in the tray menu, under your keyboard. It asks before it changes anything. Try the battery before you run it and again after.

Anything else you noticed:
