# Discord nativo — deafen y screen share por RPC

**Fecha:** 2026-08-27
**Estado:** implementado (2026-08-28)
**Actions nuevas:** `com.knupwns.audioknob.discorddeafen`, `com.knupwns.audioknob.discordscreenshare`

## Problema

Las teclas de Discord del deck son del plugin de HotSpot
(`com.hotspot.streamdock.discord.sdPlugin`), no de DeckTools. Dejaron de
funcionar el 2026-08-25 y no se recuperan solas.

### Causa raíz, verificada en el log del plugin

`AuthService.maintain()` corre cada 30 s. Al vencer el token hace, en este orden:

```js
clearStreamKitToken();                        // 1. borra el token
this.authorize("streamkit").catch(()=>{})     // 2. reintenta, error tragado
```

El paso 2 llega a `streamKitFullAuthorize()`, que reusa el cliente RPC vivo vía
`ensureConnected()`. Discord rechaza un segundo `AUTHORIZE` sobre una conexión ya
autenticada:

```
[2026-08-25T09:48:58.485] [INFO]  AuthService: StreamKit token expired, re-authorizing
[2026-08-25T09:48:58.660] [ERROR] AuthService: authorization failed: Already authenticated
```

175 ms entre ambas líneas. El token quedó borrado y la renovación falló. Desde
ahí el camino de auto-recuperación está muerto, porque exige que el token
**exista**:

```js
if (S === "streamkit") {
  if (h.streamKitToken) await this.authorize("streamkit", {silent:true});
  else await this.preConnect();     // conecta, nunca autentica
  return }
```

Y `maintain()` corta antes con `if (!h) return`. Resultado: `code 4006 Not
authenticated or invalid scope` en cada pulsación, sin ninguna señal en la tecla.

Contraste: el 2026-08-18 el token venció **al arrancar** StreamDock, con conexión
fresca todavía no autenticada, y se renovó solo (`StreamKit token expired, full
re-authorization`). Que se recupere o no depende de si el vencimiento cae con el
proceso arriba o abajo.

### Por qué no alcanza con parchear

El flujo StreamKit usa la app de Discord `207646673902501888` contra el proxy
`streamkit.discord.com/overlay/token`, que devuelve **sólo `access_token`**. Sin
`refresh_token` no hay renovación sin intervención del cliente de Discord, y la
vida del token es de 7 días (`expires_in: 604800`). `grep refresh_token
plugin/index.js` da cero resultados.

## Objetivo

Dos teclas nativas en DeckTools, funcionalmente iguales a las que hay hoy, con
credenciales propias y renovación que no dependa de que Discord esté abierto.

## Protocolo — lo que hay que implementar

### Transporte IPC

Named pipe `\\.\pipe\discord-ipc-0` .. `-9`; se prueban en orden hasta que uno
abra. Frame:

```
opcode   int32 little-endian
length   int32 little-endian
payload  UTF-8 JSON, `length` bytes
```

Opcodes: `0 HANDSHAKE`, `1 FRAME`, `2 CLOSE`, `3 PING`, `4 PONG`.

Handshake (opcode 0): `{"v":1,"client_id":"<id>"}`. Discord responde opcode 1 con
`{"cmd":"DISPATCH","evt":"READY",...}`.

Comandos (opcode 1) llevan `nonce` GUID; la respuesta trae el mismo `nonce`. Los
errores vienen como `{"evt":"ERROR","data":{"code":<n>,"message":"..."}}` con el
nonce del pedido. Los eventos suscritos llegan sin nonce, como
`{"cmd":"DISPATCH","evt":"<EVENTO>","data":{...}}`.

### Comandos usados

| Qué | Frame |
|---|---|
| Autorizar | `{"cmd":"AUTHORIZE","args":{"client_id":"…","scopes":[…],"prompt":"none"}}` → `{"code":"…"}` |
| Autenticar | `{"cmd":"AUTHENTICATE","args":{"access_token":"…"}}` |
| Leer voz | `{"cmd":"GET_VOICE_SETTINGS","args":{}}` |
| Deafen | `{"cmd":"SET_VOICE_SETTINGS","args":{"deaf":true}}` |
| Screen share | `{"cmd":"TOGGLE_SCREENSHARE","args":{}}` |
| Suscribir | `{"cmd":"SUBSCRIBE","evt":"VOICE_SETTINGS_UPDATE","args":{}}` |

`SET_VOICE_SETTINGS` y `VOICE_SETTINGS_UPDATE` son API pública documentada.
**`TOGGLE_SCREENSHARE` y `SCREENSHARE_STATE_UPDATE` no lo son** — se tomaron del
bundle de HotSpot (`TOGGLE_SCREENSHARE(){return this.request("TOGGLE_SCREENSHARE")}`).
Ver Riesgos.

### Scopes

```
identify  rpc  rpc.voice.read  rpc.voice.write
rpc.screenshare.read  rpc.screenshare.write
```

`prompt:"none"` hace que `AUTHORIZE` devuelva el code sin diálogo, una vez que el
usuario aprobó la app la primera vez.

### OAuth

`POST https://discord.com/api/v10/oauth2/token`, form-urlencoded.

Alta: `grant_type=authorization_code`, `code`, `client_id`, `client_secret`,
`redirect_uri`. Renovación: `grant_type=refresh_token`, `refresh_token`,
`client_id`, `client_secret`. Ambas devuelven `access_token`, `refresh_token`,
`expires_in` (604800).

El `redirect_uri` tiene que coincidir con uno registrado en la app aunque el code
venga por RPC y nunca haya redirect real. Se registra `http://localhost`.

## Arquitectura

Cuatro unidades, cada una testeable sola.

### `src/DiscordRpc.cs` — transporte

No sabe de OAuth ni de teclas. Abre el pipe, hace handshake, serializa frames,
correlaciona respuestas por nonce y despacha eventos a callbacks. Lee en su
propio thread; todo lo que sale va por `IHost.Invoke()` antes de tocar GDI+ o el
socket del host, igual que el resto de las acciones de red del repo.

```csharp
Connect(string clientId)                      // pipe + handshake
JObj Request(string cmd, JObj args, int timeoutMs)
Subscribe(string evt, Action<JObj> handler)
event Action Disconnected
```

### `src/DiscordAuth.cs` — credenciales

Dueño único del token. Store en
`%LOCALAPPDATA%\StreamDeckAudio\discord.dat`, cifrado con DPAPI
(`ProtectedData.Protect`, `DataProtectionScope.CurrentUser`). Guarda
`access_token`, `refresh_token`, `expires_at`, `scope`.

```csharp
bool   HasCredentials { get; }        // clientId + secret cargados
bool   IsAuthorized   { get; }        // token vivo en disco
string AccessToken()                  // renueva si hace falta, o null
void   Enroll()                       // abre su propio RPC: AUTHORIZE -> code -> exchange
```

**Regla 1 — nunca quedarse sin credencial.** `AccessToken()` renueva al 80 % de
vida (día ~5,6), no al vencer. El token nuevo se escribe **antes** de descartar
el viejo; si el refresh falla, el viejo sigue en disco y se reintenta al próximo
tick. No existe un estado intermedio sin credencial. Es exactamente lo que
HotSpot hace al revés.

**Regla 2 — `AUTHORIZE` siempre sobre conexión nueva.** `Enroll()` abre su propio
`DiscordRpc`, autoriza y lo cierra. Nunca sobre una conexión ya autenticada.

### `src/DiscordService.cs` — sesión compartida

Una conexión para las dos teclas, no una por acción. Mantiene cacheados `deaf` y
`screenshare`, suscribe los dos eventos y avisa a quien escuche. Reconecta con
backoff cuando Discord se cierra y se vuelve a abrir.

```csharp
enum LinkState { NoDiscord, NoAuth, Ready }
LinkState State { get; }
bool Deaf { get; }  bool ScreenSharing { get; }
void SetDeaf(bool on)   void ToggleScreenShare()
event Action Changed
```

### `src/ActionsDiscord.cs` — las teclas

`DiscordDeafenAction` y `DiscordScreenShareAction` sobre `ActionBase`. Keypad,
`DefaultsToKnob => false`. `Press()` manda el comando; `Render()` dibuja desde
`DiscordService`. `WantsTick` mientras haya contextos, con la misma guarda de
render-key que usa `AppVolumeAction` para no inundar el socket.

**Regla 3 — el estado se ve.** `LinkState` mapea a tres dibujos distintos: Discord
cerrado, sin autorizar, y operativo. La falla nunca se disfraza de "todo bien",
que es el defecto que hizo invisible el bug de HotSpot durante dos días.

### Property Inspector

`plugin/com.knupwns.audioknob.sdPlugin/discord.html`, siguiendo `pi.js`/`pi.css`.
Campos CLIENT ID y CLIENT SECRET, botón **Autorizar**, y una línea de estado que
diga si hay token y cuándo vence. Las credenciales van al store de DPAPI vía
`sendToPlugin`, no a los settings del perfil: no tienen por qué viajar en el
`.sdProfile` ni duplicarse por cada tecla.

## Riesgos

**Scopes de screen share — resuelto a favor.** Discord otorgó los seis scopes
pedidos a una app creada por el usuario, incluidos `rpc.screenshare.read` y
`rpc.screenshare.write`. Verificado contra el string de scope que devolvió
Discord de verdad (`token.Scope`, leído del token guardado), no contra lo que
se pidió — ver el reporte de Task 5. No hizo falta ningún rediseño ni caer al
client de StreamKit para ninguna de las dos teclas.

**`redirect_uri` en el exchange de un code emitido por RPC — confirmado.** Con
`http://localhost` registrado en la app, el canje funciona igual que en un
flujo OAuth con redirect real, pese a que acá nunca hay una redirección de
verdad de por medio.

**Comandos privados — sigue igual.** `TOGGLE_SCREENSHARE` y
`SCREENSHARE_STATE_UPDATE` siguen sin contrato documentado — se tomaron del
bundle de HotSpot, no de nada que Discord publique — y pueden cambiar o
desaparecer en cualquier update suyo. Es el mismo riesgo que ya se corría con
el plugin de HotSpot, ni más ni menos.

**El picker de pantalla es de Discord, no hay forma de saltearlo.**
`TOGGLE_SCREENSHARE` no toma argumentos, y no existe ningún otro comando de
screenshare en la superficie RPC de Discord — se buscó SCREEN/SOURCE/CAPTURE/
LIVE/STREAM en todo el bundle del competidor y no apareció nada más.
Compartir la ventana con foco sin pasar por el diálogo de Discord no es
posible desde este protocolo: el diálogo es el comportamiento del propio
Discord frente a ese comando, y el plugin que esto reemplaza hace exactamente
lo mismo. Automatizarlo implicaría clickear el diálogo de Discord o mandar
teclas — el tipo de hack a nivel de SO que este proyecto evita a propósito —
así que el picker queda. Se deja escrito para que nadie lo vuelva a
investigar.

**El campo `active` de `SCREENSHARE_STATE_UPDATE` sigue sin confirmar.** Se
tomó del bundle de HotSpot, no de documentación de Discord, y sigue siendo
una suposición. Task 6 intentó verificarlo dos veces — con Discord abierto
pero sin llamada de voz activa, y disparando `TOGGLE_SCREENSHARE` por RPC
fuera de una llamada — y ninguna de las dos hizo aparecer un evento real de
`SCREENSHARE_STATE_UPDATE` para leer su forma. Confirmarlo de verdad necesita
una llamada de voz real con pantalla compartida. Si alguna vez el estado de
la tecla deja de seguir un screen share real, este campo es el primer
sospechoso — no tratarlo como confirmado hasta entonces.

**`AUTHORIZE` se manda con `prompt: "none"`.** El alta corrió sin diálogo de
consentimiento para el dueño de la app (Task 5), probablemente porque ya
existía una concesión previa de ese mismo client en esa cuenta. Un usuario
que no sea el dueño de la app puede comportarse distinto en su primera
autorización — no se probó con una cuenta que no fuera la del dueño, y queda
sin confirmar.

**Correr la suite de tests es seguro hoy — no lo fue siempre.**
`DiscordTests.exe` redirige el store de credenciales (`DiscordAuth.
TestStoreOverride`) a un archivo temporal antes de tocar disco. Existe porque
una corrida anterior, antes de que ese redirect existiera, destruyó una
credencial viva y funcionando (la suite tiene un test que pisa el store a
propósito con basura para probar que `Load()` falla sin explotar). Vale la
pena decirlo explícito: correr `DiscordTests.exe` hoy no toca
`%LOCALAPPDATA%\StreamDeckAudio\discord.dat`, pero lo hizo una vez.

## Plan

**Fase 0 — `bin/DiscordProbe.exe`.** Companion de consola, mismo patrón que
`AudioToggle.exe`: `src/DiscordRpc.cs` + `src/DiscordAuth.cs` + `tools/DiscordProbe.cs`.
Autoriza, imprime los scopes concedidos, lee `GET_VOICE_SETTINGS`, togglea deafen
y dispara `TOGGLE_SCREENSHARE`. Contesta los tres riesgos de arriba antes de
escribir una línea de UI, y queda como herramienta de diagnóstico. No es código
descartable: son los dos módulos definitivos, ejercitados sin el deck de por
medio.

**Fase 1 — teclas.** `DiscordService.cs`, `ActionsDiscord.cs`, dos entradas en
`manifest.json` con dos states cada una, `discord.html`, iconos por `GenIcons`
(deafen/undeafen, screenshare/unscreenshare), y `build.ps1` con las fuentes
nuevas más `/reference:System.Security.dll`.

**Fase 2 — endurecer.** Reconexión cuando Discord arranca después que el deck,
los tres dibujos de `LinkState`, y el test de vencimiento.

## Testing

- `DiscordProbe.exe` cubre transporte, auth y comandos sin el host.
- `test/FakeApp.cs` ya emula el host para las acciones.
- **Vencimiento:** se fuerza escribiendo un `expires_at` pasado en `discord.dat` y
  se verifica que renueva y que, ante un refresh fallido, el token viejo sigue en
  disco. Es el caso que rompió a HotSpot; es el que no puede quedar sin test.

## Notas de compilación

`ServicePointManager.SecurityProtocol` hay que forzarlo a TLS 1.2 o discord.com
corta la conexión; el default de .NET Framework 4 es TLS 1.0. Si el enum no
expone `Tls12` en el framework contra el que compila `csc`, se castea el valor:

```csharp
ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
```

`ProtectedData` vive en `System.Security.dll`, que hay que agregar a los
`/reference` del backend y del probe.

JSON se hace con el helper `Json` y `JavaScriptSerializer` que el repo ya usa. Sin
NuGet, como todo lo demás acá.
