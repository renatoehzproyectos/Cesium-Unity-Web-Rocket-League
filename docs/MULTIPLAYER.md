# Multijugador (Netcode for GameObjects + WebRTC / PeerJS)

Modelo conservado de la versión web: **host autoritativo, el cliente envía inputs, el host envía el estado**. Solo cambia la tecnología.

| Web | Unity (este proyecto) |
|---|---|
| PeerJS + código de 5 dígitos | PeerJS + código de 6 letras, como transporte propio de Netcode (`Assets/Net/PeerTransport.cs` + `Assets/Plugins/WebGL/RocketWorldPeer.jslib`). Navegador a navegador: el anfitrión es el servidor |
| Mensajes JSON | Netcode for GameObjects: RPCs + NetworkVariables |
| `controls` cada 20 ms | `InputRpc` no fiable cada 20 ms con número de secuencia |
| `state` cada 20 ms | `SnapshotRpc` / `BallRpc` no fiables a 30 Hz; interpolación (100 ms) en clientes; coche propio con extrapolación + suavizado |
| `hello`, `math-status` | `NetworkVariable`: nombre, puntaje, vivo (+ color y slot); RPC fiables: nombre, rescate, pedir pelota |
| Traspaso por pestaña oculta | No hace falta (`runInBackground` activo durante la partida) |

## Flujo
- **Crear partida** → *Crear y entrar* → el anfitrión juega y el **código aparece arriba, en el centro de la pantalla** (con *Copiar* y *Salir*).
- **Unirse a partida** → escribir el código → *Unirse* → el código se muestra igual arriba.
- **Jugar solo** conserva el juego original. Máximo **4 jugadores: 1 anfitrión + 3 visitantes**.
- Al salir (o si el anfitrión cierra) se vuelve a jugar solo; el botón **Online** reabre el menú.

## Puesta en marcha (una vez)
1. Abrir el proyecto en Unity **6000.0.62f1** (el Package Manager instala NGO 2.4.4; ya está en `Packages/manifest.json`). **Ya no se usan Unity Services, Relay, Lobby ni ID de proyecto en la nube.**
2. El menú `RocketWorld > Ensure multiplayer setup` (se ejecuta solo al abrir) crea `Assets/Resources/Net/PlayerNet.prefab`. Hay que hacer *commit* de ese prefab y de los `.meta`.
3. El multijugador solo funciona en la build WebGL (necesita el navegador). La plantilla `Assets/WebGLTemplates/RocketWorld` carga `TemplateData/peerjs.min.js` (PeerJS 1.5.4, incluido en el repo).
4. Para probar: dos pestañas o dos móviles con la misma URL.

## Por qué WebRTC y no Relay
Un build WebGL no puede ser anfitrión por Relay: el anfitrión queda «activo» pero nadie logra unirse (mismo comportamiento que el reporte unity netcode #4111). WebRTC permite que un navegador sea el servidor. PeerJS usa su servidor de señalización gratuito (0.peerjs.com) y sus STUN/TURN públicos; si una red móvil bloquea el P2P y el TURN gratuito no basta, la conexión falla con un mensaje de tiempo agotado.

## Cómo funciona
- `Assets/Net` (ensamblado por defecto, así no depende de nombres de asmdef de los paquetes): `NetSession` (crear/unirse, NetworkManager creado en runtime), `PeerTransport` (transporte de Netcode sobre WebRTC: canal ordenado para lo fiable, canal sin orden y sin reintentos para lo no fiable), `PlayerNet` (objeto de jugador: RPCs, NetworkVariables, snapshots), `NetUi` (menú, código, marcador, nombres), `PoseSnap` (formato y buffer de interpolación), `Editor/NetPrefabSetup`.
- `Assets/Scripts/Runtime` sigue sin conocer la red: `CarSimLoop` (un único bucle de 120 Hz y **un solo** `Physics.Simulate` para todos los coches y la pelota), `RocketCar` con modo *marioneta* (`simulated=false`), `RocketBall` multi-coche, `GeoSync`, `INetRole`.
- El anfitrión simula **todos** los coches con el mismo modelo RocketSim/PhysX; los clientes no simulan, solo muestran. Las posiciones viajan en **ECEF (doubles)** + vectores adelante/arriba/velocidad ECEF, porque cada máquina tiene su propio origen Cesium.
- El anfitrión carga tiles, colliders y cámaras de streaming también alrededor de los coches remotos (`TrackRemote`).
- Puntaje = golpes reales a la pelota; vivo = coche en juego (no reapareciendo). Se cambian en `RocketBall.BallHit` / `PlayerNet.ServerTick`.
- Pelota: una compartida; **B** o el botón *Ball* la coloca delante de quien lo pida (el cliente lo pide por RPC).

## Pendiente / límites (sin verificar en Unity)
Nada de esto se ha compilado ni ejecutado en Unity (el entorno de trabajo no lo tiene). Esperar una iteración de arreglos del primer compilado. Puntos a vigilar:
- Nombres exactos de la API de NGO 2.4 (`NetworkTransport`, `[Rpc]`, `RpcParams`) y el acceso a `HEAPU8`/`HEAP32` desde el `.jslib` en Unity 6.
- Conexión P2P: depende de la red (NAT simétrico en datos móviles). Sin TURN propio puede fallar en algunas redes.
- Sin lista de partidas públicas: solo código.
- Sin predicción real del coche propio (extrapolación ~40 ms + suavizado): con latencia alta se nota.
- Los cambios de ciudad / calidad (TeleportTo, SSE) durante una partida no están sincronizados.
