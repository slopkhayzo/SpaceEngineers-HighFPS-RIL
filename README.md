# SpaceEngineers.HighFPSFix

> [!WARNING]
> This patch has been pretty much entirely been generated using AI; 
> I won't and will never claim to have enough knowledge or expertise to do this 
> kind of reverse-engineering by myself; I've tested (very) briefly and so far spotted 
> no major issues (on my machine ofc, if you have issues feel free to open an Issue).
> If you're interested and want more (human generated) info, I have a blog post [here](https://slop-blog.enkhayzomachines.net/posts/space-engineers-high-fps-ril) :)

High-frame-rate presentation fix for *Space Engineers* (Steam, 64-bit),
delivered as an ASI plugin.

The game simulates at a fixed 60 steps per second on its update thread and
draws on a separate render thread, which it caps at 120 frames per second.
A frame drawn between two steps shows the newest step again, so anything
above 60 FPS adds no motion. The fix raises the render cap (by default to the
display's refresh rate) and makes every frame show the world between the two
most recent simulation steps: the camera, every moving object, and character
skeletons are interpolated on the render thread. Physics, scripts, AI, sound,
and simulation speed are unchanged; only render-side copies of transforms
change, and they are rebuilt from the simulation's own data every step.

The game already runs simulation and rendering on separate threads, so no
simulation/render split (as in the Amnesia: The Bunker OptiPerf project) is
needed.

## How it works

- **Loading.** `SpaceEngineers.exe` is a .NET Framework 4.8 program; its only
  import is `mscoree.dll`, whose dependencies Windows resolves from System32
  only, so the usual `version.dll` loader never loads. The Ultimate ASI
  Loader is shipped as `dinput8.dll` instead: the game always loads
  DirectInput (it reads the mouse through it) with the normal search order
  ([third_party/ultimate-asi-loader/UPSTREAM.md](third_party/ultimate-asi-loader/UPSTREAM.md)).
- **Native plugin** (`SpaceEngineers.HighFPSFix.asi`, C++): waits until the
  game's CLR is running, then starts the managed part in the game's default
  AppDomain through the CLR hosting API (`ICLRRuntimeHost::ExecuteInDefaultAppDomain`).
  It hosts MinHook and the log.
- **Managed part** (`SpaceEngineers.HighFPSFix.Managed.dll`, C#): hooks the
  native code of a few game methods (the game's assemblies are NGen-compiled)
  with MinHook and calls the originals through function pointers. A startup
  self-test proves the mechanism on its own methods first. Hooks are
  installed per feature, all or none, in one batch; the render hooks are
  installed from the render thread itself (its `BeforeDraw` event), between
  two draws. Patching them from another thread while the renderer was inside
  them crashed the runtime.
- **Render cap.** The render thread's own `WaitForTargetFrameRate` gets the
  configured target (or no limit).
- **Interpolation** (`src/managed/Interpolation`). Each update frame is read on
  the render thread before the renderer processes it (`MyRender11.ProcessRenderFrame`
  / `ProcessPreFrame`): object transforms (`MyRenderMessageUpdateRenderObject`),
  the camera (`MyRenderMessageSetCameraViewMatrix`) and character skeletons
  (`MyRenderMessageSetCharacterTransforms`). After the renderer applied the
  newest frame (`MyPostponedUpdateManager.Apply`), every draw sets the actors,
  the camera matrices and the skinning palettes to the state at the
  presentation clock's display time: one update interval plus the measured
  update latency behind real time, blended between the frames around it.
  Teleports, camera cuts, and turns over 90 degrees in one step are not
  blended.
- **Mouse look** (`src/managed/MouseLook`). Interpolation alone shows a mouse
  turn one step plus the update latency late, at 60 steps per second. In
  first person on foot the render thread therefore also reads the mouse
  every draw and turns the interpolated camera by the counts the
  interpolated state does not contain yet:
  `lead = scale * (counts read now - counts blended at the display time)`.
  The game still receives every count exactly once: the hook on
  `MyDirectInput.GetMouseState` adds whatever the render thread read since
  the game's last read to the game's own read (one lock serializes both). A
  hook on `MySharedData.AfterUpdate` records, per simulation step, the counts
  the game had received, whether the player looks through their own
  character in first person with the gameplay screen focused, the
  character's up axis and the render objects of the character and its held
  tool, which turn with the camera about the camera. The radians per count
  are not assumed but learned per axis from the camera turns the game
  produces (an exact match attaches the axis, counts that turn nothing
  detach it, two agreeing ratios adopt a new sensitivity or zoom), and the
  lead fades in and out over 50 ms. Both hooks run on the update thread and
  are installed from it (`MySandboxGame.Invoke`).

## Build

Visual Studio 2026 (MSVC x64, its Roslyn C# compiler and CMake/Ninja), the
.NET Framework 4.8 reference assemblies, and an installed copy of the game
(the managed part compiles against its assemblies):

```bat
"C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat"
cmake -S . -B build-x64 -G Ninja -DCMAKE_BUILD_TYPE=RelWithDebInfo ^
  -DSEHFF_GAME_BIN64="D:/Program Files/Games/Steam/steamapps/common/SpaceEngineers/Bin64"
cmake --build build-x64
ctest --test-dir build-x64 --output-on-failure
```

`build-x64/bin` then holds the ASI, the managed DLL and the development INI.
The tests check the transform blending, the presentation clock, and the mouse
look's geometry and learning (against a simulated game) without the game.

## Configuration files

- [packaging/SpaceEngineers.HighFPSFix.ini](packaging/SpaceEngineers.HighFPSFix.ini)
  is the release INI, with every setting documented inline.
- [config/SpaceEngineers.HighFPSFix.ini](config/SpaceEngineers.HighFPSFix.ini)
  is the development default (diagnostics on, `[Debug]` keys for unattended
  runs). It is copied beside the plugin on every build.

## Packaging

```powershell
cmake --build build-x64 --target package_plugin_only
cmake -S . -B build-x64 -DSEHFF_ULTIMATE_ASI_LOADER_DLL=<path to the v9.7.4 x64 loader DLL>
cmake --build build-x64 --target package_loader_bundled
```

These write two flat archives to `dist/`:

- `SpaceEngineers.HighFPSFix-<version>-plugin-only.zip`
- `SpaceEngineers.HighFPSFix-<version>-with-Ultimate-ASI-Loader.zip`

The second target exists only when CMake is configured with
`SEHFF_ULTIMATE_ASI_LOADER_DLL` pointing at the official x64 Ultimate ASI
Loader `v9.7.4`; its hash is checked against the value in
[third_party/ultimate-asi-loader/UPSTREAM.md](third_party/ultimate-asi-loader/UPSTREAM.md),
and it is packaged as `SEHFF_LOADER_NAME` (`dinput8.dll`). Both archives
carry the same ASI, managed DLL and INI, the end-user README from
[packaging/](packaging/), the applicable licenses, and `SHA256SUMS.txt`.
Everything goes into the game's `Bin64` folder.

## Development runs

[scripts/run_session.ps1](scripts/run_session.ps1) runs one unattended
session: it installs the loader, the plugin and an INI into `Bin64` for the
run only, starts the game through Steam, lets the plugin load a test world
from `research/worlds` (`[Debug] AutoLoadWorld`) and quit after `-Seconds`
(`[Debug] AutoQuitSeconds`), collects the plugin log, the game log and the
per-draw trace (`present.csv`) into `research/runs/<stamp>-<name>`, and
removes everything it installed. It launches the game, so on a shared test
machine it goes through a job queue, for example:

```powershell
& '<queue>\queue.ps1' submit -Name 'what it measures' -Owner 'SpaceEngineers-HighFPSFix' `
    -Command "& '<repository>\scripts\run_session.ps1' -Name test -World RedShip -Seconds 30"
```

`-Set 'Section.Key=Value;...'` overrides INI values for the run.
[scripts/analyze_trace.py](scripts/analyze_trace.py) summarizes a run's
presentation trace. For mouse look, `[Debug] AutoMouseSpeed` sends a
repeating pattern of synthetic mouse turns (only while the game window is in
the foreground and the player is in first person), `AutoMouseAlternate`
switches the lead on and off every other repetition (A/B in one session),
`AutoEquip` puts a hand tool in the character's hands, `AutoFocus` asks
Windows for the foreground (granted only while the machine is idle), and
`MouseLookApply = false` keeps everything but the lead.
[scripts/analyze_mouse.py](scripts/analyze_mouse.py) compares the presented
yaw with the counts read (best-fitting delay, per-draw error) and the
character's yaw with the camera's. The game reads no mouse while its window
is unfocused, so mouse runs need the game window focused (one click into it
once the world has loaded).

## Logs

`SpaceEngineers.HighFPSFix.log` is written beside the ASI (or under
`%LOCALAPPDATA%\SpaceEngineers.HighFPSFix` if that is not writable) and
replaced on each launch, with the previous session kept as
`SpaceEngineers.HighFPSFix.previous.log`. With `Diagnostics = true` the render
and interpolation counters are reported every five seconds.

The private `research/` directory (IL dumps, test worlds, runs, notes) is
intentionally excluded from Git.

## Third-party

- [MinHook](third_party/minhook/) for function hooking.
- [Ultimate ASI Loader](https://github.com/ThirteenAG/Ultimate-ASI-Loader)
  (binary not stored in Git) for the loader-bundled archive.

## License

MIT, see [LICENSE](LICENSE). MinHook and the Ultimate ASI Loader keep their
own licenses ([third_party/](third_party/)); both are included in the release
archives.
