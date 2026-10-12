# RocketWorld (Unity) - Cesium for Unity in the browser

A Unity 6 **Web** project: Cesium for Unity streams Google Photorealistic 3D Tiles and a Fennec (RocketWorld's car, same scale and
look) sits in the world with an orbit camera. Because the web build *is* the Unity build, it looks the same as any Unity project
with these settings.

**Status: ~75 % of the current scope (see `docs/ROADMAP.md`).** Render-only: no physics, collision, car movement or height
sampling (removed on request; the 50 % zip still has the RocketSim version).

> **Honest status.** Nothing here has been compiled or run: my sandbox has no Unity or Vercel. Verified: `vercel.json`, the
> manifest and the workflow parse, and C# brace balance. Expect a first-build fix iteration; send me the log.

## Using it
Drag to orbit, wheel / pinch / +- to zoom, arrows to rotate, Esc or the gear for settings, the link button copies a URL that
reproduces your view (never includes your API key). Settings: tile provider + key, location presets, ground height (adjust until the
wheels touch the street), car heading and colour, time of day, tile detail, view distance, render scale, lighting/sky style.
URL params: `?key= ?ion= ?lat= ?lon= ?h= ?hdg= ?t= ?car= ?yaw= ?pitch= ?dist= ?sse= ?scale=`.

## Deploy: GitHub -> Vercel
1. Push this folder to `main` of a new GitHub repo.
2. **Unity license for CI** (Settings -> Secrets -> Actions). Personal license via game-ci: follow https://game.ci/docs/github/activation,
   then add secrets `UNITY_LICENSE`, `UNITY_EMAIL`, `UNITY_PASSWORD` (or `UNITY_SERIAL` for Pro/Plus). Without them CI runs only the static checks.
3. Push -> Actions builds the WebGL player with Unity 6000.0.62f1 and publishes it to branch **`release`**.
4. Vercel -> *Add New Project* -> import the repo -> **Production Branch = `release`**, Framework *Other*, no build command, output `.`.
   `vercel.json` (in `release`) sets COOP/COEP (required for Cesium's multithreaded WASM) and Brotli headers; the page shows a red banner if they are missing.
5. Open the site -> gear -> paste your Google Maps Platform key (Map Tiles API enabled; restrict it by HTTP referrer to your Vercel domain).

## Local development
Unity **6000.0.62f1** (Web module). First open creates the URP asset, materials and boot scene (`RocketWorld/Ensure project setup`).
Build with **RocketWorld -> Build WebGL**; serve the output with COOP/COEP headers (e.g. `vercel dev`). Commit the generated `.meta` files after the first open.

## Layout
```
Assets/Scripts/Runtime   GameRoot (builds everything), OrbitCamera, CarVisual, DebugHud, GameSettings, WebBridge
Assets/Scripts/Editor    ProjectSetup (URP asset/scene/materials from code), BuildScript (WebGL settings, CI entry, index.html check)
Assets/Plugins/WebGL     jslib (query params, prompt, base URL)
deploy/vercel.json       headers for the hosted build
docs/                    ROADMAP
```
