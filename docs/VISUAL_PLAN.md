# Visual plan (target: 9.9/10 look, >= 30 FPS on 12 GB phones)

Rules: plane model/geometry untouched; Cesium + Google Photorealistic 3D Tiles stay. Every step must be free or cheaper on the GPU.

## Done in this pass (none compiled/run in Unity: needs a build)
- HUD: flight cluster without boxes (IAS | speed | ALT, VS colour-coded, throttle hairline scale with ticks + %). `DebugHud.Gauge/LeverBar`.
- Touch pads: quieter glass (`UiTheme.Touch`), thinner minimap rim, navy minimap backdrop instead of black.
- OS cursor hidden while touch controls are active.
- Default depth of field 200 % -> 100 %: far-blur on the ground read as "blurry terrain" and costs GPU. (Saved user values are kept.)

## Next (ordered by value / cost)
1. Atmosphere: sun-side haze tint + altitude-aware fog (zero cost, `GameRoot.ApplyTimeOfDay`).
2. Sky: cheap cloud layer (one scrolling quad/dome, no volumetrics).
3. Camera: speed FOV kick + slight look-ahead offset, plane lower in frame (`ChaseCamera`).
4. Water: tint/brightness lift for OSM water polygons (`WaterMap`).
5. Plane presentation (no model change): fake blob shadow projected on the ground, prop disc blur, light colour matched to the sun.
6. HUD: pitch ladder / heading tape, minimap integrated with the HUD, shared radius/alpha tokens in `UiTheme`.

## FPS guard
Measure with `?bench=1` on a 12 GB device before and after each step; AntiCrash keeps its 33/48 FPS thresholds.
