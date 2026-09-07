Magic Tray {{VERSION}}
https://magictray.app/

Magic Tray shows your Apple Magic Mouse and Magic Keyboard battery level
beside your clock. It is free. There is nothing to buy.


START HERE

1. Unzip this whole folder somewhere you will keep it.
   Documents is a good spot. Your Downloads folder is not, because you
   will clear that out one day and the app will vanish with it.

2. Keep the "scripts" folder right beside MagicMouseTray.exe.
   The two belong together. Read "KEEP THE FOLDER TOGETHER" below.

3. Open the folder and double-click MagicMouseTray.exe.

4. The first time you open it, Windows may say it does not recognise
   the app. Click "More info", then click "Run anyway".
   Windows says this about every app that has not paid for a signature.
   You can check this download yourself first. See "CHECK YOUR
   DOWNLOAD" below.

5. Look beside your clock. Click the Magic Tray icon to see your
   battery levels. Cannot see the icon? Click the small arrow beside
   your clock to show the hidden ones.

6. Got a Magic Keyboard? Point at its name in the menu, then click
   "Fix battery reads". Say Yes when Windows asks permission, then turn
   Bluetooth off and on again. The percent shows up after that.


KEEP THE FOLDER TOGETHER

Do not drag MagicMouseTray.exe out on its own.

"Fix battery reads" and everything in the Diagnostics menu read files
from the "scripts" folder beside the app. Move the app out on its own
and it can no longer find them, so the keyboard battery unlock stops
working and the Diagnostics menu goes quiet.

Want it somewhere else? Move the whole folder. That is fine. Make your
shortcuts point at the app inside the folder, and leave the folder
where it is.


WHAT IS IN HERE

  MagicMouseTray.exe   The app. This is the one you double-click.
  scripts              The files the app needs for the keyboard battery
                       unlock and for the Diagnostics menu.
  README.txt           This file.
  SHA256SUMS           A fingerprint for each file above.


CHECK YOUR DOWNLOAD

You do not have to do this. It is here if you want it.

Every file in this folder has a fingerprint listed in SHA256SUMS. To
check them, right-click the folder, pick "Open in Terminal", and paste
this in:

  Get-FileHash .\MagicMouseTray.exe -Algorithm SHA256

Compare what you get with the line for MagicMouseTray.exe in
SHA256SUMS. They should match, ignoring capital letters.

The release page also lists a fingerprint for the zip file itself, so
you can check the download before you even unzip it.


NEED A HAND?

Read the guides at https://magictray.app/
Report a problem at https://github.com/LesleyMurfin/magic-tray/issues

Magic Tray is free and open source under the MIT licence.
