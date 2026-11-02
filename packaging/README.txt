SpaceEngineers.HighFPSFix
=========================

High-frame-rate fix for Space Engineers (64-bit Windows, Steam).

Space Engineers simulates the world 60 times per second and draws on a
separate thread, capped at 120 frames per second; every frame drawn between
two simulation steps shows the same picture again. This fix raises the cap
to your display's refresh rate and makes every drawn frame show the world
between the two most recent simulation steps, so motion is smooth at the
display's rate. Physics, scripts, AI, sound, and the simulation speed are
unchanged: only what the renderer shows between two steps changes.

What it smooths
---------------
- The camera: position, rotation, and field of view, in first and third
  person, in cockpits and on turrets
- Ships, stations, rovers and everything on them, including rotors,
  pistons, hinges, doors, turrets, lights and thruster flames
- Characters, the first-person arms, and tools and weapons in hand
- Floating objects, debris, meteors, and other moving objects
- Mouse look in first person on foot: the camera, your arms and your tool
  turn with the mouse on every drawn frame (see below)

Requirements
------------
- Space Engineers for Windows (Steam). Developed and tested with version
  1.210.014. The fix checks the game code it depends on when the game
  starts and switches itself off if that code does not match, for example
  after a game update.
- An x64 ASI loader. It is not included in this archive; the
  "with-Ultimate-ASI-Loader" archive includes one.

Installation
------------
Place SpaceEngineers.HighFPSFix.asi, SpaceEngineers.HighFPSFix.Managed.dll
and SpaceEngineers.HighFPSFix.ini in the game's Bin64 folder, beside
SpaceEngineers.exe (Steam: right-click Space Engineers > Manage > Browse
local files > Bin64).

Settings
--------
SpaceEngineers.HighFPSFix.ini lists every setting with a short description.
Set Enabled = false under [General] to switch the fix off without removing
it.

The frame rate is capped at the refresh rate of the display the game is on
by default; set TargetFPS under [FrameRate] to choose another cap, or -1 for
none (then use VSync or an external limiter). With VRR (G-Sync, FreeSync),
a cap a few frames below the refresh rate keeps the display in its variable
range.

What you see trails the newest simulation step by about one step (16.7 ms)
plus the time the game takes to compute a step. This is inherent to
interpolation.

Mouse look: in first person on foot, the fix also reads the mouse on every
drawn frame and turns the view by the movement the game has not shown yet,
so turning has no added delay and is as smooth as your frame rate. The game
itself still receives every mouse movement exactly once, in its own
simulation steps, so where you aim, what you hit and what other players see
are unchanged. The fix measures how far the game turns per mouse count
(sensitivity, aiming down sights, jetpack) and only leads the view while
the game turns exactly that much; in menus, the terminal and the inventory
it stands aside. Set MouseLook = false under [Interpolation] to turn it
off.

Not covered
-----------
- HUD markers drawn over 3D objects (GPS markers, target brackets) are
  positioned for the newest simulation step, so during fast turns they can
  lead the smoothed view slightly.
- Bullets, tracers and some particle effects not attached to an object
  still move at 60 steps per second.
- The mouse cursor in menus and inventories moves at 60 steps per second.
- In cockpits, on turrets, in third person and in the spectator camera the
  view is interpolated but not led by the mouse: mouse turns appear with the
  interpolation's delay there.
- Multiplayer, mods and the dedicated server are untested.

Troubleshooting
---------------
SpaceEngineers.HighFPSFix.log is written beside the plugin and replaced on
each launch; the previous session is kept as
SpaceEngineers.HighFPSFix.previous.log. If the game folder is not writable,
the log goes to %LOCALAPPDATA%\SpaceEngineers.HighFPSFix instead. If the fix
switches something off, the reason is in the log. Set Diagnostics = true in
the INI before reproducing a problem you want to report.

Removal
-------
Remove SpaceEngineers.HighFPSFix.asi, SpaceEngineers.HighFPSFix.Managed.dll,
SpaceEngineers.HighFPSFix.ini, SpaceEngineers.HighFPSFix.log, and
SpaceEngineers.HighFPSFix.previous.log. Do not remove a shared ASI loader
unless no other installed plugin depends on it. The game's own files and
save data are not modified.

Misc
-------
For more game fixes, visit https://slop-blog.enkhayzomachines.net/fixes :)
