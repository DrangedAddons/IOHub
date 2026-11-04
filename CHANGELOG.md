# Changelog

## 0.6.1
- **The device menu opens.** On the Triggers page, *Change a device* → *Pick a device and what to change* listed the
  kinds of device, but clicking one did nothing: the app's menu style had no room for a submenu. Menus now open their
  submenus to the right (Controllers → your controller → Mouse mode → Toggle), with an arrow on items that have more.

## 0.6.0
- **The profile picker is a THEN choice now.** *Profile picker* and *Controller picker* are no longer separate trigger
  types: a *Key gesture* or *Controller buttons* trigger's THEN is *Switch to a profile*, *Pick a profile* or *Change
  a device*. Existing picker triggers carry over as they were.
- **While held for profiles.** Key gestures and controller buttons can switch to a profile while held, and back to
  the previous setup when let go (a press and let-go counts as one switch for the loop guard). While held on an on/off
  device function was already there; it isn't offered for pickers or one-off actions.
- **Single controller buttons.** A controller trigger can be one button on its own, like the Xbox button. The Triggers
  page says when that button also does something in games (or, for the Xbox button, opens Game Bar). Combinations
  still only count when exactly their buttons are held.

## 0.5.0
- **Triggers can change a device.** A trigger's *Then* is now either *Switch to a profile* or *Change a device*: pick
  the kind of device, the device, the feature and what to do with it. To start with:
  - Xbox controllers: Mouse mode (toggle, on, off).
  - Paired LG TVs: Power (toggle, on, off) and Input (HDMI 1-4).
  - Sound output and microphones: Mute (toggle, mute, unmute) and Make it the default.

  The list comes from what's connected, so a device only offers what it can do. Big Picture triggers can change a
  device too, with *Undo it when Big Picture closes*.
- **While held.** A new gesture for keys and controller buttons, offered when the trigger changes something that's
  on or off: on while held, back off when let go (like Steam's hold-the-Xbox-button mouse).
- **Mouse mode.** An Xbox controller drives the mouse, for the desktop on a TV. Switch it on under Devices → Input →
  Mouse mode ("Let IO Hub control the mouse with Xbox controllers"), then turn it on and off with a trigger.
  - Left stick moves the pointer (hold LT for slow and precise), right stick scrolls, A/B/X click left/right/middle,
    LB/RB are back/forward, the D-pad is the arrow keys, Menu is Enter, View is Esc and Y opens the on-screen keyboard.
  - Menu, View and Y act when let go and only on their own, so a View + Menu trigger doesn't also press Esc and Enter.
  - It pauses while a game or other fullscreen app is in front (games still see the controller), unless that's
    switched off. Four pointer speeds.
  - Turn off Steam's controller mouse (Guide Button Chord Layout and Desktop Layout) so the two don't fight.
- **Devices page shows what can be changed** when *Show functions* (top right) is on. Each tab has a functions table: *Controller functions* (Input), *TV
  functions* (Video, for paired LG TVs) and *Sound functions* (Audio, sound output and microphones), grouped by device.
  Every row is one function, with buttons to do it now and its triggers on the right (click one to open it).
  *Add a trigger* opens a new trigger on the Triggers page with the device already picked. Sound devices also have
  a ⋯ menu with the same things.

## 0.4.1
- **A TV turned off stays off.** Auto return switched the TV back to its old input and turned it off straight away.
  Switching inputs wakes the device there over HDMI-CEC (an Apple TV, say); one still waking when the TV went off
  missed the TV's "everyone off" signal and turned the TV back on. IO Hub now leaves the TV on the old input for
  8 seconds first, so that device is awake and goes to sleep with the TV. For 20 seconds after turning a TV off (Auto
  off or Auto return), if it comes back on by itself IO Hub turns it off once more, and `switch.log` says so.
- This happens in the background, so switching doesn't wait for it. Switching back to a profile that uses the TV,
  or *Turn it on now*, cancels it straight away.
- A TV that's in the middle of turning off counts as off (it can still report itself on for a few seconds).

## 0.4.0
- **Xbox controller triggers.** New trigger types under *Xbox controller*:
  - *Controller buttons*: press, double tap, triple tap or hold a button combination (like View + Menu) to switch
    to a profile, or to switch back with *Do it again to switch back*.
  - *Controller picker*: a button combination opens the profile list, then A, B, X, Y or a D-pad direction picks
    the profile (profile 1 is A, 2 is B, and so on, up to eight). View cancels.

  Click the buttons box, hold the buttons together on the controller, then let go. A combination needs two buttons
  or more, and only counts when exactly those buttons are held, so it won't go off mid-game while you're holding
  something else. LT and RT count as buttons; so does the Xbox button, in a combination. Controllers are only read
  while a controller trigger is on.
- **Auto return leaves the input as it was.** If the TV was off when IO Hub turned it on, IO Hub notes the input it
  woke up on (say HDMI 1 for the Apple TV). Leaving, it switches back to that input, then turns the TV off, so the
  remote turns it on there next time.

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
