# Página IoT — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 7 acciones IoT nuevas en el plugin Deck Tools (3 perillas + 4 teclas) que manejan las luces WiZ y los equipos Tuya por red local.

**Architecture:** Dos núcleos de protocolo sin UI (`Wiz.cs`, `Tuya.cs`) con tests offline, un estado compartido `IotHub` (selección, marca "manual", sondeo en segundo plano) y las acciones en `ActionsIot.cs`, que sólo traducen eventos del deck a llamadas del hub y pintan con `KeyImage`.

**Tech Stack:** C# compilado con `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` vía `build.ps1` (sin SDK ni NuGet), `System.Web.Script.Serialization` para JSON, `System.Security.Cryptography` para AES. Tests: runner propio `test/Assert.cs` (`Check.Equal/True/Throws`).

**Spec:** `docs/superpowers/specs/2026-11-01-pagina-iot-design.md`

## Global Constraints

- **C# 5:** el csc de .NET Framework no acepta `$"..."`, `?.`, `=>` en miembros, `nameof`, `out var`. Usar la sintaxis del resto de `src/`.
- Namespace `StreamDeckAudio`, igual que el resto.
- Datos de equipos sólo desde `E:\Repositorios\Personal\_datos\iot` (`luces.json`, `tuya-devices.json`), ruta configurable. Ninguna key, IP ni MAC entra al repo, ni en tests (los tests usan valores inventados).
- `luces.json` lo sigue usando `wiz.py`: al reescribirlo, preservar todas las claves existentes y la indentación de 2 espacios.
- WiZ: UDP 38899. Volver al ritmo = `setPilot {"state":true}` sin otros parámetros. "A dormir" = `{"sceneId":10}`. Brillo 10–100. Temperaturas 2700 / 4000 / 6500 K.
- Tuya: protocolo 3.3, puerto TCP 6668, AES-128-ECB PKCS7 con la local key en ASCII.
- Nada de mandar comandos al purificador ni a Isaura de noche. Las pruebas en vivo de esos dos se hacen de día y avisando al user.
- Ninguna operación de red en el hilo de eventos: todo I/O va en `ThreadPool` y vuelve por `Host.Invoke`.
- Todos los textos de una tecla en una sola tinta (`KeyImage.Glyph` con el mismo color arriba y abajo).
- Commits en castellano con el trailer `Co-Authored-By:` del modelo que escribió el código.

## Review Focus

1. **Una lámpara no responde** (desenchufada, como la 8ª que ya está rota): el resto de las acciones sigue andando, la tecla no se cuelga y el redescubrimiento no se dispara en cada sondeo. Test en Task 1 (`Discovery_NotRepeatedWithinCooldown`).
2. **Router cambia la IP de una WiZ:** se redescubre por MAC y `luces.json` queda válido para `wiz.py`. Test en Task 1 (`Config_SaveKeepsUnknownKeys`).
3. **Giros rápidos de perilla** (10+ ticks en un segundo): las luces terminan en el valor final, sin una cola de paquetes que llega segundos después. Test en Task 3 (`Brightness_CoalescesBurst`).
4. **Respuesta Tuya vacía o con otra versión** (el equipo responde sin payload, o con el header `3.3`): se parsea sin excepción. Test en Task 2 (`Parse_EmptyAndVersionHeader`).
5. **La key de Tuya cambió** (alguien re-emparejó en la app): la tecla muestra error, no se cuelga. Test en Task 2 (`Decrypt_WrongKeyReturnsNull`).

---

### Task 1: Núcleo WiZ y configuración de equipos

**Files:**
- Create: `src/Wiz.cs`, `src/IotConfig.cs`, `test/IotTests.cs`
- Modify: `build.ps1` (nuevo exe de tests `bin\IotTests.exe` con `src\Json.cs`, `src\Wiz.cs`, `src\IotConfig.cs`, `src\Tuya.cs`, `src\IotHub.cs`, `test\Assert.cs`, `test\IotTests.cs`; agregar los archivos que todavía no existan a medida que aparecen)

**Interfaces:**
- Produces:
  - `sealed class WizPilot { bool On; int SceneId; int? Temp; int? Dimming; int? R, G, B; int? SchdPsetId; bool InRhythm /* SchdPsetId != null && SceneId == 0 */ }`
  - `static class WizProtocol { string GetPilot(); string SetPilot(IDictionary<string,object> p); string Resume() /* {"method":"setPilot","params":{"state":true}} */; WizPilot ParsePilot(string json) /* null si error o sin result */; string ParseMac(string json) }`
  - `sealed class WizClient { WizPilot Get(string ip); bool Set(string ip, IDictionary<string,object> p); Dictionary<string,string> Discover(string broadcast) /* mac→ip */ }`: 3 intentos, timeout 600 ms, como `wiz.py`.
  - `sealed class IotLight { string Group; string Name; string FullName /* "pc.norte" */; string Mac; string Ip }`
  - `sealed class IotTuyaDevice { string Alias; string Nombre; string Ip; string Tipo; string Id; string Key }`
  - `sealed class IotConfig { string Broadcast; List<IotLight> Lights; Dictionary<string,IotTuyaDevice> Tuya; static IotConfig Load(string folder); void SaveLightIp(IotLight light, string newIp) }`
  - `IotConfig.DefaultFolder = @"E:\Repositorios\Personal\_datos\iot"`

- [ ] **Step 1: Tests que fallan** en `test/IotTests.cs` (con `Main` que corre todo y sale con código ≠ 0 si hay fallas, igual que `DiscordTests`):
  - `Pilot_ParsesRhythm`: `ParsePilot("{\"method\":\"getPilot\",\"result\":{\"state\":true,\"sceneId\":0,\"temp\":4200,\"dimming\":79,\"schdPsetId\":5}}")` → `On`, `Temp==4200`, `Dimming==79`, `InRhythm==true`.
  - `Pilot_SceneIsNotRhythm`: `{"state":true,"sceneId":10,"dimming":100}` → `InRhythm==false`, `SceneId==10`.
  - `Pilot_ErrorIsNull`: `{"error":{"code":-32602}}` → `null`.
  - `Resume_IsBareState`: `WizProtocol.Resume()` == `{"method":"setPilot","params":{"state":true}}`.
  - `Config_LoadsGroupsAndTuya`: carpeta temporal con un `luces.json` y `tuya-devices.json` inventados (2 grupos, 1 enchufe) → 3 luces en orden de archivo, `FullName` correctos, `Tuya["noche"].Key` cruzado por nombre.
  - `Config_SaveKeepsUnknownKeys`: `SaveLightIp` cambia sólo esa IP; el json releído conserva `broadcast`, el bloque `tuya` y una clave extra inventada (`"x":1`).
  - `Discovery_NotRepeatedWithinCooldown`: ver Step 3.
- [ ] **Step 2:** `.\build.ps1; .\bin\IotTests.exe` → falla de compilación (tipos no definidos).
- [ ] **Step 3: Implementar.** Redescubrimiento: `WizClient.EnsureIp(IotConfig cfg, IotLight l, Func<DateTime> now)` intenta `Get`; si falla y pasaron ≥ 60 s desde el último `Discover`, descubre y, si encuentra la MAC con otra IP, llama `SaveLightIp`. El test usa un reloj inyectado y un `Func` de descubrimiento falso para contar llamadas (2 fallos seguidos dentro de 60 s → 1 descubrimiento). Para guardar el JSON con indentación de 2 espacios, escribir un serializador mínimo propio: `JavaScriptSerializer` no indenta.
- [ ] **Step 4:** `.\build.ps1; .\bin\IotTests.exe` → todo `ok`, salida 0.
- [ ] **Step 5: Prueba en vivo (sólo lectura):** `IotTests.exe --live` hace `Get` a las 7 luces de la config real e imprime `FullName On Dimming InRhythm`. Esperado: 7 líneas con datos, iguales a `python wiz.py status all`.
- [ ] **Step 6: Commit** `Núcleo WiZ y lectura de la config IoT`.

### Task 2: Núcleo Tuya 3.3

**Files:**
- Create: `src/Tuya.cs`
- Modify: `test/IotTests.cs`

**Interfaces:**
- Produces:
  - `static class TuyaProtocol { byte[] Frame(int seq, int cmd, byte[] payload); byte[] Encrypt(byte[] plain, string key); byte[] Decrypt(byte[] cipher, string key) /* null si padding inválido */; byte[] BuildQuery(string devId, string key, int seq); byte[] BuildControl(string devId, string key, int seq, IDictionary<string,object> dps, long unixTime); Dictionary<string,object> ParseResponse(byte[] frame, string key) /* dps o null */ }`
  - Constantes: `CmdControl = 7`, `CmdDpQuery = 10`. Prefijo `0x000055AA`, sufijo `0x0000AA55`, CRC32 sobre todo menos los 8 bytes finales. En CONTROL, al cifrado se le antepone `"3.3"` + 12 bytes en cero; en DP_QUERY, no.
  - `sealed class TuyaClient { Dictionary<string,object> Status(IotTuyaDevice d); bool Set(IotTuyaDevice d, IDictionary<string,object> dps) }`: TCP 6668, timeout 3 s, una conexión por operación.

- [ ] **Step 1: Generar vectores de referencia con tinytuya** (sólo en local, valores inventados): script en el scratchpad que, con `key="0123456789abcdef"`, `devId="testdev0001"` y `t=1700000000`, imprime en hex el frame que tinytuya arma para un DP_QUERY con `seq=1` y un CONTROL `{"1":true}` con `seq=2` (forzando `t`). Pegar los hex como constantes en el test.
- [ ] **Step 2: Tests que fallan:**
  - `Frame_MatchesTinytuyaQuery` y `Frame_MatchesTinytuyaControl`: bytes idénticos a los vectores.
  - `Roundtrip_Decrypt`: `Decrypt(Encrypt(x,k),k)` == `x`.
  - `Decrypt_WrongKeyReturnsNull`: con otra key → `null`, sin excepción.
  - `Parse_EmptyAndVersionHeader`: frame con payload de 4 bytes de return code y nada más → `null` sin excepción; frame cuyo payload trae `"3.3"`+12 bytes antes del cifrado → dps parseados.
- [ ] **Step 3:** build + correr → fallan.
- [ ] **Step 4: Implementar** hasta verde.
- [ ] **Step 5: Prueba en vivo (sólo lectura):** `IotTests.exe --live` suma `Status` de los 4 equipos Tuya. Esperado: los mismos dps que `wiz.tuya_device(...).status()` (purificador con `"4":"sleep"`, Isaura con `"8"` batería).
- [ ] **Step 6: Commit** `Protocolo local Tuya 3.3 en C#`.

### Task 3: IotHub, el estado compartido

**Files:**
- Create: `src/IotHub.cs`
- Modify: `test/IotTests.cs`

**Interfaces:**
- Consumes: `IotConfig`, `WizClient`, `TuyaClient`, `WizPilot`.
- Produces (`sealed class IotHub`, una instancia estática `IotHub.Shared` creada perezosamente con la config por defecto; el constructor recibe dependencias para test):
  - Selección: `IList<string> Targets /* "Todas","PC","Cama", luego FullName de cada luz en orden de config */`, `string Selected`, `void MoveSelection(int ticks)`, `void TouchSelection()` (reinicia el plazo), `void Tick(DateTime now)` → vuelve a `"Todas"` si pasaron ≥ 10 s desde el último toque.
  - `IList<IotLight> Resolve(string target)`.
  - Acciones de luz (todas sobre `Selected` salvo las que dicen lo contrario): `Toggle()`, `Resume()` (también limpia "manual"), `ApplyColor(int r,int g,int b)`, `AddBrightness(int delta)`, `CycleTemperature()` (2700→4000→6500→2700, según la temperatura actual de la primera lámpara del objetivo), `Bedtime()` (siempre las 7), `ResumeAll()`, `AllOff()` (7 luces + `noche` + `vestidor` + `purificador`, nunca Isaura), `Identify(IotLight l)` (parpadeo: brillo 10 → valor previo).
  - Marca manual: `bool IsManual(IotLight l)`; brillo, color y temperatura la ponen; `Resume`/`ResumeAll` la sacan.
  - Estado en caché: `WizPilot Pilot(IotLight l)`, `Dictionary<string,object> TuyaStatus(string alias)`, `event Action Changed`.
  - Sondeo: `void SetVisible(object owner, bool visible)`; mientras haya algún dueño visible, un hilo de fondo refresca todo cada 3 s.
  - Brillo con ráfagas: `AddBrightness` acumula el objetivo y manda como mucho un paquete cada 100 ms por lámpara, siempre el último valor.

- [ ] **Step 1: Tests que fallan** (con clientes falsos que registran los paquetes enviados):
  - `Selection_CyclesAndWraps`: con 2 luces → `Todas, PC, Cama, pc.a, cama.b, Todas`; ticks negativos al revés.
  - `Selection_ResetsAfter10s`: `MoveSelection(1)`, `Tick(now+9s)` sigue en `PC`; `Tick(now+10s)` vuelve a `Todas`.
  - `Resume_SendsBareStateAndClearsManual`.
  - `Bedtime_AlwaysAllLights` aunque `Selected=="PC"`.
  - `AllOff_SkipsVacuum`: el falso de Tuya recibe `noche`, `vestidor`, `purificador` y no `aspiradora`.
  - `CycleTemperature_FromCurrent`: luz en 4200 K → 6500; luz en 6500 → 2700.
  - `Brightness_CoalescesBurst`: 12 `AddBrightness(+5)` en < 100 ms desde 50 % → a lo sumo 2 paquetes, y el último es `dimming:100`.
- [ ] **Step 2:** build + correr → fallan.
- [ ] **Step 3: Implementar.**
- [ ] **Step 4:** verde.
- [ ] **Step 5: Commit** `Estado compartido de la página IoT`.

### Task 4: Toque largo en el host

**Files:**
- Modify: `src/PluginHost.cs:142-145` (sumar `"keyUp"` a `ActionScoped`), el `switch` de despacho (~línea 423), y `ActionBase` (~línea 102).

**Interfaces:**
- Produces: en `ActionBase`, `public virtual void Release(string context) { }` y `protected virtual bool HandlesLongPress { get { return false; } }`. Si es `true`, `keyDown` sólo anota la hora y `keyUp` llama a `LongPress(context)` si pasaron ≥ 500 ms, o a `Press(context)` si no. Si es `false`, `keyDown` llama a `Press` como hoy (las acciones existentes no cambian).

- [ ] **Step 1:** implementar. El repo no tiene tests del host: la verificación es manual en Task 6.
- [ ] **Step 2:** `.\build.ps1` compila sin errores.
- [ ] **Step 3: Commit** `Toque largo en teclas del plugin`.

### Task 5: Acciones de luces (perillas + tecla Luces)

**Files:**
- Create: `src/ActionsIot.cs`, `plugin/com.knupwns.audioknob.sdPlugin/iotpalette.html`, `iotbrightness.html`, `iotlights.html`; imágenes base en `images/` (`iot-selector`, `iot-palette`, `iot-brightness`, `iot-rhythm`, `iot-bedtime`, `iot-temp`, `iot-off`, en el formato que ya usan las otras de `images/`)
- Modify: `src/PluginHost.cs:169-182` (registrar), `build.ps1` (sumar `src\Wiz.cs`, `src\Tuya.cs`, `src\IotConfig.cs`, `src\IotHub.cs`, `src\ActionsIot.cs` al exe del plugin), `manifest.json` (4 acciones)

**Interfaces:**
- Consumes: `IotHub.Shared` (Task 3), `KeyImage.Glyph` (`src/KeyImage.cs:72`).
- Produces, en el manifest:
  - `com.knupwns.audioknob.iot.selector` — Knob, sin menú (usar `separator.html` o un HTML vacío).
  - `com.knupwns.audioknob.iot.palette` — Knob, `iotpalette.html`: lista editable de colores `#rrggbb`; por defecto `#ff0000 #ff6400 #ffc800 #00ff00 #00c8ff #0000ff #9600ff #ff0078`. Setting `colors` (string separado por espacios).
  - `com.knupwns.audioknob.iot.brightness` — Knob, `iotbrightness.html`: setting `step`, 1–25, por defecto 5.
  - `com.knupwns.audioknob.iot.lights` — Keypad, `iotlights.html`: setting `function` ∈ `rhythm | bedtime | temp | alloff`.

Comportamiento (de la tabla del spec):
- Selector: girar → `MoveSelection` y, si cae en una luz suelta, `Identify`; apretar → `Toggle`. Pantalla: `Selected` arriba y abajo `ON`/`OFF` + `ritmo`/`manual`.
- Paleta: girar mueve un índice por contexto y repinta la muestra; apretar → `ApplyColor`. Sólo girar no envía nada a las luces.
- Brillo: girar → `AddBrightness(ticks*step)`; apretar → `Resume`. Pantalla: `%` de la primera luz del objetivo.
- Luces: `rhythm` → `Resume` (marcada si las 7 `InRhythm` y ninguna manual) · `bedtime` → `Bedtime` (marcada si las 7 en escena 10) · `temp` → `CycleTemperature` (muestra K) · `alloff` → `AllOff`.
- Todas suscriben `IotHub.Changed` → repintar; `WillAppear/WillDisappear` → `SetVisible`; `WantsTick` → `IotHub.Shared.Tick(DateTime.Now)` y repintar el Selector.

- [ ] **Step 1:** implementar acciones, HTML y manifest.
- [ ] **Step 2:** `.\build.ps1` sin errores; `.\bin\IotTests.exe` sigue verde.
- [ ] **Step 3: Instalar:** `.\install-plugin.ps1 -Restart`.
- [ ] **Step 4: Verificación en vivo con el user** (de día): armar la página, girar el Selector hasta `pc.norte` (parpadea), bajar brillo (sólo esa baja), apretar Brillo (vuelve a la curva: `wiz.py status` muestra los mismos valores que las demás), aplicar un color, A dormir (las 7 en escena 10), Ritmo (las 7 vuelven). Esperar 10 s sin tocar: el Selector muestra `Todas`.
- [ ] **Step 5: Commit** `Perillas y teclas de luces de la página IoT`.

### Task 6: Acciones Tuya (enchufes, purificador, Isaura)

**Files:**
- Create: `plugin/com.knupwns.audioknob.sdPlugin/iotplug.html`; imágenes `iot-plug`, `iot-purifier`, `iot-vacuum`
- Modify: `src/ActionsIot.cs`, `src/PluginHost.cs` (registrar), `manifest.json` (3 acciones), `test/IotTests.cs`

**Interfaces:**
- Consumes: `IotHub.TuyaStatus`, `TuyaClient.Set` vía nuevos métodos del hub: `TogglePlug(string alias)`, `TogglePurifier()`, `NextPurifierSpeed()`, `VacuumStep()`.
- Produces, en el manifest:
  - `com.knupwns.audioknob.iot.plug` — Keypad, `iotplug.html`: setting `plug` ∈ `noche | vestidor`.
  - `com.knupwns.audioknob.iot.purifier` — Keypad, `HandlesLongPress = true`.
  - `com.knupwns.audioknob.iot.vacuum` — Keypad.

Comportamiento:
- Enchufe: alterna DP1. Muestra el nombre y `ON`/`OFF`.
- Purificador: toque → alterna DP1; toque largo → DP4 al siguiente de `sleep→low→mid→high→auto→sleep`. Muestra velocidad + `filtro N%`; con DP5 ≤ 15, el texto del filtro en `KeyImage.Warn` (la única excepción a la tinta única, porque es una alerta).
- Isaura: según DP5: `standby|charging|charge_done|sleep` → DP1 `true` (limpiar) · `cleaning|zone_clean|part_clean|goto_pos` → DP2 `true` (pausa) · `paused` → DP3 `true` (a la base). Muestra estado traducido (`en base`, `limpiando`, `en pausa`, `volviendo`) + batería DP8; si DP28 ≠ 0, `ShowAlert` y texto `falla`.

- [ ] **Step 1: Tests que fallan:** `Vacuum_StepByStatus` (tabla de los 3 casos con un falso de Tuya), `Purifier_SpeedCycle` (`auto` → `sleep`).
- [ ] **Step 2:** build + correr → fallan. **Step 3:** implementar. **Step 4:** verde.
- [ ] **Step 5:** `.\build.ps1; .\install-plugin.ps1 -Restart`.
- [ ] **Step 6: Verificación en vivo con el user, de día y avisando:** enchufe Noche on/off; purificador on, toque largo cambia velocidad, off; Isaura: un toque arranca, otro pausa, otro la manda a la base. Todo off apaga luces, enchufes y purificador y no toca a Isaura.
- [ ] **Step 7: Commit** `Enchufes, purificador e Isaura en la página IoT`.
