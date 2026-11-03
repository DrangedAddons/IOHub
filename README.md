# IO Hub

Switch your PC between setups in one go: which displays are on, the main display, the default speakers and microphone, your main input and camera. Set it off with a hotkey, a key gesture, or an integration like Steam Big Picture.

Made for moving from the desk monitor and headset to the TV and a controller, and back, but a setup can be anything: streaming, work, a second room.

## Install

Download `IOHub.exe` from the [latest release](https://github.com/DrangedAddons/IOHub/releases/latest) and run it. It offers to install itself for your Windows account (no administrator needed), with Start menu and desktop shortcuts, and starts with Windows so triggers are ready. Remove it from Settings → Apps.

It keeps itself up to date from the releases here.

## Using it

- **Devices** shows everything it can switch.
- **Profiles** is a table: profiles across the top, devices down the side. Tick what each profile uses. A section left blank isn't changed by that profile.
- **Triggers** are what switch profiles: a hotkey, a double tap, triple tap or hold of one key, a profile picker (a gesture, then press 1–9), or Steam Big Picture opening.
- **Overview** shows which profile the PC is in now.

Closing the window keeps it running in the tray. Right-click the tray icon to switch profiles, pause triggers or quit.

## Building

```
dotnet build src
dotnet publish src -c Release -o publish
```

The release build is one self-contained `IOHub.exe`. Releases are cut by pushing a `v*` tag that matches `<Version>` in `src/IOHub.csproj`; notes come from `CHANGELOG.md`.

Set `IOHUB_DEV=1` to run a release build in place without installing it or touching an installed copy.

The icon is the DrangedAddons triangle in this app's colours: `python tools/make_icon.py tools/icon-template.png src/app.ico preview.png three src/logo.png`.
