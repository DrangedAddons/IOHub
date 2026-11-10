# FishIO

A World of Warcraft addon for WoW Forever. While a fishing pole is in your main hand, it turns off the sounds that aren't part of fishing, so the bobber's splash stands out. Take the pole off and your sound settings go back to exactly how they were.

It's the companion to [IO Hub](https://github.com/DrangedAddons/IOHub)'s *WoW fishing bite* trigger, which listens to WoW's sound for the splash and can rumble your controller. It works on its own too: the splash is just easier to hear.

## Installing
IO Hub carries FishIO and installs it: the WoW fishing bite trigger (or the controller's functions on the Devices page) has *Install in WoW Forever*, *Update* and *Remove*. IO Hub updates installed copies when it starts with a newer one. A copy with `.git` in its folder is treated as a development copy and never touched.

The source lives here, in IO Hub's repo, and is built into IOHub.exe (`src/IOHub.csproj` embeds `FishIO.toc` and `Core.lua`). Bump `## Version:` in `FishIO.toc` and add a CHANGELOG entry when it changes.

## Focus mode
With a fishing pole in your main hand:
- **Off:** music, ambience (wind, water, crowds), NPC dialog, emote sounds, pet sounds, and error voice lines ("Not yet!").
- **On, at full volume:** sound effects. That's combat, interaction, the cast and the splash.
- **Sound in the background** is turned on, so you still hear the splash while another window is in front.

Your own values are saved before anything changes and put back when the pole comes off, when you log out, or the next time you log in after a crash. If you change one of those settings yourself while fishing, FishIO leaves your choice alone.

## Using it
- `/fishio` shows whether it's on, whether a pole is in hand, and whether focus mode is running.
- `/fishio on` / `/fishio off` turns focus mode on or off (on by default).
- `/fishio quiet` / `/fishio loud` turns the chat line when focus starts or ends off or on.

Other addons with their own fishing sound options (EllesmereUI's *Fishing Sounds*, SKTools' *Mute* and *Louder Splash*) change the same settings. Use one of them or FishIO, not both.
