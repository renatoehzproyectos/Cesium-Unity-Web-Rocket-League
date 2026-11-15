# Global road system

Independent, smooth road colliders under the Google Photorealistic 3D Tiles. The tiles stay the only visuals; roads are invisible pavement that the car
drives on (`?roads=debug` draws them). Photogrammetry still provides buildings, walls, kerbs, overpasses and relief.

## Flow
1. **Data (offline, once per Overture release):** `tools/roads/build_tiles.py` reads Overture `transportation/segment` GeoParquet (DuckDB), keeps `subtype=road`,
   maps class / surface / `road_flags` (bridge, tunnel, link) / `width_rules` / `level_rules`, hashes GERS ids (segments and connectors), and writes **RWR1 tiles at z14**
   plus `manifest.json` (`release`, attribution). Upload `roads-out/` to the static host as `/roads/` (Vercel config included: immutable tiles, revalidated manifest).
   Do not point the game at Overture's own PMTiles: Overture states they are for inspection only and some releases were published late or missing.
2. **Runtime download (`RoadWorld`):** manifest -> tiles within 1.2-3 km of the car (grows with speed, shrinks on phones / under memory pressure), bots get 600 m,
   saved zones are fetched ahead and pinned. 404 = no roads (cached as empty). Offline: last manifest and last cached release are used.
3. **Persistent cache (`RoadStore`):** `persistentDataPath/roads` (IndexedDB on WebGL, flushed by `RW_FlushFS`). `raw_` tiles per release, `proc_` fitted profiles per chunk
   (release + `ProcVersion`), `nodes_all.bin` shared junction heights. LRU bound 160 MB desktop / 48 MB mobile; zone tiles never evicted. A new release downloads fresh tiles
   and invalidates the processed profiles; an older cached release is used only when the download fails.
4. **Geometry (`Sim/Roads`, engine-free):** z15 chunks. Centreline = centripetal Catmull-Rom through the source vertices (data positions kept, corners rounded),
   4 m stations. Height = robust fit of ray hits on the original tile colliders (median/MAD outlier rejection, then asymmetric least squares that follows the lower
   envelope, so trees, vehicles and roofs are ignored; gaps bridged by a minimum-curvature curve; grade limit by class). Bridges and tunnels ignore the deck/roof samples and
   interpolate between their pinned ends. Cross slope from left/right samples (max 8 %). Shared connectors are pinned to one stored height, so segments, chunks and sessions meet
   without steps. Width: `width_rules`, else lanes, else class default. Intersections get convex-hull patches (no gaps); tight bends clamp the inner edge (no folding).
   A chunk with no ground samples yet is not built from guesses: it waits (or uses the processed cache) and retries when more tile colliders exist. Cached profiles are checked
   against live ground (median > 1.2 m off = refit).
5. **Separation from Cesium colliders (`RoadCarver`, called by `TileColliderBudget`):** after MeshDespike, tile triangles that are surface-like, fully inside the road footprint
   and within -1.0 / +2.2 m of the road are removed from the **collider copy** (the road collider replaces them: nothing removed is uncovered). Walls, kerbs, buildings and
   overpasses stay; partly covered triangles are pressed down to the road height so no bump pokes through; tunnels never carve. When a road collider appears, changes or is removed,
   the affected tile colliders are re-cut in place (never destroyed first). Road colliders live on `PhysicsSetup.RoadLayer` (27) with the same RocketSim world friction/restitution.
6. **Integration:** `RocketCar.Road` (class, surface, width, bridge/tunnel under the car, 4 Hz), `RocketCar.roads` / `Boost()` while waiting for ground, `GameRoot` (created with the tileset,
   `ApplyMemoryLevel` shrinks radii), `DebugHud` line, **F9 or the "Save zone here" button in Settings saves a zone** (`SavedZones`, PlayerPrefs; the "Zone radius" slider, 0.3-4 km, sets its size) which is prefetched and pinned.

## Parameters
`?roads=0` off, `?roads=debug` draw colliders, `?roads=https://cdn/roads/` other host. No manifest reachable = system off, game unchanged.
Tuning: `RoadProfile.VerticalCurveLength/SurfaceBias`, `RoadWorld.CarveBelow/CarveAbove`, `RoadCachePolicy.*`.

## Official / national sources
`build_tiles.py --official official.json`: each entry has `bbox`, `geojson` (LineStrings), field mapping, `license`, `redistributable: true` (the tool refuses otherwise)
and replaces Overture inside its bbox; its attribution goes into the manifest. I did not bundle any national dataset: licences differ and must be checked per source.

## Licence
Overture transportation is derived largely from OpenStreetMap (ODbL): keep the attribution in the manifest visible (credits) and check
https://docs.overturemaps.org attribution/licensing for your release before publishing derived tiles. Processed tiles are a derived database.

## Verified / not verified
Verified (`bash tools/roads/run.sh`, 40 checks): tile format both ways (C# and Python), centreline, height fit under noise (trees +3.5 m, roofs +14 m, holes -> 8 cm error),
grade limit, mesh winding / junction gaps / bend folding, carving (no removed triangle outside the footprint, bumps pressed, overhead kept), shared-node continuity, processed cache,
LRU with pins, tile selection incl. antimeridian.
**Not verified (needs Unity, a real tileset and Overture data):** the Unity compile of `Runtime/Roads`, `TileColliderBudget`, `RocketCar`, `GameRoot` edits (only syntax-parsed);
`CesiumGlobeAnchor` placement of chunks and the ENU rotation; IndexedDB persistence via `FS.syncfs`; the DuckDB query against the real release (column names follow the published schema);
the feel of driving on the roads; frame cost of carving / cooking on phones. Expect a first-build fix iteration.

## Known limits
- Height comes from the tiles (no DEM): far from the car, roads are built only from the processed cache. A DEM prior (e.g. Terrarium + local geoid offset) would let roads be built beyond the collider ring.
- Overture OSM geometry is a centreline; very wide roads with medians, roundabouts and multi-level interchanges are approximated by ribbons and node patches.
- `lanes` is not exported yet (width comes from `width_rules` or class defaults); `level_rules` is stored but z-order is resolved by height only.
- Layer 27 is not named in TagManager; physics works by number.
