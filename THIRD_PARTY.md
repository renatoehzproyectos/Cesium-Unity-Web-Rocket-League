# Third-party

- **RocketSim** (ZealanL/RocketSim) - MIT. `Assets/Scripts/Sim` is a C# port of its car model (Car.cpp, btVehicleRL.cpp, RLConst.h, CarConfig);
  the MIT notice is in `Assets/Scripts/Sim/LICENSE-RocketSim.txt`. The parity tool builds RocketSim from source at the commit pinned in
  `.github/workflows/ci.yml` and links Bullet Physics 3.24 (zlib) that ships inside it. Nothing from RocketSim is bundled in the Unity build except the ported logic.

- **Cesium for Unity / cesium-native** - Apache-2.0. Installed from the Cesium scoped registry (`Packages/manifest.json`).
- **Google Photorealistic 3D Tiles** - subject to the Google Maps Platform Terms of Service. Attribution/logo must stay visible
  (`showCreditsOnScreen = true`). You need your own API key with *Map Tiles API* enabled; never commit it.
- **Fennec model** (`Assets/Resources/Cars/fennec.glb`) - carried over from the RocketWorld project; check its original license before publishing.
- **Unity glTFast** - Apache-2.0 (imports the .glb in the Editor).

- **Barlow Semi Condensed** (The Barlow Project Authors) - SIL Open Font License 1.1, `Assets/Resources/Fonts` (license: `OFL.txt` there). Used for the in-game UI.

- **YC-130 (Prototype of C-130)** by KOG_THORNS (https://sketchfab.com/ioai25312) - CC BY 4.0, https://sketchfab.com/3d-models/yc-130prototype-of-c-130-11d237757851434ab5cb853db80376bb. Modified: the closed rear ramp was split into its own node so it can be animated, textures downscaled to 1024 px. `Assets/Resources/Models/YC130.glb`.

- **Ball model, ball texture, touch spark sprite** (`Assets/Resources/Ball`) come from the RocketPrismas web project this game is based on (ball.glb, ball_d, spark). The ball physics follow RocketSim (MIT, see above); the car-ball hit impulse and curve were read from RocketSim 2.2.1 and validated against its compiled build.

- **OpenFreeMap** vector tiles (https://openfreemap.org, no key) with **OpenStreetMap** data (c) OpenStreetMap contributors, ODbL, OpenMapTiles schema. Used at runtime for water polygons; attribution is shown in the HUD.
