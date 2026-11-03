# Changelog

## 0.1.2
- **Switches stop replaying their slide.** A switch now only animates when you flip it; before, every switch on a page
  slid back into place whenever the page refreshed, which made the page look like it was constantly reloading.
- **Much less work in the background.** Downloading an update refreshed the Settings page hundreds of times; now only
  the progress text changes, once per percent. Pages that aren't on screen (all of them, while the app is in the
  tray) no longer redraw when devices or triggers change; they catch up when you open them.

## 0.1.1
- **Beta updates.** Settings → Updates has a new "Get beta updates" switch. With it on, the app also offers
  pre-release versions to try new features early. Leave it off to get full releases only; if you turn it off while on
  a beta, you move to the next full release when it's out.

## 0.1.0

First release.

- **Devices:** lists displays (including ones on standby), audio outputs and inputs with live level meters, controllers, keyboards, mice and cameras. Updates on its own as things are plugged in or changed.
- **Profiles:** a table with profiles as columns and devices as rows. Pick which displays are on and which is the main one, the default sound and mic, the main input and the camera. Blank sections are left alone. Drag columns to reorder, pick colours, and start from your current setup.
- **Triggers:** hotkeys, double tap, triple tap or hold of a key, a profile picker (a gesture, then 1–9), and a Steam Big Picture integration that switches while Big Picture is open and reverts when it closes.
- **Overview:** this PC wired to each profile, with the active one lit. Flow, Hub and Tree layouts.
- **Switching** sets the default audio devices and turns displays on and off. A new display layout asks to be kept and undoes itself after 15 seconds if nobody answers.
- **Runs in the tray,** starts with Windows, installs per-user, and updates itself from GitHub releases.
