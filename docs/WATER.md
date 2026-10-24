# Water: drive on seas, lakes and rivers as a boat

Over water the car becomes a boat (floats, thrust + rudder, no wheels). On land it is the normal RocketSim car. The switch is automatic.

## Data source (free, no API key)
**OpenFreeMap** vector tiles (OpenStreetMap data, OpenMapTiles schema): `https://tiles.openfreemap.org/planet` (TileJSON, tile URLs are versioned weekly).
No registration, no key, no request limit, CORS-enabled (works inside the WebGL build with COOP/COEP). z14 tiles, ~0.5 m per tile unit; real accuracy is OSM's.
`WaterMap` streams the 3x3 tiles around each car (plus a look-ahead), decodes them with a small built-in MVT reader and answers *is this point water?*
from the real polygons (oceans, seas, lakes, river banks; islands are holes). Tunnels and swimming pools are ignored. Bridges and piers
(`transportation` lines) are solid: a car on one never becomes a boat. Disable everything with `?water=0`.

## How it floats without "concrete water"
The Google tile mesh at the sea is the water *surface* with a collider. In boat mode the car never rests on it: six hull points get a progressive
spring + damper (Archimedes), the hull draft is 0.30 m, and the hitbox hovers ~0.33 m above the surface, so PhysX contacts still work for piers,
banks, pillars and buildings but never for the water mesh. The surface height is read from the mesh under the car, so no datum maths is needed.
Boat forces (thrust, boost, reverse, rudder, keel drag, bank, self-righting, small swell) replace the wheel model only while afloat (`WaterDriver`).

| | |
|---|---|
| top speed | 22 m/s (79 km/h), boosted 30 m/s (109 km/h), reverse ~7.5 m/s |
| full lock at speed | ~15 m radius, leans into the turn; handbrake stops from 22 m/s in ~7 m |
| capsize | rights itself |

## Verified here / not verified
Verified (`bash tools/water/run.sh`, also a CI job): MVT decoding (islands, tunnels, bridges), geodesy, tile maths vs an independent reference,
and a 6-DOF simulation of the boat model with the Octane inertia (rest height, drops, speeds, turns, capsize, 90 s of random controls).
**Not verified:** anything that needs Unity (compile, PhysX contacts, WebGL, live OpenFreeMap download, Cesium API calls). Expect a first-build fix pass.

## Known limits
- OSM shorelines and the Google mesh can differ by a few metres: the mode switches at the OSM edge.
- Streams narrower than OSM river polygons (line-only waterways) are not water.
- Drops above ~10 m can graze the water mesh; PhysX contact takes over for that instant.
- No wake/splash particles or water audio yet; the ball is still solid on water.
- Remote clients see a boat as a slightly raised car model (sink is faked from a snapshot flag).
