# Third-party

- **RocketSim** (ZealanL/RocketSim) - MIT. `Assets/Scripts/Sim` is a C# port of its car model (Car.cpp, btVehicleRL.cpp, RLConst.h, CarConfig);
  the MIT notice is in `Assets/Scripts/Sim/LICENSE-RocketSim.txt`. The parity tool builds RocketSim from source at the commit pinned in
  `.github/workflows/ci.yml` and links Bullet Physics 3.24 (zlib) that ships inside it. Nothing from RocketSim is bundled in the Unity build except the ported logic.

- **Cesium for Unity / cesium-native** - Apache-2.0. Installed from the Cesium scoped registry (`Packages/manifest.json`).
- **Google Photorealistic 3D Tiles** - subject to the Google Maps Platform Terms of Service. Attribution/logo must stay visible
  (`showCreditsOnScreen = true`). You need your own API key with *Map Tiles API* enabled; never commit it.
- **Fennec model** (`Assets/Resources/Cars/fennec.glb`) - carried over from the RocketWorld project; check its original license before publishing.
- **Unity glTFast** - Apache-2.0 (imports the .glb in the Editor).
