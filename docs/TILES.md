# View-driven tile streaming and cache

Tiles outside the camera view are not rendered at full detail (since the beauty pass they stay loaded at a coarse level, so no hole appears) and finer levels are no longer requested for them. A tile that finished loading
stays in Cesium's cache, so looking back at it shows it at once, with no download. Off switch: `?viewcull=0` (the old "load a radius round the car").

## How
- The main camera drives tile selection; frustum culling hides what is off screen (`enableFrustumCulling` on; `enforceCulledScreenSpaceError = true` since the beauty pass, see below).
- The four streaming cameras no longer load a 600 m circle: they shrink to a **physics ring** (150 m at rest, +2.5 m per m/s, max 330 m: 265 m at the car's top
  speed) so the colliders under the car always exist. Everything else follows the view.
- `TileViewPolicy` raises `maximumCachedBytes` to 2x the device default at start, up to 3x (desktop) / 2x (mobile), never above half of the heap allowance, and adapts it
  every 2 s to the memory really used: -15% above 88% of the allowance, +8% below 62%. A finished tile is dropped only when the device needs the memory.
- The debug HUD shows `tile cache <now>/<max> MB, heap, ring`.

## Verified / not verified
Verified (`bash tools/water/run.sh`, `TileCachePolicyTests`): the budget, adaptation and ring numbers (convergence to the bounds, limits, speed scaling).
**Not verified (needs Unity + a real tileset):** that Cesium keeps and re-shows culled tiles as expected, hole behaviour, memory on real WebGL devices.

## Limits
- Cesium decides per tile what to keep (LRU by last use). The API does not let us pin "tiles that reached 100%" individually; the large cache achieves it in practice.
  Tiles still loading when they leave the view are not kept.
- With a small ring, ground behind or beside the car beyond it can be missing while it is not on screen: drive fast backwards or turn the camera away at your own risk.
- Remote players' cars use the same ring around them.

## Terrain beauty pass
Goal: the ground always looks sharp and complete from the player's point of view. Everything below is in code; **none of it has been run in Unity or
against a real tileset** (only `TileCachePolicy.AheadRange` is covered by `tools/water/run.sh` / `TileCachePolicyTests`). Tune on a real build.

| Change | Where | Why |
|---|---|---|
| `enforceCulledScreenSpaceError = true`, `CulledSse = 128` | `GameRoot` | No holes: tiles outside every camera are kept at a coarse level instead of not existing. Lower `CulledSse` = sharper behind/beside you, more bandwidth. |
| Foveated rendering off | `GameRoot` | It relaxed SSE towards 256 outside the screen centre (blurry edges). |
| Default SSE 32 -> 20 (28 on mobile); saved 32 migrates once (`rw2.tv`) | `GameSettings` | Sharper refinement threshold; `?sse=` and the slider still override. |
| Look-ahead streaming camera (`StreamAhead`, FOV 50, reach 350-900 m by speed) | `StreamingCameras`, `TileCachePolicy.AheadRange` | Fine tiles are already loaded where you are heading, even if the main camera looks sideways. |
| Haze starts at 2.5 % of far (500 m), ends at 35 % | `GameRoot.ApplyTimeOfDay` | Distant LOD changes dissolve into the sky; adds depth. |
| Anisotropic filtering forced (16x desktop, 8x mobile) | `GameRoot.Awake` | Ground at grazing angles no longer smears. Needs mipmapped tile textures. |
| Camera near plane 0.3 -> 0.5 m | `GameRoot.BuildCamera` | Depth precision with a 20 km far plane (less z-fighting between LODs). |
| AutoQuality order: scale, view distance, SSE last | `AutoQuality` | Changing SSE re-requests almost every tile (waves of blur). AutoQuality is locked off in `GameSettings` today. |

Not done: freezing tile LOD under the car's colliders (the collider follows the visible LOD, so a refine can nudge the car); cache ceiling unchanged
(WebGL heap risk). Costs: more tiles and memory than before, so watch `tile cache` / `heap` in the HUD.

## Helicopter camera
Third driving camera (`HeliCamera.cs`, numbers in `Sim/HeliCamPolicy.cs`): hovers ~38 m above and ~14 m behind the car and looks down ~70 degrees along its
heading, climbing and pulling back with speed (up to 60 m / 26 m). Heading follow is slower than the chase camera. Wheel / pinch / +- zoom (0.5x-2.5x). A building
in the way makes it climb rather than pull in; it stays 4 m above the roofs/terrain under it. `C` cycles chase -> helicopter -> orbit; the choice is saved
(`?cam=heli|chase|orbit` overrides). Not verified in Unity: the feel (follow rates, heights) is tuned from a reference screenshot, not from a run.

## Cinematic effects
`CinematicFx` builds a global URP Volume in code (no assets) with two effects, each with its own slider in the settings panel (0 % = off and not rendered at all, 100 % = designed look, up to 200 %; saved; `?dof=`, `?mblur=` take 0-2, `?cine=0` turns all off). Defaults: depth of field 200 % and motion blur 75 % on every device.

| Effect | How | Tuning (`Sim/CinematicPolicy.cs`) |
|---|---|---|
| Depth of field | Gaussian (far blur only): sharp up to ~120 m (3.5x camera-to-car distance), soft by +700 m; the slider scales the blur radius | `DofStart`, `DofEnd`, `DofMaxRadius` |
| Motion blur | URP camera motion blur, 0 below 12 m/s up to ~0.65 at top speed; zeroed for 0.35 s after teleports / camera swaps | `MotionBlurIntensity` |

Removed after testing on a phone: the custom edge blur (blacked out the 3D view) and the colour grade. `ProjectSetup` removes the old edge-blur feature and material from the renderer asset.
URP skips the whole post-processing stack when the renderer asset has no post-processing data, so `ProjectSetup.EnsurePostProcessing` assigns it. Re-run *RocketWorld > Ensure project setup* (it runs on Editor load) and **rebuild**.
The settings panel shows a status line (`camera post-processing`, `volume in stack: depth of field ...`) that tells whether the stack is really running.
**Not run in Unity**: the Volume parameter names (URP 17.0.4) and the look itself are unverified. Cost: depth of field + motion blur at render scale 2 are heavy; lower the sliders or the render scale if FPS drops.
If effects are missing in a WebGL build, check URP Global Settings > "Strip Unused Post Processing Variants" is off (the volume is created at runtime, so Unity cannot see it).


## Anti crash

Settings toggle, on by default (`?anticrash=0` turns it off). Every page load starts with render scale (0.3), depth of field and motion blur at their minimum, then `AntiCrash` (logic in `AntiCrashLogic`) raises the level one step at a time while the frame rate stays high (~48+ FPS) and drops it two steps at once when it falls below ~33 FPS. Order of recovery: render scale first, then depth of field, then motion blur. The player's own sliders are the maximum, and nothing else is touched. A drop soon after a rise caps the level just below it, and the cap relaxes every 30 s. The controller waits while the world is loading and pauses during the benchmark.

The same toggle also protects the *load*. iOS Safari kills the tab ("A problem repeatedly occurred") when the 512 MB / 2 GB shared wasm heap is reserved, which no runtime setting can change, so CI builds a second player with `-rwLite 1` (initial 256 MB, max 1 GB, data caching off; threads stay on because Cesium needs them) into `/lite/`. `index.html` runs first and redirects there when Anti crash is on and the device is an iPhone/iPad, or when the last two loads on any device never reached the first frame (`RW_HideLoading` clears the counter). `localStorage` (`rw_anticrash`, `rw_boot`, `rw_lite`) is the shared source of truth because the two builds have separate PlayerPrefs. Overrides: `?lite=1` / `?lite=0` force a build, `?anticrash=0` disables the redirect. Turning the toggle off in settings and reloading goes back to the main build.
