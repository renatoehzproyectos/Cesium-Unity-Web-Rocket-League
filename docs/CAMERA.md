# Camera menu

Open it with the **Camera** button (top right, next to the vehicle button) or **Camera menu** in the settings panel. Esc / OK / the red X close it.
It is translucent and the game keeps running, so every change can be judged live.

| Control | What it does |
|---|---|
| Chase / Helicopter / Orbit | camera mode (the **C** key still cycles them) |
| Horizontal Turn ± | turns the camera left / right, ±90° |
| Vertical Turn ± | tilts the camera up / down, ±60° |
| Up/Down ± | moves the camera up / down, ±40 m |
| Side position ± | moves the camera sideways, ±40 m |
| Field of View ± | adds / removes degrees on top of the mode's own FOV (clamped to 20–120°) |
| Front/Back ± | moves the camera towards (+) / away from (−) the vehicle, ±40 m |
| Follow roll too | **off** (default): the camera follows yaw and pitch only, horizon stays level. **on**: it follows yaw, pitch and roll of the vehicle (chase + helicopter modes) |
| Follow delay | multiplier of the camera lag. 1.00 = the original feel, 0 = rigidly attached, 3 = very lazy. Applies to the chase and helicopter modes |
| OK / Save / Last Save / Reset Defaults | close · store the current values in this browser · reload the stored values · zero every offset, delay 1.00, roll follow off |

A tap on ± moves one small step; holding repeats after 0.35 s. Saved values are loaded automatically at the next start.

## Code
* `Assets/Scripts/Sim/CamTunePolicy.cs` – limits, follow-delay scaling, button steps (engine-free, unit-tested in `CamTunePolicyTests`).
* `Assets/Scripts/Runtime/CamTuning.cs` – the working values + Save / Last Save / Reset (PlayerPrefs, key prefix `rw2.camtune.`), and the camera rig:
  `CamRigBegin` (order 40) undoes last frame's offset before the active mode runs, `CamRigEnd` (order 200) re-applies it, so the offsets never feed back into the
  chase / helicopter smoothing (they use the camera's own position as state).
* `Assets/Scripts/Runtime/CameraMenu.cs` – the panel.
* `ChaseCamera` / `HeliCamera` – follow delay (`CamTuning.Blend`) and the full-axis branch.
