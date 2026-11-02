# Aire acondicionado en la página IoT — diseño

Fecha: 2026-11-02. Extiende `2026-11-01-pagina-iot-design.md` (leerlo primero: selector, hub,
sondeo, tinta única, patrón de acciones).

## Por qué

El user sumó un aire acondicionado Tophouse que se maneja por un control IR Tuya
("Smart IR", UFO-R1). Ya anda desde `E:\Repositorios\Personal\_datos\iot\wiz.py aire …`
(`aire.py`). Quiere manejarlo desde el deck sin perder la página IoT de luces.

## Qué quiere el user (decidido por él)

1. **Selector (perilla 1):** suma una parada más, **Aire**, después de la última lámpara
   (`… → cama.oeste → Aire → Todas`). Con Aire elegido, **apretar prende/apaga el aire**
   (igual que hoy apretar prende/apaga lo elegido).
2. **Con Aire elegido, las perillas 2 y 3 cambian de función. Regla: girar elige, apretar
   aplica. Girar nunca manda nada al aire.**
   - Perilla 2 (hoy Paleta): girar recorre `Frío → Calor → Auto → Vent → Seco` (circular) sólo
     en pantalla; apretar aplica ese modo.
   - Perilla 3 (hoy Brillo): girar cambia la temperatura mostrada (16–30, paso 1, sin dar
     vuelta) sólo en pantalla; apretar aplica esa temperatura.
   - Lo girado y no aplicado se descarta cuando el selector vuelve a Todas (regla existente
     de ~10 s sin tocar perillas) o cuando cambia la selección.
3. **Con cualquier otra selección, las perillas 2 y 3 se comportan exactamente como hoy.**
   En particular el **brillo de las luces se aplica al girar** (no cambia).
4. **Tecla nueva "Aire"** en la página 1, en la posición donde hoy está "A dormir" (fila de
   arriba, 2ª). "A dormir" sale de la página 1; ya existe en la página 2. Toque = prende/apaga.
5. **"Todo off" no toca el aire** (sigue apagando sólo luces, enchufes y purificador).
6. Las 2 teclas libres del deck (tiene 10, la página usa 8) **quedan libres**. Swing y sleep
   **no** van al deck en esta etapa.

El armado de páginas lo hace el user arrastrando acciones en el software del deck; el
builder sólo entrega la acción nueva y los cambios de comportamiento.

## Cómo se manda al aire (hechos verificados 2026-11-02)

- El aire es un sub-equipo "biblioteca" de Tuya (código Gree, `remote_index` 2717). Estos se
  mandan **por la API cloud de Tuya**, no por red local (los códigos IR los arma Tuya).
- Ids: control IR `infrared_id = 7211305010521c04d0e8`; aire `remote_id = eb0ea7023baabc7728pl4z`.
  Están también en `tuya-devices.json` (aire: `category: infrared_ac`, `sub: true`,
  `gateway_id` = id del control). Leerlos de ahí, no hardcodear en el repo.
- Credenciales cloud: `_datos\iot\tuya.json` = `{apiKey, apiSecret, apiRegion}` (región `us`
  → `https://openapi.tuyaus.com`). **Secreto: nunca al repo, ni a logs, ni a tests.**
- Leer estado: `GET /v2.0/infrareds/{ir}/remotes/{ac}/ac/status` →
  `{"power":"1","mode":"0","temp":"24","wind":"0"}` (strings).
- Mandar estado completo: `POST /v2.0/infrareds/{ir}/air-conditioners/{ac}/scenes/command`
  body `{"power":1,"mode":0,"temp":24,"wind":0}` (ints). Respuesta `{"success":true,…}`.
- Modos Tuya: 0 Frío · 1 Calor · 2 Auto · 3 Vent · 4 Seco. Viento: 0 auto · 1 · 2 · 3.
- Referencia de comportamiento: `_datos\iot\aire.py` (`estado`, `mandar_estado`, `biblioteca`).
  Como en la página IoT, el plugin **no llama a Python**: se porta a C#.
- Firma Tuya OpenAPI: token con `GET /v1.0/token?grant_type=1`; cada request lleva
  `client_id`, `t` (ms), `sign_method: HMAC-SHA256`, `sign`, y `access_token` salvo el de token.
  `sign = HMAC-SHA256(secret, client_id + [access_token] + t + stringToSign).ToUpper()`,
  `stringToSign = METHOD + "\n" + SHA256(body) + "\n" + "" + "\n" + url_con_query`.
  Renovar el token al vencer (`expire_time`).

## Comportamiento

- **Aplicar modo o temperatura:** se lee el último estado conocido, se cambia sólo ese campo,
  `power = 1`, y se manda el estado completo (así funciona `aire.py` y el control original).
  Aplicar con el aire apagado lo prende.
- **Prender/apagar** (selector o tecla): estado conocido con `power` invertido. Sin estado
  conocido: leer primero; si la lectura falla, no mandar nada y mostrar "Sin señal".
- **Cada orden hace pitar el aire**: nunca mandar más de una orden por gesto del user.
- **Estado mostrado = el que Tuya tiene registrado.** Si alguien usa el control original,
  Tuya no se entera; el deck lo muestra desactualizado hasta la próxima orden. Aceptado.
- **Sondeo:** el estado del aire se lee al aparecer una acción que lo muestre y luego cada
  60 s mientras haya alguna visible (no cada 3 s: es una API cloud). Después de mandar una
  orden con éxito, el estado mostrado pasa a ser lo mandado, sin esperar el sondeo.
- **Errores:** si la API falla (red, token, `success:false`), la pantalla muestra
  "Sin señal" y no se reintenta solo. Ninguna excepción tumba el plugin.
- Todo el I/O cloud fuera del hilo de UI (mismo patrón que la cola Tuya del hub).

## Pantallas (tinta única, color del ícono = estado, como el resto de la página)

- Selector en Aire: título `Aire`, valor `Frío 24°` o `Apagado` o `Sin señal`; tinte On si
  `power = 1`.
- Perilla 2 en Aire: título `Modo`, valor = modo en preselección; marcar de algún modo
  visible si difiere del modo actual (a criterio del builder, sin agregar colores nuevos).
- Perilla 3 en Aire: título `Temp`, valor `24°` en preselección; ídem.
- Tecla Aire: título `Aire`, valor `Frío 24°` / `Apagado` / `Sin señal`.
- Si `°` no se ve bien con la tipografía del deck, usar `24 C`.

## Fuera de alcance

- Swing y sleep (están en `aire.py` por red local; no van al deck ahora).
- Velocidad de ventilador desde el deck.
- Teclas libres: no se usan.
- Cambios a `wiz.py` / `aire.py`.

## Decisiones de Claude (no confirmadas por el user)

- Posición de "Aire" en el giro del selector: después de la última lámpara.
- Toque largo de la tecla Aire: **no hace nada** (el user pidió sólo prender/apagar; se
  descartó "toque largo = modo siguiente" para no agregar algo no pedido).
- Sondeo cloud cada 60 s; preselección se descarta al volver a Todas.
- Puerto de la firma Tuya a C# en lugar de invocar `aire.py`, por la restricción "sin
  Python" del diseño IoT.

- **"Todas" sigue siendo sólo luces**: el aire no entra en "Todas" ni en sus acciones.
- **Teclas de luces con Aire elegido** (Ritmo, Cálido/Frío): no hacen nada (`Resolve("Aire")`
  devuelve lista vacía). "A dormir" no cambia (siempre va a las 7 luces).
- **Sondeo**: sólo mientras la tecla Aire esté visible o Aire sea la selección actual; al
  elegir Aire sin dato reciente, una lectura inmediata.
- **Preselección inicial** de perillas 2/3 al elegir Aire = último modo/temperatura conocidos.
  Sin estado conocido muestran "Sin señal" y apretar no manda nada (igual que el toggle).
- Girar perillas 2/3 en Aire llama `TouchSelection()` (reinicia los 10 s). Un sondeo que
  llega durante la preselección no pisa lo girado y no aplicado.
- **Ids del aire**: entrada de `tuya-devices.json` con `category == "infrared_ac"`; su `id` es
  el `remote_id` y su `gateway_id` es el `infrared_id` (verificado 2026-11-02). Lookup
  separado de `luces.json` (el aire no está ahí). Si falta, "Sin señal".

## Tests

- Offline (en `IotTests.exe`, sin red): firma Tuya con vector fijo inventado; armado del
  estado al aplicar modo/temp/toggle; preselección que se descarta al volver a Todas;
  perillas 2/3 sin cambios de comportamiento cuando la selección no es Aire.
- En vivo sólo con `--live` explícito: **lectura** del estado del aire. Mandar órdenes reales
  sólo avisando al user (hace pitar el aire).
