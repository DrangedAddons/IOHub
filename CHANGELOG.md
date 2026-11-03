# Changelog

## 0.3.0
- **LG TVs turn on reliably.** IO Hub used to skip the wake-up whenever the TV answered on the network, and a TV with
  Quick Start+ answers while it's off, so nothing was sent. It now asks the TV whether it's on. If it's in standby it
  gets Wake-on-LAN plus the TV's own "turn on", repeated until the TV says it's on (up to 40 seconds). If only the
  picture is off, the picture is turned back on. A profile with *Auto on* now does this even while Windows still
  lists the TV.
- Wake-on-LAN goes out every network adapter, to each one's subnet and to the TV's address, so a VPN or virtual
  adapter can't swallow it. IO Hub learns the TV's network cards (wired and Wi-Fi) from the TV itself when pairing
  and whenever it's on.
- If LGTV Companion is installed, IO Hub asks it to turn the TV on as well, in case its route works where IO Hub's
  doesn't. It isn't needed.
- **Auto return.** A second choice under *When leaving*, next to *Auto off*. It returns the TV to how it was before
  IO Hub switched to it: off if it was off, otherwise the input or app it was showing (Apple TV on HDMI 1,
  Netflix…). Turn on one or the other; with neither, the TV is left on the PC's input.
- Searching for TVs to pair asks from every network adapter. With a TV already paired, an empty search says "No other
  LG TVs found to pair" (the paired TV is reached by its address either way).
- "Turn it on now" shows that it's working, and every step of turning a TV on goes to `switch.log`: its power state,
  where the wake-up went, and how long it took.

## 0.2.0
- **Displays come back at their own refresh rate.** Turning a display back on (switching from the TV to the monitor)
  let Windows pick its mode, which is often 60 Hz. IO Hub now remembers each display's resolution and refresh rate
  while it's on and asks for exactly that when it turns it back on. If a display still comes up different, the switch
  notice says so.
- **Safer switching.** Switches now queue instead of overlapping, display changes are at least 3 seconds apart, and
  Big Picture is given time to settle after the displays change (Steam recreates its window, which could look like
  Big Picture closing and set off a switch back and forth). If triggers switch 8 times within 2 minutes, they pause
  and say so instead of carrying on.
- **Switch log.** Every switch, its steps, how long Windows took and any problem go to `switch.log` in the data
  folder (Settings → Your data → Open folder), so a problem can be traced.
- **LG TV power, built in.** An LG TV gets an "LG TV power" row under its display in Profiles. *Pair this TV* finds
  LG TVs on the network (or takes an IP address); the TV asks once to allow IO Hub. Then, per profile: *Auto on* turns
  the TV on when switching to that profile (IO Hub waits for it to come up before changing displays), *Auto off*
  turns it off when switching to a profile that doesn't use it, and *Input* switches it to an HDMI input (sent a few
  times, and again after the displays change, because a TV that's just woken up often ignores the first one).
  Turning on needs "Turn on via Wi-Fi" (or Quick Start+) enabled on the TV. Nothing else to install.

## 0.1.3
- **Uses about a third of the memory.** Sitting in the tray: 32 MB instead of 115 MB. In use, after opening every
  page: about 50 MB instead of about 175 MB.
- The download is bigger, about 165 MB instead of 72 MB. The exe is no longer compressed: a compressed one has to
  unpack all of .NET into memory every time it starts, which was most of that 115 MB.
- The Overview's light beam does less work per frame.

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
