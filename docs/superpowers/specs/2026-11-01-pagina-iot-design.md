# Página IoT del Stream Deck — diseño

Fecha: 2026-11-01 · Estado: borrador para aprobar

## Intención

Una página entera del StreamDock dedicada a la casa: luces WiZ, dos enchufes Tuya, el
purificador y la aspiradora Isaura. Hoy todo eso se maneja desde el celular (app WiZ y
SmartLife) o con `E:\Repositorios\Personal\_datos\iot\wiz.py`. El objetivo es tenerlo a
mano en el escritorio, con estado en vivo y sin depender de la nube.

El uso real del user gira alrededor de dos estados de luz: el **ritmo circadiano** de WiZ
(automatización que atenúa y cambia el tono según la hora; es el default todo el día) y
**"ir a dormir"**. Todo lo demás es ajuste puntual.

## Metas

- Volver al ritmo y pasar a "ir a dormir" con un toque.
- Elegir qué luz/grupo se toca y cambiarle color o brillo sin sacarlo de la curva cuando
  no hace falta.
- Prender/apagar enchufes y purificador, y lanzar/frenar a Isaura, viendo su estado.
- Todo por red local, desde el plugin en C#. Sin Python, sin nube en tiempo de uso.

## No-metas

- Escenas animadas de WiZ (fogata, océano, fiesta…): la paleta es sólo de colores fijos.
- Configuración de uso esporádico: ionizador, UV, temporizador y bloqueo del purificador;
  agua, volumen, no molestar, zonas y puntos de Isaura. Quedan en la app.
- Editar la curva del ritmo. La curva vive en la automatización de la app WiZ.

## Anti-metas (sería un fracaso aunque "funcione")

- Que una perilla o tecla tarde perceptiblemente (lanzar un proceso por cada clic de perilla).
- Que las claves locales de Tuya o el Access Secret terminen en el repo.
- Que girar una perilla por accidente deje una sola lámpara desfasada sin que se note.
- Mandar comandos al purificador o a Isaura durante el desarrollo de noche (hacen ruido).
- Hacks de sistema operativo en vez de acciones nativas del plugin (preferencia del user).

## Hechos verificados en vivo (2026-11-01)

WiZ, UDP 38899, JSON `{"method":…, "params":{…}}`:

- `getPilot` devuelve `state`, `sceneId`, `temp`, `dimming`, `r/g/b` y, si la lámpara tiene
  la automatización del ritmo cargada, `schdPsetId` (hoy `5` en las 7).
- `setPilot {"dimming":30}` cambia el brillo y **no** borra `schdPsetId`; la lámpara queda
  desfasada de la curva (no se corrigió sola en ~10 min).
- `setPilot {"sceneId":10}` = "ir a dormir" de la app. **Borra** `schdPsetId`.
- `setPilot {"state":true}` sin otros parámetros **vuelve al ritmo** en ambos casos: aplica el
  valor actual de la curva y recupera `schdPsetId`. (pywizlight documenta lo mismo como
  "PilotBuilder vacío".)
- `sceneId 1000` y `{"schdPsetId":5}` son rechazados por la lámpara.
- `getModelConfig`: rango de temperatura 2200–6500 K.

Indicador "en ritmo" de una lámpara: tiene `schdPsetId` y `sceneId == 0`. Ojo: no detecta el
desfase por brillo manual; por eso la UI marca aparte lo tocado a mano (ver Selector).

Tuya (protocolo local 3.3, AES-128-ECB con la local key, puerto 6668):

- Purificador (`kj`): DP1 switch · DP2 pm25 · DP3 mode `manual|auto|sleep` ·
  DP4 fan_speed_enum `sleep|low|mid|high|auto` · DP5 filtro % · DP12 temp · DP13 humedad ·
  DP6 anion · DP7 lock · DP9 uv · DP11 filter_reset · DP18 countdown.
- Isaura (`sd`): DP1 power_go · DP2 pause · DP3 switch_charge · DP4 mode ·
  DP5 status `standby|cleaning|paused|goto_charge|charging|charge_done|sleep|…` ·
  DP8 batería % · DP28 fault (bitmap) · DP17/19/21/23 horas de cepillos/filtro/trapo.
- Enchufes `noche` y `vestidor` (`cz`): DP1 switch.

Fuente de especificaciones: `tinytuya.Cloud.getdps()` (lectura, no toca el equipo).

## La página

```
Perillas:  [1 Selector] [2 Paleta] [3 Brillo] [4 Página siguiente]
           (la 4ª es la que el user ya tiene en todas las páginas para pasar de página;
            no se toca. La página es 100 % IoT: las 8 teclas y las perillas 1–3.)

┌────────┬────────┬────────┬────────┐
│ Ritmo  │A dormir│ Noche  │Vestidor│
├────────┼────────┼────────┼────────┤
│ Isaura │Purific.│Todo off│Cálido/ │
│        │        │        │ Frío   │
└────────┴────────┴────────┴────────┘
```

### "Lo elegido"

Estado compartido entre acciones, en memoria del plugin. Valores en orden de giro:
`Todas → PC → Cama → pc.norte → pc.este → pc.sur → cama.sur → cama.norte → cama.este → cama.oeste`
(circular). Arranca en **Todas**. Si pasan ~10 s sin tocar ninguna perilla, vuelve a Todas.

### Perillas

| Perilla | Girar | Apretar | Pantalla |
|---|---|---|---|
| 1 Selector | cambia lo elegido; al caer en una lámpara suelta, ésta hace un parpadeo corto para identificarla | prende/apaga lo elegido (apagar = `state:false`; prender = `state:true`, o sea vuelve al ritmo) | nombre de lo elegido + estado (ON/OFF, "ritmo" / "manual") |
| 2 Paleta | recorre la paleta; sólo cambia la vista previa, no las luces | aplica el color a lo elegido | muestra del color |
| 3 Brillo | ±paso de brillo sobre lo elegido (10–100 %) | vuelve al ritmo lo elegido | % actual |

Paleta por defecto: rojo, naranja, amarillo, verde, celeste, azul, violeta, rosa
(mismos RGB que `wiz.py`). Configurable en el menú de la acción.

"Manual": el plugin recuerda qué lámparas tocó con brillo/color/temperatura desde el último
"volver al ritmo". La pantalla del Selector lo muestra; la tecla Ritmo lo considera.

### Teclas

| Tecla | Toque | Muestra |
|---|---|---|
| Ritmo | `state:true` a lo elegido (en Todas = las 7) | marcada si las 7 están en ritmo y ninguna en "manual" |
| A dormir | `sceneId:10` a las 7, siempre | marcada si las 7 están en escena 10 |
| Noche / Vestidor | alterna el enchufe | ON/OFF |
| Isaura | según estado: en base → limpiar (DP1) · limpiando → pausa (DP2) · en pausa → volver a base (DP3) | estado + batería; alerta si DP28 ≠ 0 |
| Purificador | toque: prende/apaga (DP1) · toque largo: velocidad siguiente (DP4, `sleep→low→mid→high→auto`) | velocidad + % de filtro; aviso si filtro ≤ 15 % |
| Todo off | apaga las 7 luces, ambos enchufes y el purificador. No toca a Isaura | — |
| Cálido/Frío | a lo elegido, cicla 2700 K → 4000 K → 6500 K | K actual |

Todos los textos de las teclas en una sola tinta (preferencia del user: nada de jerarquía
apagado/brillante).

## Cómo se configura en StreamDock

Opción elegida: **acciones nuevas dentro del plugin Deck Tools** existente
(`plugin/com.knupwns.audioknob.sdPlugin`), una por función. El user arma la página
arrastrándolas.

| Acción (UUID sufijo) | Controller | Menú (property inspector) |
|---|---|---|
| `iot.selector` | Knob | — |
| `iot.palette` | Knob | lista de colores |
| `iot.brightness` | Knob | paso por clic (default 5 %) |
| `iot.lights` | Keypad | función: Ritmo · A dormir · Cálido/Frío · Todo off |
| `iot.plug` | Keypad | enchufe: Noche · Vestidor |
| `iot.purifier` | Keypad | — |
| `iot.vacuum` | Keypad | — |

Datos de equipos: el plugin lee `luces.json` y `tuya-devices.json` de
`E:\Repositorios\Personal\_datos\iot` (ruta configurable). Nada de eso se copia al repo. Si
una WiZ no responde, se redescubre por broadcast por MAC y se reescribe `luces.json`, igual
que `wiz.py` (compatibilidad de formato obligatoria: `wiz.py` sigue en uso).

UUIDs completos: `com.knupwns.audioknob.iot.selector`, `…iot.palette`, etc. Cada acción
nueva lleva su HTML de menú siguiendo el patrón de `brightness.html` + `pi.js`.

Formato de los archivos (ya existen, no hace falta aprovisionar nada):
- `luces.json`: `{"broadcast", "grupos": {grupo: {luz: {mac, ip}}}, "tuya": {alias: {nombre, mac, ip, tipo}}}`.
- `tuya-devices.json`: lista de `{name, id, key, …}`; se cruza con `luces.json` por `name == nombre`.
  La `key` es la local key de Tuya (versión de protocolo 3.3).
- El redescubrimiento WiZ se porta a C# (broadcast de `getSystemConfig`, matcheo por MAC);
  `wiz.py` es la referencia de comportamiento, no se lo llama.

Estado en vivo: sondeo cada ~3 s sólo mientras haya alguna acción IoT visible.

## Restricciones

- C# dentro del host actual (`src/PluginHost.cs` y compañía). Patrón de archivos:
  `src/ActionsIot.cs` + núcleo de protocolo (`src/Wiz.cs`, `src/Tuya.cs` o similar).
- El host hoy no maneja `keyUp`: hay que agregarlo para el toque largo del Purificador.
- Tuya 3.3 portado a C#, sin dependencias externas ni Python.
- Comandos de prueba a purificador e Isaura sólo de día y avisando al user.

## Decisiones que quedan al builder

- Estructura interna de clases, nombres, y cómo se modela "lo elegido".
- Umbral de toque largo (sugerido ~500 ms).
- Diseño gráfico de cada tecla/pantalla dentro de las convenciones de `KeyImage.cs`.
- Íconos.

## Decisiones tomadas por Claude (a revisar por el user)

- Selector vuelve a "Todas" a los ~10 s.
- Prender desde el Selector = volver al ritmo.
- Apretar la perilla de Brillo = volver al ritmo.
- Cálido/Frío con 3 pasos (2700/4000/6500 K).
- Todo off no incluye a Isaura.
- Aviso de filtro del purificador en ≤ 15 %.
