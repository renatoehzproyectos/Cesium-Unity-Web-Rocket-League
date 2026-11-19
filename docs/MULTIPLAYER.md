# Multijugador (Netcode for GameObjects + WebRTC / PeerJS)

**Cada jugador simula su propio coche** (sin retraso de input), el anfitrión solo retransmite y simula la pelota. Hasta **15 jugadores** (el creador elige 2-15 al crear la partida).

| Antes (host autoritativo) | Ahora |
|---|---|
| El cliente enviaba inputs y veía su coche tras ida y vuelta al host (RTT + extrapolación) | Cada jugador simula su coche localmente: respuesta inmediata, sin correcciones |
| El host simulaba N coches (PhysX a 120 Hz) y cargaba tiles + colliders + cámaras de streaming alrededor de cada uno | El host solo simula su coche y la pelota; cada navegador carga su propio terreno |
| N x (N-1) mensajes de 90 B por tick (15 jugadores: 6300 msg/s) | **1 datagrama por jugador y tick** con todos los coches (31 B cada uno): ~0,5 KB x 14 x 30 Hz |
| Instantánea ECEF con doubles + 3 vectores (~90 B) | `NetCodec`: 31 B (cm int32, velocidad int16, cuaternión «smallest three») |
| Retardo de interpolación fijo 100 ms | Adaptativo: 1,5 intervalos de lote + 3 x jitter medido (50-300 ms) |
| Misma frecuencia para todos los coches | Por distancia: < 600 m 30 Hz, < 4 km 10 Hz, más lejos 3 Hz; solo se envía lo que cambió; 20 Hz con más de 8 jugadores |

## Flujo
- **Crear partida** → *Crear y entrar* → el anfitrión juega y el **código aparece arriba, en el centro de la pantalla** (con *Salir*).
- **Unirse a partida** → escribir el código → *Unirse* → el código se muestra igual arriba.
- **Jugar solo** conserva el juego original. Al crear la partida se elige el máximo de **2 a 15 jugadores** (1 anfitrión + 1-14 visitantes); el cartel superior muestra `jugadores/máximo` y, en los visitantes, el ping (RTT).
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
- **Coches remotos = marionetas cinemáticas** en todas las máquinas (`RocketCar.SetProxyTarget` / `ProxyPreStep`, movidos en cada tick de `CarSimLoop`): tu coche y la pelota chocan con ellos con su velocidad real. Las posiciones viajan en **ECEF** (cada máquina tiene su propio origen Cesium).
- **Pelota**: la simula solo el anfitrión; los golpes de un visitante se aplican cuando el anfitrión ve su coche tocarla (latencia de un sentido + interpolación). Es el único elemento con retraso inevitable.
- **Autobús de inicio**: cada jugador vuela en su propio autobús (carril por *slot*); los demás lo ven por una entrada `BusBase + slot` del lote.
- Un visitante empieza donde está el anfitrión (el mundo se recentra en su primera posición, `NetSession.TryStartClient`).
- Los visitantes también pueden usar vehículos, rescate (R) y teletransporte: todo es local. El Tsar Bomba sigue reservado a solo / anfitrión (los visitantes ven la bola pequeña) y el anfitrión lo retransmite.
- Topología: **estrella** (anfitrión = servidor, WebRTC/PeerJS). Una malla completa quitaría un salto, pero cada par necesita un camino P2P directo (sin TURN propio muchos NAT lo impiden) y NGO es una estrella; la estrella + lote único mantiene un solo enlace crítico por jugador.
- Puntaje = golpes reales a la pelota; vivo = coche en juego (no reapareciendo). Se cambian en `RocketBall.BallHit` / `PlayerNet.ServerTick`.
- Pelota: una compartida; **B** o el botón *Ball* la coloca delante de quien lo pida (el visitante lo pide por RPC).

## Pendiente / límites (sin verificar en Unity)
Nada de esto se ha compilado ni ejecutado en Unity (el entorno de trabajo no lo tiene). Esperar una iteración de arreglos del primer compilado. Puntos a vigilar:
- Nombres exactos de la API de NGO 2.4 (`NetworkTransport`, `[Rpc]`, `RpcParams`) y el acceso a `HEAPU8`/`HEAP32` desde el `.jslib` en Unity 6.
- Conexión P2P: depende de la red (NAT simétrico en datos móviles). Sin TURN propio puede fallar en algunas redes.
- Sin lista de partidas públicas: solo código.
- Sin predicción real del coche propio (extrapolación ~40 ms + suavizado): con latencia alta se nota.
- Los cambios de ciudad / calidad (TeleportTo, SSE) durante una partida no están sincronizados.
