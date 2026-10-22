# Agua realista (máscara invisible de OSM)

`WaterMap` NO dibuja nada: descarga las teselas estándar de OpenStreetMap (z17, ~1 m/px), clasifica por color el agua (#aad3df),
limpia la máscara (cierre morfológico = rellena etiquetas; apertura = quita piscinas y arroyos finos) y responde `IsWater(lat, lon)`.
`RocketCar` consulta la máscara una vez por frame:
- **Entrar:** el auto está sobre agua y a < 2 m de la superficie que dan los tiles 3D → el collider pasa a trigger (atraviesa el "agua sólida") y
  `WaterTick` reemplaza al modelo RocketSim: resorte de flotación sobre la altura suavizada del agua, empuje/giro con los mismos controles
  (Boost = turbo de lancha), arrastre lateral de quilla y auto-enderezado.
- **Salir:** 0,3 s sobre tierra → collider normal, se sube el auto si quedó bajo el suelo y se devuelve el control a RocketSim (`OnStateSet`).
- Ajustes: `Agua realista` en el panel, o `?water=0|1`. Servidor de teselas propio: `?watertiles=https://host/{z}/{x}/{y}.png`.
- Constantes de afinación: bloque "water (boat mode)" en `RocketCar.cs`.
- Multijugador: cada cliente detecta el agua de su propio auto; los demás lo ven flotar porque su pose se replica igual.
