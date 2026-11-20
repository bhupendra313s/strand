# How to use Strand

Strand links your Android phone and your Windows PC over your own Wi-Fi. Copy on one, paste on the other.
Send files, share your phone's screen, and paste phone screenshots straight onto the PC. Nothing goes
through the internet or any account.

## What you need

* A Windows 11 PC and an Android phone (Android 10 or later; Android 12 or later for the colour matching).
* Both on the **same Wi-Fi**. In Windows, that Wi-Fi must be set to **Private** (Settings → Network & internet
  → Wi-Fi → your network → Private).

## Install

**On the PC**

1. Download `Strand-windows-x64.zip` from the latest release and unzip it anywhere (for example `Documents\Strand`).
2. Run `ContinuityHub.exe`. (The file keeps its old name so existing setups keep working.)
3. If Windows asks, **allow it on Private networks**. Strand sits in the system tray from now on.

**On the phone**

1. Download `Strand-android.apk` from the same release onto the phone and open it.
2. Android will ask to allow installing from your browser or Files app. Allow it, then tap **Install**.

## Pair the two (once)

1. On the PC, open Strand → **Devices** → **Pair a new phone**. A QR code appears.
2. On the phone, open Strand and tap **Scan QR code**. Point the camera at the PC screen.
3. Check that the fingerprint shown on the phone matches the PC, then tap **Pair**.

The QR code works once and expires after 10 minutes. To add another phone, show a new one.

## Finish setup on the phone

Open Strand → **Settings** and allow:

* **Notifications**: shows that Strand is running and gives you a Send button.
* **Photos: allow all**: needed so new screenshots can be offered to the PC.
* **Battery: unrestricted**: keeps the link alive when the screen is off. Tap **Allow**, then **Allow** again.

## Everyday use

| To… | Do this |
|---|---|
| Copy PC → phone | Just copy on the PC. It's on the phone's clipboard a moment later. |
| Copy phone → PC | Turn on **Auto-send copied text** (Settings), then just copy. Without it: select text → **Send to PC**, or Share → **Send to PC**, or the **Send clipboard** tile or notification button. |
| Paste a phone screenshot on the PC | Take the screenshot, then press **Ctrl + V** in any PC app. It downloads only when you paste. |
| Send files PC → phone | Drag files or folders onto the Strand window, or **Choose files** on Home. They land in the phone's `Download/QuickDrop`. |
| Send files phone → PC | Share → **QuickDrop to PC**, or **Send files** on Home. They land in the PC's `Downloads\QuickDrop`. |
| Show your phone screen on the PC | Phone: **Share screen** on Home → choose **Entire screen** → **Start**. A window opens on the PC. Close it, or tap **Stop sharing**, to end. |

## Turning things on and off

* **Whole features** (clipboard, screenshots, QuickDrop, screen share): **Features** page on either device. Tap the
  arrow on a card for its extra settings.
* **One clipboard direction only**: phone → Features → Clipboard text → arrow → switch off **PC → phone** (or
  **Phone → PC**). The PC has the same two switches.
* **Pause everything for a while**: PC → Home → **Pause**, or the tray icon → Pause sync.
* **Match colours**: the PC uses your phone's wallpaper colours. Turn it off in PC → Settings → **Match my phone's colours**.

## Auto-send copied text

Android doesn't let background apps read the clipboard, so this uses an accessibility service that only
watches for Android's "copied" preview and then sends the text once. To turn it on: phone → Strand →
Settings → **Auto-send copied text** → switch on, then in Android's Accessibility page turn on
**Strand: auto-send copied text**.

* After updating Strand, Android may switch it off. If Settings shows **Not running**, turn it off and on again.
* Copies that password managers mark as private are never sent.

## If something doesn't work

* **Phone says "Looking for…" or stays disconnected**: same Wi-Fi on both? Wi-Fi set to **Private** on the PC?
  Strand allowed through Windows Firewall? Open Strand on the phone once to wake it up.
* **Sync stops when the phone is idle**: set **Battery: unrestricted** (phone → Settings).
* **Ctrl + V pastes nothing after a screenshot**: allow **Photos: allow all** on the phone.
* **Start over**: PC → Devices → **Forget** the phone, then pair again.

## Privacy

Everything stays on your local network, encrypted with certificates the two devices exchange when you scan
the QR code. There are no accounts, no cloud and no tracking. The PC log (`%APPDATA%\ContinuityHub\hub.log`)
records sizes and ids, never what you copied.
