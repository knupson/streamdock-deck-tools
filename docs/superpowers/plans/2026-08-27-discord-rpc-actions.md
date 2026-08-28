# Discord nativo (deafen + screen share) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Dos teclas nativas en DeckTools — deafen y screen share de Discord — con credenciales propias que se renuevan sin intervención.

**Architecture:** Cliente IPC sobre el named pipe de Discord (`src/DiscordRpc.cs`), OAuth con `refresh_token` y store cifrado con DPAPI (`src/DiscordAuth.cs`), una sesión compartida por ambas teclas (`src/DiscordService.cs`), y dos acciones sobre `ActionBase` (`src/ActionsDiscord.cs`). La Fase 0 entrega un probe de consola que valida transporte y scopes antes de que ninguna UI dependa de la respuesta.

**Tech Stack:** C# contra .NET Framework, compilado con `csc.exe` de Windows. Sin SDK, sin NuGet, sin dependencias externas. `System.Web.Script.Serialization.JavaScriptSerializer` para JSON, `System.Security.Cryptography.ProtectedData` para el store, `HttpWebRequest` para OAuth.

**Spec:** `docs/superpowers/specs/2026-08-27-discord-rpc-actions-design.md`

## Global Constraints

- **Sin NuGet ni SDK.** Todo compila con `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` vía `build.ps1`. Cualquier dependencia nueva tiene que ser una DLL que ya venga con Windows.
- **TLS 1.2 obligatorio.** `ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;` antes de la primera llamada HTTPS. El default de .NET Framework 4 es TLS 1.0 y discord.com corta la conexión.
- **Namespace `StreamDeckAudio`** en todos los archivos nuevos, como el resto del repo.
- **Texto uniforme en los tiles.** Usar siempre el overload `KeyImage.Glyph(baseName, top, bottom, knob)` de cuatro argumentos. El overload con `bottomColor` existe pero **no se usa acá**: una sola tinta para todas las filas de texto.
- **Nada de `await`.** El repo es .NET Framework sin async/await en el backend; el I/O bloqueante va en su propio thread y vuelve por `IHost.Invoke()`.
- **Regla 1 — nunca quedarse sin credencial.** El token nuevo se escribe antes de descartar el viejo. Si el refresh falla, el viejo sigue en disco.
- **Regla 2 — `AUTHORIZE` siempre sobre conexión RPC recién abierta.** Nunca sobre una ya autenticada.
- **Regla 3 — el estado se ve.** Auth caída o Discord cerrado se dibujan distinto de operativo. Nunca se finge que anda.

## File Structure

| Archivo | Responsabilidad |
|---|---|
| `src/DiscordRpc.cs` | Transporte: pipe, frames, handshake, correlación por nonce, eventos. No sabe de OAuth ni de teclas. |
| `src/DiscordAuth.cs` | Dueño único del token: store DPAPI, exchange, refresh, enroll. |
| `src/DiscordService.cs` | Sesión compartida: estado cacheado, suscripciones, reconexión. |
| `src/ActionsDiscord.cs` | `DiscordDeafenAction`, `DiscordScreenShareAction`. |
| `tools/DiscordProbe.cs` | Companion de consola. Valida y diagnostica sin el deck. |
| `test/Assert.cs` | Runner de asserts mínimo (el repo no tiene ninguno). |
| `test/DiscordTests.cs` | Tests offline: codec de frames, store, política de expiración. |
| `plugin/.../discord.html` | Property Inspector: CLIENT ID/SECRET, botón autorizar, estado. |
| `tools/GenIcons.cs` | Modificar: cuatro dibujos nuevos. |
| `build.ps1` | Modificar: fuentes nuevas, `System.Security.dll`, dos ejecutables nuevos. |
| `plugin/.../manifest.json` | Modificar: dos acciones nuevas, dos states cada una. |

---

### Task 1: Runner de tests y codec de frames

Arranca offline a propósito: el codec se puede probar entero contra un `MemoryStream`, sin Discord abierto. El runner de asserts va acá porque es el primer task que lo necesita.

**Files:**
- Create: `test/Assert.cs`
- Create: `test/DiscordTests.cs`
- Create: `src/DiscordRpc.cs`
- Modify: `build.ps1`

**Interfaces:**
- Consumes: nada.
- Produces: `DiscordRpc.WriteFrame(Stream, int, string)`, `DiscordRpc.ReadFrame(Stream, out int, out string)` — ambos `internal static`, para que los tests los alcancen. `Check.Equal<T>(string, T, T)`, `Check.True(string, bool)`, `Check.Throws(string, Action)`, `Check.Report()` que devuelve el exit code.

- [ ] **Step 1: Escribir el runner de asserts**

`test/Assert.cs`:

```csharp
// Runner de asserts mínimo. El repo no tiene framework de tests y no puede
// tener uno: no hay NuGet. Esto es lo que hace falta y nada más.

using System;
using System.Collections.Generic;

namespace StreamDeckAudio
{
    internal static class Check
    {
        private static readonly List<string> Failures = new List<string>();
        private static int _total;

        public static void Equal<T>(string name, T expected, T actual)
        {
            _total++;
            if (Equals(expected, actual)) { Console.WriteLine("  ok    " + name); return; }
            Failures.Add(name + ": esperaba <" + expected + ">, vino <" + actual + ">");
            Console.WriteLine("  FALLA " + name);
        }

        public static void True(string name, bool condition)
        {
            Equal(name, true, condition);
        }

        public static void Throws(string name, Action work)
        {
            _total++;
            try
            {
                work();
                Failures.Add(name + ": esperaba una excepcion, no hubo");
                Console.WriteLine("  FALLA " + name);
            }
            catch (Exception)
            {
                Console.WriteLine("  ok    " + name);
            }
        }

        public static int Report()
        {
            Console.WriteLine();
            Console.WriteLine(_total - Failures.Count + "/" + _total + " pasaron");
            foreach (string failure in Failures) Console.WriteLine("  " + failure);
            return Failures.Count == 0 ? 0 : 1;
        }
    }
}
```

- [ ] **Step 2: Escribir el test que falla**

`test/DiscordTests.cs`:

```csharp
using System;
using System.IO;
using System.Text;

namespace StreamDeckAudio
{
    internal static class DiscordTests
    {
        private static void FrameRoundTrip()
        {
            Console.WriteLine("frames:");
            using (var buffer = new MemoryStream())
            {
                DiscordRpc.WriteFrame(buffer, 1, "{\"cmd\":\"PING\"}");
                buffer.Position = 0;

                int opcode;
                string json;
                Check.True("ReadFrame devuelve true", DiscordRpc.ReadFrame(buffer, out opcode, out json));
                Check.Equal("opcode", 1, opcode);
                Check.Equal("payload", "{\"cmd\":\"PING\"}", json);
            }
        }

        private static void HeaderIsLittleEndian()
        {
            using (var buffer = new MemoryStream())
            {
                DiscordRpc.WriteFrame(buffer, 0, "ab");
                byte[] raw = buffer.ToArray();

                Check.Equal("largo total", 10, raw.Length);
                Check.Equal("opcode byte 0", (byte)0, raw[0]);
                Check.Equal("length byte 4", (byte)2, raw[4]);
                Check.Equal("length byte 5", (byte)0, raw[5]);
            }
        }

        private static void UnicodeSurvives()
        {
            using (var buffer = new MemoryStream())
            {
                DiscordRpc.WriteFrame(buffer, 1, "{\"n\":\"ñé\"}");
                buffer.Position = 0;

                int opcode;
                string json;
                DiscordRpc.ReadFrame(buffer, out opcode, out json);
                Check.Equal("utf-8", "{\"n\":\"ñé\"}", json);
            }
        }

        private static void TruncatedStreamIsFalseNotHang()
        {
            using (var buffer = new MemoryStream(new byte[] { 1, 0, 0, 0, 20, 0, 0, 0, 65 }))
            {
                int opcode;
                string json;
                Check.Equal("stream cortado devuelve false", false,
                            DiscordRpc.ReadFrame(buffer, out opcode, out json));
            }
        }

        public static int Main()
        {
            FrameRoundTrip();
            HeaderIsLittleEndian();
            UnicodeSurvives();
            TruncatedStreamIsFalseNotHang();
            return Check.Report();
        }
    }
}
```

- [ ] **Step 3: Agregar el ejecutable de tests a `build.ps1`**

Insertar después del bloque de `FakeApp.exe`:

```powershell
# Tests offline. No necesitan Discord ni el deck.
Invoke-Csc -Out (Join-Path $bin 'DiscordTests.exe') `
           -Sources @('src\AudioCore.cs', 'src\DiscordRpc.cs',
                      'test\Assert.cs', 'test\DiscordTests.cs') `
           -Refs @('System.Web.Extensions.dll')
```

`src\AudioCore.cs` entra porque ahí vive `Log`, que `DiscordRpc` usa.

- [ ] **Step 4: Correr y verificar que falla a compilar**

Run: `.\build.ps1`
Expected: FALLA con `CS0103` o `CS0117` — `DiscordRpc` no existe todavía. Ese es el fallo esperado.

- [ ] **Step 5: Implementar el codec mínimo**

`src/DiscordRpc.cs`:

```csharp
// Cliente del RPC local de Discord.
//
// Discord expone un named pipe por cada instancia: \\.\pipe\discord-ipc-0 a -9.
// El encuadre es opcode y largo en int32 little-endian, y despues el JSON en
// UTF-8. Es formato binario propio, no HTTP ni WebSocket.

using System;
using System.IO;
using System.Text;

namespace StreamDeckAudio
{
    internal sealed partial class DiscordRpc
    {
        internal const int OpHandshake = 0;
        internal const int OpFrame = 1;
        internal const int OpClose = 2;
        internal const int OpPing = 3;
        internal const int OpPong = 4;

        internal static void WriteFrame(Stream stream, int opcode, string json)
        {
            byte[] payload = Encoding.UTF8.GetBytes(json);
            var header = new byte[8];
            Buffer.BlockCopy(BitConverter.GetBytes(opcode), 0, header, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(payload.Length), 0, header, 4, 4);

            stream.Write(header, 0, header.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        internal static bool ReadFrame(Stream stream, out int opcode, out string json)
        {
            opcode = 0;
            json = null;

            var header = new byte[8];
            if (!ReadExactly(stream, header, 8)) return false;

            opcode = BitConverter.ToInt32(header, 0);
            int length = BitConverter.ToInt32(header, 4);
            if (length < 0 || length > 8 * 1024 * 1024) return false;

            var payload = new byte[length];
            if (!ReadExactly(stream, payload, length)) return false;

            json = Encoding.UTF8.GetString(payload);
            return true;
        }

        // Un read parcial es normal en un pipe; un stream que se termina no lo es.
        private static bool ReadExactly(Stream stream, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int got = stream.Read(buffer, read, count - read);
                if (got <= 0) return false;
                read += got;
            }
            return true;
        }
    }
}
```

`BitConverter` es little-endian en x64, que es la única plataforma que `build.ps1` compila (`/platform:x64`).

- [ ] **Step 6: Correr los tests**

Run: `.\build.ps1; .\bin\DiscordTests.exe; echo "exit=$LASTEXITCODE"`
Expected: `9/9 pasaron`, `exit=0`.

- [ ] **Step 7: Commit**

```bash
git add test/Assert.cs test/DiscordTests.cs src/DiscordRpc.cs build.ps1
git commit -m "test: add assert runner and Discord IPC frame codec"
```

---

### Task 2: Conexión, handshake y correlación por nonce

**Files:**
- Create: `src/Json.cs` (movido desde `PluginHost.cs`, Step 1)
- Modify: `src/PluginHost.cs` (se le saca la clase `Json`)
- Modify: `src/DiscordRpc.cs`
- Create: `tools/DiscordProbe.cs`
- Modify: `build.ps1`

**Interfaces:**
- Consumes: `DiscordRpc.WriteFrame`, `DiscordRpc.ReadFrame` del Task 1.
- Produces:
  - `bool DiscordRpc.Connect(string clientId)` — abre el pipe y hace handshake. `false` si Discord no está.
  - `Dictionary<string, object> DiscordRpc.Request(string cmd, Dictionary<string, object> args, int timeoutMs)` — devuelve el `data` de la respuesta, o lanza `DiscordRpcException` si vino `evt:"ERROR"`.
  - `void DiscordRpc.Subscribe(string evt, Action<Dictionary<string, object>> handler)`
  - `event Action DiscordRpc.Disconnected`
  - `void DiscordRpc.Close()`
  - `class DiscordRpcException : Exception` con `public int Code`.

- [ ] **Step 1: Extraer `Json` a su propio archivo**

Tiene que pasar **antes** de implementar nada de este task. A partir del Step 5, `DiscordRpc` usa `Json.Escape`, `Json.Str`, `Json.Dict` y `Json.Int`. Esa clase vive hoy dentro de `src/PluginHost.cs`, que ni `DiscordTests.exe` ni `DiscordProbe.exe` pueden compilar: arrastra el WebSocket entero. Sin este paso, el Step 7 no compila.

Cortar la clase `internal static class Json` completa de `src/PluginHost.cs` y pegarla en `src/Json.cs`, con el mismo namespace `StreamDeckAudio` y los `using` que necesita (`System`, `System.Collections.Generic`, `System.Globalization`, `System.Text`). **No cambiar una línea del cuerpo** — es un movimiento puro, y el diff tiene que poder leerse como tal.

En `build.ps1`, agregar `'src\Json.cs'` a las fuentes de **`DeckTools.exe`** y de **`DiscordTests.exe`**. Lo de `DeckTools.exe` no es opcional: sacar `Json` de `PluginHost.cs` sin agregarlo rompe el build principal en el acto.

Verificar antes de seguir:

Run: `.\build.ps1`
Expected: compila igual que antes, sin errores.

Run: `.\bin\DiscordTests.exe; echo "exit=$LASTEXITCODE"`
Expected: `9/9 pasaron`, `exit=0`.

- [ ] **Step 2: Escribir el test que falla**

El handshake necesita Discord corriendo, así que este test vive en el probe, no en `DiscordTests.exe`. `tools/DiscordProbe.cs`:

```csharp
// Companion de consola para el backend de Discord. Ejercita transporte, auth y
// comandos sin el deck de por medio, que es como se diagnostica cuando una tecla
// no responde.
//
//   DiscordProbe.exe connect
//   DiscordProbe.exe enroll <client-id> <client-secret>
//   DiscordProbe.exe status
//   DiscordProbe.exe deafen
//   DiscordProbe.exe screenshare

using System;
using System.Collections.Generic;

namespace StreamDeckAudio
{
    internal static class DiscordProbe
    {
        private static int Connect()
        {
            var rpc = new DiscordRpc();
            if (!rpc.Connect(DiscordAuth.StreamKitClientId))
            {
                Console.WriteLine("no hay pipe de Discord abierto. Discord corriendo?");
                return 1;
            }

            Console.WriteLine("conectado, handshake ok");
            rpc.Close();
            return 0;
        }

        public static int Main(string[] args)
        {
            Log.Component = "discordprobe";
            string command = args.Length > 0 ? args[0].ToLowerInvariant() : "connect";

            try
            {
                switch (command)
                {
                    case "connect": return Connect();
                    default:
                        Console.WriteLine("comando desconocido: " + command);
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("fallo: " + ex.Message);
                return 1;
            }
        }
    }
}
```

`DiscordAuth.StreamKitClientId` es la constante `"207646673902501888"`. Se usa acá sólo para probar el transporte: el handshake no requiere autorización, así que sirve para validar el pipe antes de que exista ninguna app propia. Definirla en este task como `internal const string` dentro de una clase `DiscordAuth` mínima si todavía no existe.

- [ ] **Step 3: Agregar el probe a `build.ps1`**

```powershell
# Companion de consola para el backend de Discord.
Invoke-Csc -Out (Join-Path $bin 'DiscordProbe.exe') `
           -Sources @('src\AudioCore.cs', 'src\Json.cs', 'src\DiscordRpc.cs',
                      'src\DiscordAuth.cs', 'tools\DiscordProbe.cs') `
           -Refs @('System.Web.Extensions.dll', 'System.Security.dll')
```

- [ ] **Step 4: Correr y verificar que falla**

Run: `.\build.ps1`
Expected: FALLA — `Connect`, `Close` y `DiscordAuth` no existen.

- [ ] **Step 5: Implementar conexión, handshake y correlación**

Agregar a `src/DiscordRpc.cs` (la clase ya es `partial`; se puede seguir en el mismo archivo):

```csharp
using System.Collections.Generic;
using System.IO.Pipes;
using System.Threading;
using System.Web.Script.Serialization;

internal sealed class DiscordRpcException : Exception
{
    public readonly int Code;

    public DiscordRpcException(int code, string message) : base(message)
    {
        Code = code;
    }
}
```

Y dentro de `DiscordRpc`:

```csharp
private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

private NamedPipeClientStream _pipe;
private Thread _reader;
private volatile bool _closing;

private readonly Dictionary<string, Dictionary<string, object>> _replies =
    new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
private readonly Dictionary<string, Action<Dictionary<string, object>>> _handlers =
    new Dictionary<string, Action<Dictionary<string, object>>>(StringComparer.Ordinal);
private readonly object _gate = new object();

public event Action Disconnected;

/// <summary>Abre el primer pipe que responda y completa el handshake.</summary>
public bool Connect(string clientId)
{
    for (int i = 0; i < 10; i++)
    {
        var pipe = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut);
        try
        {
            pipe.Connect(200);
        }
        catch (Exception)
        {
            pipe.Dispose();
            continue;
        }

        _pipe = pipe;
        WriteFrame(_pipe, OpHandshake,
                   "{\"v\":1,\"client_id\":\"" + Json.Escape(clientId) + "\"}");

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "discord-rpc" };
        _reader.Start();
        Log.Write("Discord RPC: conectado a discord-ipc-" + i);
        return true;
    }
    return false;
}

private void ReadLoop()
{
    try
    {
        int opcode;
        string json;
        while (!_closing && ReadFrame(_pipe, out opcode, out json))
        {
            if (opcode == OpPing) { WriteFrame(_pipe, OpPong, json); continue; }
            if (opcode != OpFrame) continue;

            var message = Serializer.Deserialize<Dictionary<string, object>>(json);
            Dispatch(message);
        }
    }
    catch (Exception ex)
    {
        if (!_closing) Log.Write("Discord RPC: lectura cortada: " + ex.Message);
    }

    if (_closing) return;
    Action handler = Disconnected;
    if (handler != null) handler();
}

// Una respuesta trae el nonce del pedido; un evento suscrito no trae ninguno.
private void Dispatch(Dictionary<string, object> message)
{
    string nonce = Json.Str(message, "nonce");
    if (!string.IsNullOrEmpty(nonce))
    {
        lock (_gate)
        {
            _replies[nonce] = message;
            Monitor.PulseAll(_gate);
        }
        return;
    }

    string evt = Json.Str(message, "evt");
    if (string.IsNullOrEmpty(evt)) return;

    Action<Dictionary<string, object>> subscriber;
    lock (_gate)
    {
        if (!_handlers.TryGetValue(evt, out subscriber)) return;
    }
    subscriber(Json.Dict(message, "data"));
}

public Dictionary<string, object> Request(string cmd, Dictionary<string, object> args, int timeoutMs)
{
    string nonce = Guid.NewGuid().ToString("N");
    var frame = new Dictionary<string, object>
    {
        { "cmd", cmd },
        { "args", args ?? new Dictionary<string, object>() },
        { "nonce", nonce }
    };
    WriteFrame(_pipe, OpFrame, Serializer.Serialize(frame));

    Dictionary<string, object> reply = WaitFor(nonce, timeoutMs);
    if (reply == null) throw new DiscordRpcException(0, cmd + ": sin respuesta en " + timeoutMs + " ms");

    if (Json.Str(reply, "evt") == "ERROR")
    {
        Dictionary<string, object> error = Json.Dict(reply, "data");
        throw new DiscordRpcException(Json.Int(error, "code", 0), Json.Str(error, "message"));
    }
    return Json.Dict(reply, "data");
}

private Dictionary<string, object> WaitFor(string nonce, int timeoutMs)
{
    DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
    lock (_gate)
    {
        while (true)
        {
            Dictionary<string, object> reply;
            if (_replies.TryGetValue(nonce, out reply)) { _replies.Remove(nonce); return reply; }

            var left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
            if (left <= 0) return null;
            Monitor.Wait(_gate, left);
        }
    }
}

public void Subscribe(string evt, Action<Dictionary<string, object>> handler)
{
    lock (_gate) { _handlers[evt] = handler; }

    string nonce = Guid.NewGuid().ToString("N");
    var frame = new Dictionary<string, object>
    {
        { "cmd", "SUBSCRIBE" }, { "evt", evt },
        { "args", new Dictionary<string, object>() }, { "nonce", nonce }
    };
    WriteFrame(_pipe, OpFrame, Serializer.Serialize(frame));
    WaitFor(nonce, 5000);
}

public void Close()
{
    _closing = true;
    try { if (_pipe != null) _pipe.Dispose(); }
    catch (Exception) { }
    _pipe = null;
}
```

Crear también `src/DiscordAuth.cs` con lo mínimo que el probe referencia:

```csharp
namespace StreamDeckAudio
{
    internal static class DiscordAuth
    {
        // El client de StreamKit. Se usa sólo para probar el transporte: el
        // handshake no pide autorización, así que valida el pipe antes de que
        // exista ninguna app propia.
        internal const string StreamKitClientId = "207646673902501888";
    }
}
```

- [ ] **Step 6: Correr contra Discord de verdad**

Run: `.\build.ps1; .\bin\DiscordProbe.exe connect`
Expected, con Discord abierto: `conectado, handshake ok`, exit 0.
Expected, con Discord cerrado: `no hay pipe de Discord abierto`, exit 1.

Probar **los dos casos**. El segundo es el que hace que la tecla sepa dibujar `NoDiscord` en vez de colgarse.

- [ ] **Step 7: Verificar que los tests del Task 1 siguen pasando**

Run: `.\bin\DiscordTests.exe; echo "exit=$LASTEXITCODE"`
Expected: `9/9 pasaron`, `exit=0`.

- [ ] **Step 8: Commit**

```bash
git add src/Json.cs src/PluginHost.cs src/DiscordRpc.cs src/DiscordAuth.cs tools/DiscordProbe.cs build.ps1
git commit -m "feat: connect to Discord RPC over the named pipe"
```

---

### Task 3: Store de credenciales con DPAPI

Acá vive la Regla 1. El test de que un refresh fallido no borra el token viejo es el más importante del plan: es exactamente el bug que rompió el plugin de HotSpot.

**Files:**
- Modify: `src/DiscordAuth.cs`
- Modify: `test/DiscordTests.cs`
- Modify: `build.ps1`

**Interfaces:**
- Consumes: nada del Task 2 más allá de la clase `DiscordAuth` ya creada.
- Produces:
  - `class DiscordToken` con `public string Access, Refresh, Scope; public DateTime ExpiresUtc;` y `public bool NeedsRenewal { get; }` (true pasado el 80 % de vida).
  - `static void DiscordAuth.Save(DiscordToken)`
  - `static DiscordToken DiscordAuth.Load()` — `null` si no hay o no se puede descifrar.
  - `static string DiscordAuth.StorePath { get; }`

- [ ] **Step 1: Escribir los tests que fallan**

Agregar a `test/DiscordTests.cs`. En `Main`, las llamadas van **en este orden**, después de las cuatro del Task 1:

```csharp
TokenRoundTrip();
RenewalWindow();
FailedRenewalKeepsOldToken();
CorruptStoreIsNullNotCrash();   // último: deja el store ilegible a proposito
```

`CorruptStoreIsNullNotCrash` pisa el archivo con basura, así que cualquier test que corra después vería un store roto.

```csharp
private static void TokenRoundTrip()
{
    Console.WriteLine("store:");
    var token = new DiscordToken
    {
        Access = "at-123",
        Refresh = "rt-456",
        Scope = "identify rpc",
        ExpiresUtc = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc)
    };
    DiscordAuth.Save(token);

    DiscordToken loaded = DiscordAuth.Load();
    Check.Equal("access", "at-123", loaded.Access);
    Check.Equal("refresh", "rt-456", loaded.Refresh);
    Check.Equal("scope", "identify rpc", loaded.Scope);
    Check.Equal("expiracion", token.ExpiresUtc, loaded.ExpiresUtc);
}

// 80 % de 7 dias son 5,6 dias. A los 6 hay que renovar; a los 3 todavia no.
private static void RenewalWindow()
{
    var fresh = new DiscordToken { Access = "a", ExpiresUtc = DateTime.UtcNow.AddDays(4) };
    Check.Equal("token de 4 dias no renueva", false, fresh.NeedsRenewal);

    var stale = new DiscordToken { Access = "a", ExpiresUtc = DateTime.UtcNow.AddDays(1) };
    Check.Equal("token de 1 dia renueva", true, stale.NeedsRenewal);

    var dead = new DiscordToken { Access = "a", ExpiresUtc = DateTime.UtcNow.AddDays(-1) };
    Check.Equal("token vencido renueva", true, dead.NeedsRenewal);
}

// Regla 1. Este es el bug de HotSpot, escrito como test.
private static void FailedRenewalKeepsOldToken()
{
    var original = new DiscordToken
    {
        Access = "viejo", Refresh = "rt-viejo",
        ExpiresUtc = DateTime.UtcNow.AddDays(-1)      // vencido a proposito
    };
    DiscordAuth.Save(original);

    // Refresh que falla: sin red, sin credenciales de app cargadas.
    DiscordAuth.SetClientCredentials(null, null);
    string token = DiscordAuth.AccessToken();

    Check.Equal("un refresh fallido devuelve null", null, token);
    Check.True("el token viejo sigue en disco", DiscordAuth.Load() != null);
    Check.Equal("y es el mismo", "viejo", DiscordAuth.Load().Access);
}

private static void CorruptStoreIsNullNotCrash()
{
    System.IO.File.WriteAllText(DiscordAuth.StorePath, "esto no es DPAPI");
    Check.Equal("store corrupto devuelve null", null, DiscordAuth.Load());
}
```

`RenewalWindow` usa un token de 4 días de vida restante para "no renueva": la ventana se calcula sobre la vida total de 7 días, así que a los 4 días restantes van 3 consumidos — 43 %, por debajo del 80 %.

- [ ] **Step 2: Correr y verificar que fallan**

Run: `.\build.ps1`
Expected: FALLA — `DiscordToken`, `Save`, `Load`, `AccessToken`, `SetClientCredentials`, `StorePath` no existen.

- [ ] **Step 3: Implementar el store**

Reemplazar `src/DiscordAuth.cs`:

```csharp
// Credenciales de Discord: store cifrado, renovacion y alta.
//
// Regla que ordena todo este archivo: el token nuevo se escribe antes de
// descartar el viejo. Si la renovacion falla, la credencial vieja sigue en
// disco y se reintenta despues. Nunca hay un estado sin credencial.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace StreamDeckAudio
{
    internal sealed class DiscordToken
    {
        public string Access;
        public string Refresh;
        public string Scope;
        public DateTime ExpiresUtc;

        private const double LifetimeDays = 7.0;
        private const double RenewAfter = 0.8;

        /// <summary>
        /// Cierto pasado el 80 % de la vida del token. Renovar temprano deja
        /// margen para reintentar; renovar al vencer no deja ninguno.
        /// </summary>
        public bool NeedsRenewal
        {
            get
            {
                double left = (ExpiresUtc - DateTime.UtcNow).TotalDays;
                return left <= LifetimeDays * (1.0 - RenewAfter);
            }
        }
    }

    internal static partial class DiscordAuth
    {
        internal const string StreamKitClientId = "207646673902501888";

        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DeckTools.Discord.v1");

        public static string StorePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "StreamDeckAudio");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "discord.dat");
            }
        }

        public static void Save(DiscordToken token)
        {
            if (token == null) return;
            var record = new Dictionary<string, object>
            {
                { "access", token.Access },
                { "refresh", token.Refresh },
                { "scope", token.Scope },
                { "expires", token.ExpiresUtc.ToString("o") }
            };

            byte[] plain = Encoding.UTF8.GetBytes(Serializer.Serialize(record));
            byte[] sealed_ = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

            // Escritura atomica: un corte de luz a mitad no puede dejar el store
            // a medio escribir, que seria justamente quedarse sin credencial.
            string temporary = StorePath + ".tmp";
            File.WriteAllBytes(temporary, sealed_);
            if (File.Exists(StorePath)) File.Delete(StorePath);
            File.Move(temporary, StorePath);
        }

        public static DiscordToken Load()
        {
            try
            {
                if (!File.Exists(StorePath)) return null;

                byte[] plain = ProtectedData.Unprotect(
                    File.ReadAllBytes(StorePath), Entropy, DataProtectionScope.CurrentUser);
                var record = Serializer.Deserialize<Dictionary<string, object>>(
                    Encoding.UTF8.GetString(plain));

                return new DiscordToken
                {
                    Access = Json.Str(record, "access"),
                    Refresh = Json.Str(record, "refresh"),
                    Scope = Json.Str(record, "scope"),
                    ExpiresUtc = DateTime.Parse(Json.Str(record, "expires"), null,
                                                System.Globalization.DateTimeStyles.RoundtripKind)
                };
            }
            catch (Exception ex)
            {
                Log.Write("Discord: store ilegible: " + ex.Message);
                return null;
            }
        }
    }
}
```

`Json` ya vive en `src/Json.cs` desde el Task 2, y `DiscordTests.exe` ya lo compila. Acá no hay que moverlo de nuevo.

- [ ] **Step 4: Dar a `DiscordTests.exe` lo que el store necesita**

En `build.ps1`, en el bloque de `DiscordTests.exe`: agregar `'src\DiscordAuth.cs'` a `-Sources` y `'System.Security.dll'` a `-Refs`. Sin la referencia, `ProtectedData` no resuelve y el test no compila.

Queda así:

```powershell
Invoke-Csc -Out (Join-Path $bin 'DiscordTests.exe') `
           -Sources @('src\AudioCore.cs', 'src\Json.cs', 'src\DiscordRpc.cs',
                      'src\DiscordAuth.cs', 'test\Assert.cs', 'test\DiscordTests.cs') `
           -Refs @('System.Web.Extensions.dll', 'System.Security.dll')
```

- [ ] **Step 5: Implementar `AccessToken` y `SetClientCredentials` como stubs honestos**

El refresh de verdad llega en el Task 4. Acá alcanza con la parte que los tests exigen — que un fallo no borre nada:

```csharp
private static string _clientId;
private static string _clientSecret;

public static void SetClientCredentials(string clientId, string clientSecret)
{
    _clientId = clientId;
    _clientSecret = clientSecret;
}

/// <summary>
/// Token vivo, renovando si hace falta. Null cuando no se puede: el llamador
/// dibuja NoAuth y reintenta despues. El token viejo nunca se borra acá.
/// </summary>
public static string AccessToken()
{
    DiscordToken token = Load();
    if (token == null) return null;
    if (!token.NeedsRenewal) return token.Access;

    DiscordToken renewed = TryRefresh(token);
    if (renewed == null)
    {
        Log.Write("Discord: refresh fallido, se conserva el token anterior");
        return token.ExpiresUtc > DateTime.UtcNow ? token.Access : null;
    }

    Save(renewed);
    return renewed.Access;
}

private static DiscordToken TryRefresh(DiscordToken token)
{
    return null;   // Task 4
}
```

- [ ] **Step 6: Correr los tests**

Run: `.\build.ps1; .\bin\DiscordTests.exe; echo "exit=$LASTEXITCODE"`
Expected: `20/20 pasaron`, `exit=0`.

Si `FailedRenewalKeepsOldToken` falla, no seguir. Es la garantía entera del rediseño.

- [ ] **Step 7: Commit**

```bash
git add src/DiscordAuth.cs test/DiscordTests.cs build.ps1
git commit -m "feat: DPAPI-backed Discord token store that survives failed renewal"
```

---

### Task 4: Exchange y refresh contra la API de Discord

**Files:**
- Modify: `src/DiscordAuth.cs`
- Modify: `tools/DiscordProbe.cs`

**Interfaces:**
- Consumes: `DiscordToken`, `DiscordAuth.Save/Load` del Task 3.
- Produces:
  - `static DiscordToken DiscordAuth.Exchange(string code, string redirectUri)`
  - `TryRefresh` implementado de verdad.
  - `static string DiscordAuth.RedirectUri` — constante `"http://localhost"`.

- [ ] **Step 1: Escribir la llamada HTTP**

Agregar a `src/DiscordAuth.cs`:

```csharp
using System.Net;

internal const string TokenEndpoint = "https://discord.com/api/v10/oauth2/token";
internal const string RedirectUri = "http://localhost";

internal static readonly string[] Scopes =
{
    "identify", "rpc", "rpc.voice.read", "rpc.voice.write",
    "rpc.screenshare.read", "rpc.screenshare.write"
};

public static DiscordToken Exchange(string code, string redirectUri)
{
    return PostToken(
        "grant_type=authorization_code" +
        "&code=" + Uri.EscapeDataString(code) +
        "&redirect_uri=" + Uri.EscapeDataString(redirectUri));
}

private static DiscordToken TryRefresh(DiscordToken token)
{
    if (token == null || string.IsNullOrEmpty(token.Refresh)) return null;
    try
    {
        return PostToken("grant_type=refresh_token&refresh_token=" +
                         Uri.EscapeDataString(token.Refresh));
    }
    catch (Exception ex)
    {
        Log.Write("Discord: refresh fallo: " + ex.Message);
        return null;
    }
}

private static DiscordToken PostToken(string grant)
{
    if (string.IsNullOrEmpty(_clientId) || string.IsNullOrEmpty(_clientSecret))
        throw new InvalidOperationException("faltan CLIENT ID y CLIENT SECRET");

    // El default de .NET Framework 4 es TLS 1.0 y discord.com corta la conexion.
    // El enum puede no exponer Tls12 segun el framework contra el que compile csc.
    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;

    string body = grant +
                  "&client_id=" + Uri.EscapeDataString(_clientId) +
                  "&client_secret=" + Uri.EscapeDataString(_clientSecret);
    byte[] payload = Encoding.UTF8.GetBytes(body);

    var request = (HttpWebRequest)WebRequest.Create(TokenEndpoint);
    request.Method = "POST";
    request.ContentType = "application/x-www-form-urlencoded";
    request.ContentLength = payload.Length;
    request.Timeout = 15000;
    using (Stream stream = request.GetRequestStream()) stream.Write(payload, 0, payload.Length);

    using (var response = (HttpWebResponse)request.GetResponse())
    using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
    {
        var body_ = Serializer.Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
        string access = Json.Str(body_, "access_token");
        if (string.IsNullOrEmpty(access)) throw new Exception("respuesta sin access_token");

        return new DiscordToken
        {
            Access = access,
            Refresh = Json.Str(body_, "refresh_token"),
            Scope = Json.Str(body_, "scope"),
            ExpiresUtc = DateTime.UtcNow.AddSeconds(Json.Int(body_, "expires_in", 604800))
        };
    }
}
```

Sumar `using System.IO;` si no está.

- [ ] **Step 2: Verificar que los tests del Task 3 siguen pasando**

Run: `.\build.ps1; .\bin\DiscordTests.exe; echo "exit=$LASTEXITCODE"`
Expected: `20/20 pasaron`, `exit=0`.

`FailedRenewalKeepsOldToken` sigue pasando porque `PostToken` lanza cuando faltan credenciales y `TryRefresh` lo atrapa devolviendo null. Ese es el camino que el test cubre.

- [ ] **Step 3: Commit**

```bash
git add src/DiscordAuth.cs
git commit -m "feat: exchange and refresh Discord OAuth tokens over HTTPS"
```

---

### Task 5: Alta por RPC — el task que contesta el riesgo de scopes

Este es el gate del plan. Si Discord no otorga `rpc.screenshare.write` a una app nueva, se descubre acá, con dos módulos escritos, no con la UI entera hecha.

**Files:**
- Modify: `src/DiscordAuth.cs`
- Modify: `tools/DiscordProbe.cs`

**Interfaces:**
- Consumes: `DiscordRpc.Connect/Request/Close` (Task 2), `DiscordAuth.Exchange` (Task 4).
- Produces: `static bool DiscordAuth.Enroll(string clientId, string clientSecret, out string error)`.

- [ ] **Step 1: Implementar `Enroll`**

```csharp
/// <summary>
/// Alta: AUTHORIZE por RPC, y el code que devuelve se canjea por HTTPS.
///
/// Abre su propia conexion RPC y la cierra al terminar. Discord rechaza un
/// segundo AUTHORIZE sobre una conexion ya autenticada con "Already
/// authenticated", que es lo que dejo muerto al plugin de HotSpot.
/// </summary>
public static bool Enroll(string clientId, string clientSecret, out string error)
{
    error = null;
    SetClientCredentials(clientId, clientSecret);

    var rpc = new DiscordRpc();
    if (!rpc.Connect(clientId))
    {
        error = "Discord no esta corriendo";
        return false;
    }

    try
    {
        var args = new Dictionary<string, object>
        {
            { "client_id", clientId },
            { "scopes", Scopes },
            { "prompt", "none" }
        };

        Dictionary<string, object> data = rpc.Request("AUTHORIZE", args, 60000);
        string code = Json.Str(data, "code");
        if (string.IsNullOrEmpty(code)) { error = "AUTHORIZE no devolvio code"; return false; }

        DiscordToken token = Exchange(code, RedirectUri);
        Save(token);
        Log.Write("Discord: alta ok, scopes concedidos: " + token.Scope);
        return true;
    }
    catch (DiscordRpcException ex)
    {
        error = "RPC " + ex.Code + ": " + ex.Message;
        return false;
    }
    catch (Exception ex)
    {
        error = ex.Message;
        return false;
    }
    finally
    {
        rpc.Close();
    }
}
```

- [ ] **Step 2: Agregar los comandos al probe**

En `tools/DiscordProbe.cs`, agregar al `switch` y como métodos:

```csharp
private static int Enroll(string[] args)
{
    if (args.Length < 3)
    {
        Console.WriteLine("uso: DiscordProbe.exe enroll <client-id> <client-secret>");
        return 2;
    }

    string error;
    if (!DiscordAuth.Enroll(args[1], args[2], out error))
    {
        Console.WriteLine("alta fallida: " + error);
        return 1;
    }

    DiscordToken token = DiscordAuth.Load();
    Console.WriteLine("alta ok");
    Console.WriteLine("  vence:  " + token.ExpiresUtc.ToLocalTime());
    Console.WriteLine("  refresh: " + (string.IsNullOrEmpty(token.Refresh) ? "NO" : "si"));
    Console.WriteLine("  scopes: " + token.Scope);

    foreach (string wanted in DiscordAuth.Scopes)
    {
        bool granted = (token.Scope ?? string.Empty).Contains(wanted);
        Console.WriteLine("    " + (granted ? "ok  " : "NO  ") + wanted);
    }
    return 0;
}

private static int Status()
{
    DiscordToken token = DiscordAuth.Load();
    if (token == null) { Console.WriteLine("sin credenciales"); return 1; }

    Console.WriteLine("vence:   " + token.ExpiresUtc.ToLocalTime());
    Console.WriteLine("renueva: " + (token.NeedsRenewal ? "ahora" : "todavia no"));
    Console.WriteLine("scopes:  " + token.Scope);
    return 0;
}
```

- [ ] **Step 3: Crear la app de Discord**

Manual, una sola vez:
1. https://discord.com/developers/applications → **New Application**.
2. **OAuth2 → Redirects**: agregar `http://localhost`.
3. Copiar CLIENT ID y CLIENT SECRET.

- [ ] **Step 4: Correr el gate**

Run: `.\build.ps1; .\bin\DiscordProbe.exe enroll <client-id> <client-secret>`

Discord muestra el diálogo de consentimiento la primera vez. Aceptar.

Expected: `alta ok`, `refresh: si`, y las seis líneas de scopes.

**Este es el punto de decisión del plan.** Leer la lista de scopes concedidos:

- Los seis en `ok` → seguir al Task 6 tal cual está escrito.
- `rpc.screenshare.*` en `NO`, el resto `ok` → **parar y avisar.** Deafen sigue con app propia; screen share necesita el client de StreamKit y otro camino de token. Es un cambio de diseño, no de implementación: vuelve al spec antes de tocar el Task 6.
- `rpc.*` en `NO` → parar y avisar. Las dos teclas van por StreamKit y las Reglas 1 y 2 pasan a aplicarse sobre `AUTHORIZE prompt:none`.

- [ ] **Step 5: Verificar el refresh de verdad**

Forzar la renovación con un token vencido a mano:

```powershell
.\bin\DiscordProbe.exe status
# anotar la expiracion, después:
.\bin\DiscordProbe.exe refresh
.\bin\DiscordProbe.exe status
```

Agregar el comando `refresh` al probe, que llama `DiscordAuth.AccessToken()` e imprime si devolvió token.
Expected: la expiración se corre siete días hacia adelante.

- [ ] **Step 6: Commit**

```bash
git add src/DiscordAuth.cs tools/DiscordProbe.cs
git commit -m "feat: enroll against Discord RPC on a fresh connection"
```

---

### Task 6: Sesión compartida

**Files:**
- Create: `src/DiscordService.cs`
- Modify: `tools/DiscordProbe.cs`
- Modify: `build.ps1`

**Interfaces:**
- Consumes: `DiscordRpc`, `DiscordAuth.AccessToken()`.
- Produces:
  - `enum DiscordLink { NoDiscord, NoAuth, Ready }`
  - `static DiscordLink DiscordService.State { get; }`
  - `static bool DiscordService.Deaf { get; }`, `static bool DiscordService.ScreenSharing { get; }`
  - `static void DiscordService.SetDeaf(bool)`, `static void DiscordService.ToggleScreenShare()`
  - `static event Action DiscordService.Changed`
  - `static void DiscordService.Poke()` — intento de conexión, idempotente, llamado desde el tick.

- [ ] **Step 1: Implementar el servicio**

`src/DiscordService.cs`:

```csharp
// Una conexion de Discord para las dos teclas.
//
// Conectar y autenticar es I/O bloqueante; corre en su propio thread y el
// resultado vuelve por el evento Changed, que las acciones traducen a un
// redibujo desde el hilo del host.

using System;
using System.Collections.Generic;
using System.Threading;

namespace StreamDeckAudio
{
    internal enum DiscordLink { NoDiscord, NoAuth, Ready }

    internal static class DiscordService
    {
        private static readonly object Gate = new object();
        private static DiscordRpc _rpc;
        private static bool _connecting;
        private static DateTime _nextAttemptUtc = DateTime.MinValue;

        public static DiscordLink State { get; private set; }
        public static bool Deaf { get; private set; }
        public static bool ScreenSharing { get; private set; }

        public static event Action Changed;

        private static void Announce()
        {
            Action handler = Changed;
            if (handler != null) handler();
        }

        /// <summary>
        /// Intento de conexion. Idempotente y barato: lo llama el tick de cada
        /// segundo, y sale enseguida si ya hay sesion o si toca esperar.
        /// </summary>
        public static void Poke()
        {
            lock (Gate)
            {
                if (_rpc != null || _connecting) return;
                if (DateTime.UtcNow < _nextAttemptUtc) return;
                _connecting = true;
            }

            var worker = new Thread(Attach) { IsBackground = true, Name = "discord-attach" };
            worker.Start();
        }

        private static void Attach()
        {
            try
            {
                string token = DiscordAuth.AccessToken();
                if (string.IsNullOrEmpty(token)) { Fail(DiscordLink.NoAuth); return; }

                var rpc = new DiscordRpc();
                if (!rpc.Connect(DiscordAuth.ClientId)) { Fail(DiscordLink.NoDiscord); return; }

                try
                {
                    rpc.Request("AUTHENTICATE",
                                new Dictionary<string, object> { { "access_token", token } }, 10000);
                }
                catch (Exception ex)
                {
                    Log.Write("Discord: AUTHENTICATE fallo: " + ex.Message);
                    rpc.Close();
                    Fail(DiscordLink.NoAuth);
                    return;
                }

                rpc.Disconnected += Detach;
                rpc.Subscribe("VOICE_SETTINGS_UPDATE", OnVoiceSettings);
                rpc.Subscribe("SCREENSHARE_STATE_UPDATE", OnScreenShare);

                try { OnVoiceSettings(rpc.Request("GET_VOICE_SETTINGS", null, 10000)); }
                catch (Exception) { }

                lock (Gate) { _rpc = rpc; _connecting = false; }
                State = DiscordLink.Ready;
                Log.Write("Discord: sesion lista");
                Announce();
            }
            catch (Exception ex)
            {
                Log.Write("Discord: conexion fallo: " + ex.Message);
                Fail(DiscordLink.NoDiscord);
            }
        }

        // Backoff para no golpear el pipe una vez por segundo cuando Discord
        // esta cerrado, que es la mayor parte del tiempo.
        private static void Fail(DiscordLink state)
        {
            lock (Gate) { _connecting = false; _nextAttemptUtc = DateTime.UtcNow.AddSeconds(15); }
            if (State == state) return;
            State = state;
            Announce();
        }

        private static void Detach()
        {
            lock (Gate) { _rpc = null; _nextAttemptUtc = DateTime.UtcNow.AddSeconds(5); }
            State = DiscordLink.NoDiscord;
            Log.Write("Discord: sesion caida");
            Announce();
        }

        private static void OnVoiceSettings(Dictionary<string, object> data)
        {
            if (data == null) return;
            Deaf = Json.Bool(data, "deaf", Deaf);
            Announce();
        }

        private static void OnScreenShare(Dictionary<string, object> data)
        {
            if (data == null) return;
            ScreenSharing = Json.Bool(data, "active", ScreenSharing);
            Announce();
        }

        public static void SetDeaf(bool on)
        {
            Send(rpc => rpc.Request("SET_VOICE_SETTINGS",
                                    new Dictionary<string, object> { { "deaf", on } }, 5000));
        }

        public static void ToggleScreenShare()
        {
            Send(rpc => rpc.Request("TOGGLE_SCREENSHARE", null, 5000));
        }

        // Los comandos salen en un thread aparte: el hilo del host no puede
        // quedarse esperando a Discord.
        private static void Send(Action<DiscordRpc> work)
        {
            DiscordRpc rpc;
            lock (Gate) { rpc = _rpc; }
            if (rpc == null) { Poke(); return; }

            var worker = new Thread(() =>
            {
                try { work(rpc); }
                catch (Exception ex) { Log.Write("Discord: comando fallo: " + ex.Message); }
            }) { IsBackground = true };
            worker.Start();
        }
    }
}
```

`SCREENSHARE_STATE_UPDATE` no tiene contrato documentado; se lee `active` y, si el campo viniera con otro nombre, el probe del Task 5 lo muestra. Confirmarlo en el Step 3 antes de dar el task por cerrado.

- [ ] **Step 2: Agregar `ClientId` a `DiscordAuth`**

El servicio necesita el client id de la app propia, que hoy sólo se pasa a `Enroll`. Guardarlo junto al token:

```csharp
public static string ClientId { get { return _clientId; } }
```

Y persistirlo: agregar `clientId` y `clientSecret` al record que `Save` escribe y `Load` lee, y que `Load` los meta en `_clientId`/`_clientSecret`. Así el backend arranca con credenciales sin que el Property Inspector se abra.

- [ ] **Step 3: Verificar contra Discord**

Agregar al probe un comando `watch` que llame `Poke()`, se suscriba a `Changed` e imprima estado, `Deaf` y `ScreenSharing` cada vez que cambie.

Run: `.\bin\DiscordProbe.exe watch`
Con Discord abierto y una llamada activa, tocar deafen y compartir pantalla desde Discord.
Expected: cada cambio imprime una línea. Confirma que ambos eventos llegan y que el campo de screenshare se llama como se asumió.

- [ ] **Step 4: Commit**

```bash
git add src/DiscordService.cs src/DiscordAuth.cs tools/DiscordProbe.cs build.ps1
git commit -m "feat: shared Discord session behind both keys"
```

---

### Task 7: Iconos

**Files:**
- Modify: `tools/GenIcons.cs`

**Interfaces:**
- Consumes: nada.
- Produces: `images/headphones-deaf.png`, `images/share.png`, `images/share-off.png`. `headphones.png` ya existe y se reusa.

- [ ] **Step 1: Agregar los dibujos**

En `tools/GenIcons.cs`, junto a las llamadas de `mixer`/`mixer-muted` (líneas 90-104), agregar:

```csharp
Write(Path.Combine(dir, "headphones-deaf.png"), 144,
      g => { DrawHeadphones(g); DrawSlash(g); });
Write(Path.Combine(dir, "share.png"), 144, DrawShare);
Write(Path.Combine(dir, "share-off.png"), 144,
      g => { DrawShare(g); DrawSlash(g); });
```

`DrawHeadphones` ya existe (produce `headphones.png`); confirmar el nombre real leyendo la línea 90. `mixer-muted` ya dibuja una barra sobre `mixer`: reusar esa misma función de tachado en vez de escribir otra. Si es un lambda inline y no un método con nombre, extraerlo a `DrawSlash` y hacer que `mixer-muted` lo use también.

`DrawShare`: un rectángulo de monitor con una flecha saliendo hacia arriba a la derecha, en el mismo peso de trazo que `DrawScreens`.

- [ ] **Step 2: Generar y mirar**

Run: `.\build.ps1`
Expected: los tres PNG nuevos en `plugin\com.knupwns.audioknob.sdPlugin\images\`.

Abrirlos. A 144 px tienen que leerse igual de nítidos que `mixer-muted.png` al lado.

- [ ] **Step 3: Commit**

```bash
git add tools/GenIcons.cs plugin/com.knupwns.audioknob.sdPlugin/images/
git commit -m "feat: artwork for the Discord deafen and screen share keys"
```

---

### Task 8: Las dos acciones

**Files:**
- Create: `src/ActionsDiscord.cs`
- Modify: `src/PluginHost.cs:169-180`
- Modify: `plugin/com.knupwns.audioknob.sdPlugin/manifest.json`
- Modify: `build.ps1`

**Interfaces:**
- Consumes: `DiscordService`, `ActionBase`, `KeyImage.Glyph`.
- Produces: `DiscordDeafenAction` (`com.knupwns.audioknob.discorddeafen`), `DiscordScreenShareAction` (`com.knupwns.audioknob.discordscreenshare`).

- [ ] **Step 1: Escribir las acciones**

`src/ActionsDiscord.cs`:

```csharp
// Teclas de Discord: enmudecer los auriculares y compartir pantalla.
//
// Las dos comparten una sola sesion; ver DiscordService. Lo que cada una
// aporta es el comando que manda y el tile que dibuja.

using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDeckAudio
{
    internal abstract class DiscordActionBase : ActionBase
    {
        private readonly Dictionary<string, string> _rendered =
            new Dictionary<string, string>(StringComparer.Ordinal);

        protected override bool DefaultsToKnob { get { return false; } }
        public override bool WantsTick { get { return Contexts.Count > 0; } }

        public override void WillDisappear(string context)
        {
            base.WillDisappear(context);
            _rendered.Remove(context);
        }

        // El tick sirve para dos cosas: empujar la reconexion y recoger el
        // cambio de estado que llego desde el thread del RPC.
        public override void Tick()
        {
            DiscordService.Poke();
            foreach (string context in Contexts.Keys.ToList()) Render(context);
        }

        protected abstract string Artwork();
        protected abstract string Caption();

        protected override void Render(string context)
        {
            try
            {
                string art;
                string caption;

                switch (DiscordService.State)
                {
                    case DiscordLink.NoDiscord: art = "headphones-deaf"; caption = "Sin Discord"; break;
                    case DiscordLink.NoAuth:    art = "headphones-deaf"; caption = "Sin permiso"; break;
                    default:                    art = Artwork();         caption = Caption();     break;
                }

                // Redibujar un tile que no cambio inundaria el socket una vez
                // por segundo, por tecla.
                string key = art + "|" + caption;
                string previous;
                if (_rendered.TryGetValue(context, out previous) && previous == key) return;
                _rendered[context] = key;

                Host.SetImage(context, KeyImage.Glyph(art, caption, null, OnKnob(context)));
                Host.SetTitle(context, string.Empty);
            }
            catch (Exception ex)
            {
                Log.Write("DiscordAction.Render fallo: " + ex.Message);
            }
        }
    }

    internal sealed class DiscordDeafenAction : DiscordActionBase
    {
        public override string Uuid { get { return "com.knupwns.audioknob.discorddeafen"; } }

        public override void Press(string context)
        {
            if (DiscordService.State != DiscordLink.Ready) { Host.ShowAlert(context); return; }
            DiscordService.SetDeaf(!DiscordService.Deaf);
        }

        protected override string Artwork()
        {
            return DiscordService.Deaf ? "headphones-deaf" : "headphones";
        }

        protected override string Caption()
        {
            return DiscordService.Deaf ? "Mudo" : "Audio";
        }
    }

    internal sealed class DiscordScreenShareAction : DiscordActionBase
    {
        public override string Uuid { get { return "com.knupwns.audioknob.discordscreenshare"; } }

        public override void Press(string context)
        {
            if (DiscordService.State != DiscordLink.Ready) { Host.ShowAlert(context); return; }
            DiscordService.ToggleScreenShare();
        }

        protected override string Artwork()
        {
            return DiscordService.ScreenSharing ? "share" : "share-off";
        }

        protected override string Caption()
        {
            return DiscordService.ScreenSharing ? "En vivo" : "Pantalla";
        }
    }
}
```

Los tres estados de `DiscordLink` se distinguen por el texto, no por color: una sola tinta para todas las filas.

- [ ] **Step 2: Registrar las acciones**

En `src/PluginHost.cs`, después de la línea 180 (`Register(new MediaPlayerAction());`):

```csharp
Register(new DiscordDeafenAction());
Register(new DiscordScreenShareAction());
```

- [ ] **Step 3: Agregar al manifest**

En `plugin/com.knupwns.audioknob.sdPlugin/manifest.json`, dentro de `Actions`:

```json
{
  "UUID": "com.knupwns.audioknob.discorddeafen",
  "Name": "Discord Deafen",
  "Tooltip": "Enmudece los auriculares en Discord",
  "Icon": "images/action",
  "Controllers": ["Keypad"],
  "PropertyInspectorPath": "discord.html",
  "States": [{ "Image": "images/headphones", "TitleAlignment": "middle" }]
},
{
  "UUID": "com.knupwns.audioknob.discordscreenshare",
  "Name": "Discord Screen Share",
  "Tooltip": "Comparte o corta la pantalla en Discord",
  "Icon": "images/action",
  "Controllers": ["Keypad"],
  "PropertyInspectorPath": "discord.html",
  "States": [{ "Image": "images/share-off", "TitleAlignment": "middle" }]
}
```

Un solo state por acción: el dibujo lo maneja `SetImage`, igual que `appvolume`. Copiar la forma exacta de los campos de una acción Keypad que ya funcione (`blackout`) antes de inventar ninguno.

- [ ] **Step 4: Agregar las fuentes a `build.ps1`**

Sumar `'src\DiscordRpc.cs'`, `'src\DiscordAuth.cs'`, `'src\DiscordService.cs'`, `'src\ActionsDiscord.cs'` a las fuentes de `DeckTools.exe`, y `System.Security.dll` a sus `-Refs`.

- [ ] **Step 5: Compilar, instalar y probar en el deck**

Run: `.\build.ps1; .\install-plugin.ps1 -Restart`

Arrastrar las dos acciones a un perfil. Verificar, en este orden:
1. Con Discord cerrado: las dos dicen `Sin Discord`.
2. Abrir Discord, esperar hasta 15 s: pasan a `Audio` y `Pantalla`.
3. Apretar deafen: Discord se enmudece y el tile pasa a `Mudo`.
4. Sacar el deafen **desde Discord**: el tile vuelve a `Audio` solo. Esto prueba la suscripción, que es lo que el plugin de HotSpot no hacía.
5. Apretar screen share en una llamada: comparte y el tile pasa a `En vivo`.

- [ ] **Step 6: Commit**

```bash
git add src/ActionsDiscord.cs src/PluginHost.cs build.ps1 plugin/com.knupwns.audioknob.sdPlugin/manifest.json
git commit -m "feat: Discord deafen and screen share keys"
```

---

### Task 9: Property Inspector

**Files:**
- Create: `plugin/com.knupwns.audioknob.sdPlugin/discord.html`
- Modify: `src/ActionsDiscord.cs`

**Interfaces:**
- Consumes: `DiscordAuth.Enroll`, `DiscordAuth.Load`.
- Produces: comandos `getStatus` y `authorize` sobre `FromPropertyInspector`.

- [ ] **Step 1: Escribir el HTML**

Copiar la estructura de `ssh.html` (Keypad, campos de texto, sin listas dinámicas) y adaptarla: dos `input` — CLIENT ID y CLIENT SECRET — un botón **Autorizar**, y un `div` de estado.

El botón manda `{"command":"authorize","clientId":…,"clientSecret":…}` por `sendToPlugin`. Al abrirse manda `{"command":"getStatus"}`. La respuesta del backend llega por `sendToPropertyInspector` con `{"status":"…","expires":"…"}` y se pinta en el div.

Incluir el enlace a `https://discord.com/developers/applications` y la instrucción de registrar `http://localhost` como Redirect: sin eso el exchange falla y el mensaje de Discord no dice por qué.

- [ ] **Step 2: Manejar los comandos en el backend**

En `DiscordActionBase`:

```csharp
public override void PropertyInspectorAppeared(string context)
{
    PushStatus(context);
}

public override void FromPropertyInspector(string context, Dictionary<string, object> payload)
{
    switch (Json.Str(payload, "command"))
    {
        case "getStatus":
            PushStatus(context);
            return;

        case "authorize":
            string clientId = Json.Str(payload, "clientId");
            string secret = Json.Str(payload, "clientSecret");

            // Enroll bloquea mientras el usuario mira el dialogo de Discord.
            var worker = new System.Threading.Thread(() =>
            {
                string error;
                bool ok = DiscordAuth.Enroll(clientId, secret, out error);
                Host.Invoke(() =>
                {
                    if (!ok) Log.Write("Discord: alta fallida: " + error);
                    PushStatus(context);
                    Render(context);
                });
            }) { IsBackground = true };
            worker.Start();
            return;
    }

    Dictionary<string, object> settings = Json.Dict(payload, "settings");
    if (settings != null) SettingsChanged(context, settings);
}

private void PushStatus(string context)
{
    DiscordToken token = DiscordAuth.Load();
    string status = token == null ? "sin autorizar" : "autorizado";
    string expires = token == null ? string.Empty : token.ExpiresUtc.ToLocalTime().ToString("g");

    Host.SendToPropertyInspector(context, Uuid,
        "{\"status\":\"" + Json.Escape(status) + "\",\"expires\":\"" + Json.Escape(expires) + "\"}");
}
```

- [ ] **Step 3: Probar el ciclo completo**

Run: `.\build.ps1; .\install-plugin.ps1 -Restart`

1. Borrar `%LOCALAPPDATA%\StreamDeckAudio\discord.dat`.
2. La tecla dice `Sin permiso`.
3. Abrir el Property Inspector: dice `sin autorizar`.
4. Pegar CLIENT ID y SECRET, **Autorizar**. Aceptar el diálogo de Discord.
5. El PI pasa a `autorizado` con la fecha de vencimiento, y la tecla pasa a `Audio`.

- [ ] **Step 4: Commit**

```bash
git add plugin/com.knupwns.audioknob.sdPlugin/discord.html src/ActionsDiscord.cs
git commit -m "feat: property inspector for Discord credentials"
```

---

### Task 10: Documentación

**Files:**
- Modify: `README.md`
- Modify: `docs/superpowers/specs/2026-08-27-discord-rpc-actions-design.md`

- [ ] **Step 1: Documentar las dos acciones en el README**

Seguir el formato de las acciones que ya están. Incluir el alta de una sola vez (crear app, registrar `http://localhost`, pegar ID y secret) y que el token se renueva solo.

Agregar una línea sobre `DiscordProbe.exe`: es la herramienta de diagnóstico cuando una tecla no responde, y `status` dice si el token está vivo.

- [ ] **Step 2: Cerrar el spec**

Cambiar `**Estado:** diseñado` por `**Estado:** implementado (2026-XX-XX)`.

Si el Task 5 mostró que los scopes de screenshare no se otorgan a una app propia, escribir el resultado real en la sección de Riesgos: es la información más cara de todo el plan y la que un lector futuro va a buscar primero.

- [ ] **Step 3: Commit**

```bash
git add README.md docs/superpowers/specs/2026-08-27-discord-rpc-actions-design.md
git commit -m "docs: Discord keys and the probe"
```
