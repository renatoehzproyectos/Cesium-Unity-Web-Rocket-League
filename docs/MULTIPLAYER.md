# Multijugador (Netcode for GameObjects + Relay + Lobby)

Modelo conservado de la versión web: **host autoritativo, el cliente envía inputs, el host envía el estado**. Solo cambia la tecnología.

| Web | Unity (este proyecto) |
|---|---|
| PeerJS + código de 5 dígitos | Multiplayer Services (Sessions API = **Lobby** para el código y la lista pública + **Relay** para el tráfico) |
| Mensajes JSON | Netcode for GameObjects: RPCs + NetworkVariables |
| `controls` cada 20 ms | `InputRpc` no fiable cada 20 ms con número de secuencia |
| `state` cada 20 ms | `SnapshotRpc` / `BallRpc` no fiables a 30 Hz; interpolación (100 ms) en clientes; coche propio con extrapolación + suavizado |
| `hello`, `math-status` | `NetworkVariable`: nombre, puntaje, vivo (+ color y slot); RPC fiables: nombre, rescate, pedir pelota |
| Traspaso por pestaña oculta | No hace falta (`runInBackground` activo durante la partida) |

## Flujo
- **Crear partida** → *Crear y entrar* → el anfitrión juega y el **código aparece arriba, en el centro de la pantalla** (con *Copiar* y *Salir*).
- **Unirse a partida** → escribir el código (o elegir una partida pública de la lista) → *Unirse* → el código se muestra igual arriba.
- **Jugar solo** conserva el juego original. Máximo **4 jugadores: 1 anfitrión + 3 visitantes**.
- Al salir (o si el anfitrión cierra) se vuelve a jugar solo; el botón **Online** reabre el menú.

## Puesta en marcha (una vez)
1. Abrir el proyecto en Unity **6000.0.62f1** (el Package Manager instala NGO 2.4.4, Multiplayer Services 1.1.6 y Authentication 3.5.1; ya están en `Packages/manifest.json`).
2. `Edit > Project Settings > Services`: **enlazar un proyecto de Unity Cloud** y activar **Relay** y **Lobby** en el dashboard (Multiplayer Services).
3. El menú `RocketWorld > Ensure multiplayer setup` (se ejecuta solo al abrir) crea `Assets/Resources/Net/PlayerNet.prefab`. Hay que hacer *commit* de ese prefab y de los `.meta`.
4. Para probar en local: Multiplayer Play Mode, o dos pestañas/ventanas (cada carga de página usa una identidad anónima distinta).
5. WebGL: se usa **WebSockets seguros (wss)** vía Relay; nada más que configurar.

## Error `UnityProjectNotLinkedException` («Some services couldn't be initialized»)
Unity Services lee el ID del proyecto de Unity Cloud de `Application.cloudProjectId`, que en una build de CI/Vercel está vacío (el proyecto no está enlazado). El ID **se fija al compilar** (no es secreto: va dentro de toda build). Cualquiera de estas opciones:
- Crear en el repositorio el archivo `Assets/Resources/Net/cloud_project.txt` con el Project ID (una sola línea). Es lo más fácil desde el móvil (GitHub web > Add file).
- O el secreto de GitHub `UNITY_CLOUD_PROJECT_ID`, o `-rwCloudProject <id>` al compilar a mano.
`BuildScript` escribe el ID en los ajustes del jugador (`cloudProjectId`) antes de compilar. Después hay que volver a lanzar el workflow. Además hay que activar **Relay** y **Lobby** en ese proyecto (dashboard > Products).
El ID está en cloud.unity.com > Settings del proyecto (no es el ID del *entorno*).

## Cómo funciona
- `Assets/Net` (ensamblado por defecto, así no depende de nombres de asmdef de los paquetes): `NetSession` (servicios, sesión, NetworkManager creado en runtime), `PlayerNet` (objeto de jugador: RPCs, NetworkVariables, snapshots), `NetUi` (menú, código, marcador, nombres), `PoseSnap` (formato y buffer de interpolación), `Editor/NetPrefabSetup`.
- `Assets/Scripts/Runtime` sigue sin conocer la red: `CarSimLoop` (un único bucle de 120 Hz y **un solo** `Physics.Simulate` para todos los coches y la pelota), `RocketCar` con modo *marioneta* (`simulated=false`), `RocketBall` multi-coche, `GeoSync`, `INetRole`.
- El anfitrión simula **todos** los coches con el mismo modelo RocketSim/PhysX; los clientes no simulan, solo muestran. Las posiciones viajan en **ECEF (doubles)** + vectores adelante/arriba/velocidad ECEF, porque cada máquina tiene su propio origen Cesium.
- El anfitrión carga tiles, colliders y cámaras de streaming también alrededor de los coches remotos (`TrackRemote`).
- Puntaje = golpes reales a la pelota; vivo = coche en juego (no reapareciendo). Se cambian en `RocketBall.BallHit` / `PlayerNet.ServerTick`.
- Pelota: una compartida; **B** o el botón *Ball* la coloca delante de quien lo pida (el cliente lo pide por RPC).

## Pendiente / límites (sin verificar en Unity)
Nada de esto se ha compilado ni ejecutado en Unity (el entorno de trabajo no lo tiene). Esperar una iteración de arreglos del primer compilado. Puntos a vigilar:
- Nombres exactos de la API de Multiplayer Services 1.1.x (`SessionOptions`, `WithRelayNetwork`, `QuerySessionsAsync`) y de NGO 2.4 (`[Rpc]`, `RpcParams`).
- **Anfitrión desde WebGL**: el anfitrión es cliente del Relay (wss), así que debería funcionar, pero hay reportes de problemas en algunas versiones; si fallase, probar con el anfitrión en escritorio o subir versiones de paquetes.
- Sin predicción real del coche propio (extrapolación ~40 ms + suavizado): con latencia alta se nota.
- El código de Lobby solo lo conoce el anfitrión; quien entra desde la lista pública ve «—» salvo que el servicio lo devuelva.
- Los cambios de ciudad / calidad (TeleportTo, SSE) durante una partida no están sincronizados.
