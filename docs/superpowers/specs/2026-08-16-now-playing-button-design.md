# Now Playing — botón de play/pausa con artista y canción

**Fecha:** 2026-08-16
**Estado:** implementado (2026-08-16)
**Action nueva:** `com.knupwns.audioknob.media`

## Problema

El pack no tiene ningún control de reproducción. Se quiere una tecla que:

1. Haga play/pausa del reproductor que esté sonando.
2. Muestre artista y/o canción, cada uno con su propio mostrar/ocultar y su
   propia posición (arriba o abajo).
3. Cuando un texto no entre en el ancho del tile, **oscile de izquierda a
   derecha** en vez de achicar la fuente.
4. Agrande o achique el icono central para dejar el lugar que ocupen los textos.
5. Muestre glyph de **play cuando está pausado** y de **pausa cuando está
   reproduciendo** — el icono anuncia la acción, no el estado.

## Viabilidad ya verificada

`csc.exe` de `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\` compila contra
WinRT sin SDK ni NuGet, referenciando el metadata que trae el propio sistema
operativo en `C:\Windows\System32\WinMetadata`:

```
/reference:C:\Windows\System32\WinMetadata\Windows.Foundation.winmd
/reference:C:\Windows\System32\WinMetadata\Windows.Media.winmd
/reference:C:\Windows\System32\WinMetadata\Windows.Storage.winmd
/reference:C:\Windows\System32\WinMetadata\Windows.ApplicationModel.winmd
/reference:System.Runtime.dll
```

`System.Runtime.dll` es obligatorio: sin él, `Windows.Foundation.winmd` falla con
`CS0012: The type 'System.Attribute' is defined in an assembly that is not
referenced`. Se resuelve por nombre simple desde el directorio del compilador; no
hace falta ruta absoluta ni reference assemblies (esta máquina no los tiene).

**`await` no sirve.** Las extensiones `GetAwaiter`/`AsTask` de
`System.Runtime.WindowsRuntime.dll` no bindean contra el winmd del sistema
(`CS4028`), porque la identidad de tipo de `IAsyncOperation<T>` no coincide entre
el metadata del SO y el que espera la facade. Referenciar la facade desde el GAC
tampoco lo arregla. El `IAsyncOperation` se drena a mano:

```csharp
private static T Wait<T>(IAsyncOperation<T> op, int timeoutMs)
{
    var done = new ManualResetEventSlim(false);
    op.Completed = delegate { done.Set(); };
    if (!done.Wait(timeoutMs)) throw new TimeoutException("WinRT call did not complete");
    return op.GetResults();
}
```

Probado end-to-end: detecta `Spotify.exe`, `PlaybackStatus = Playing`, artista y
título correctos. `RequestAsync` tardó 8 ms y `TryGetMediaPropertiesAsync` 3 ms.

## Fuente de datos

`Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager`, siempre
la **sesión actual** (`GetCurrentSession()`). Sin configuración de reproductor: el
botón sigue solo a lo que Windows considere que está sonando — Spotify, YouTube
en Chrome, VLC, lo que sea. No hay selector de app en el Property Inspector.

Sin sesión: glyph de play en tono apagado y sin textos. La tecla no hace nada y
muestra el alert del host.

**Una fila vacía no reserva su ranura.** `HasSession` no alcanza para decidirlo:
sigue en `true` con Spotify abierto y parado, o con una pestaña de Chrome que ya
reprodujo algo, así que el tile quedaba chico con las dos bandas en blanco. La
regla es por fila — si el texto está vacío, la fila no se dibuja ni ocupa lugar,
y el icono se queda con lo que sobre. Sin artista ni canción, eso es el tile
entero: 128 px.

## Layout

Tile de 144×144. Sólo Keypad, así que se usa el alto completo (`floor = 140`,
`top = 4`) y no aplica la banda muerta de las perillas.

Cuatro ranuras de texto de 30 px: dos arriba y dos abajo. Artista y Canción se
ubican según su posición; si las dos caen en la misma banda, se apilan.

```
0 textos          1 arriba          2 arriba          1 arriba 1 abajo    2 abajo
┌──────────┐      ┌──────────┐      ┌──────────┐      ┌──────────┐      ┌──────────┐
│          │      │ Artista  │      │ Artista  │      │ Artista  │      │          │
│          │      │          │      │ Canción  │      │          │      │  ICONO   │
│  ICONO   │      │  ICONO   │      │          │      │  ICONO   │      │   68px   │
│  128px   │      │   98px   │      │  ICONO   │      │   68px   │      │          │
│          │      │          │      │   68px   │      │          │      │ Artista  │
│          │      │          │      │          │      │ Canción  │      │ Canción  │
└──────────┘      └──────────┘      └──────────┘      └──────────┘      └──────────┘
```

**Orden dentro de una banda:** Artista siempre arriba de Canción. Es una regla
fija, no depende del orden en que se toquen los controles del PI; así el
resultado es predecible y no hay estado escondido.

**Constantes:**

| | |
|---|---|
| `Size` | 144 |
| `TileTop` / `TileFloor` | 4 / 140 |
| `RowHeight` | 30 |
| `IconGap` | 4 |
| `SidePadding` | 7 (el que ya usa `KeyImage`) |

**Ranuras.** Con `topCount` filas arriba y `bottomCount` abajo:

- fila `i` de arriba: `y = TileTop + i * RowHeight`
- fila `j` de abajo (0 = la de más arriba de ese grupo):
  `y = TileFloor - (bottomCount - j) * RowHeight`

**Rectángulo del icono** — lo que sobra:

```
iconTop    = TileTop   + topCount    * RowHeight + IconGap
iconBottom = TileFloor - bottomCount * RowHeight - IconGap
side       = min(iconBottom - iconTop, Size - 2 * 8)
```

El icono es cuadrado, `side` de lado, centrado horizontalmente en x=72 y
verticalmente dentro de `[iconTop, iconBottom]`. Da 128 / 98 / 68 px para los
casos del diagrama.

**Glyphs vectoriales.** Play y pausa se trazan en `KeyImage` con `GraphicsPath`,
no se cargan de un PNG. Un PNG fijo se pixela al escalar a 128 px; el trazo queda
nítido a cualquier `side`.

- Play: triángulo equilátero apuntando a la derecha, inscrito en el cuadrado con
  ~18% de margen, esquinas redondeadas para acompañar el resto del pack.
- Pausa: dos barras verticales redondeadas, ancho 26% del lado cada una, 16% de
  separación.
- Relleno con el mismo degradado oscuro→claro de izquierda a derecha que usan las
  barras de `DrawStat`, para mantener el look iluminado de un lado.
- Sin sesión: el mismo glyph de play relleno en `Muted` plano, sin degradado.

**Color de los textos.** Los dos en `Ink` (`#F2F4F6`). Fuente Segoe UI Bold 20 px,
fija: no se autoajusta, porque el punto del marquee es justamente no achicar la
letra.

> Revisado el 2026-08-16. La primera versión ponía el artista en `Muted`
> (`#9AA1A7`) para copiar la jerarquía de `DrawStat`. A tamaño igual el gris se
> lee como una tipografía más chica y más fina que el blanco, así que las dos
> filas parecían de estilos distintos sin serlo. Ahora son idénticas.

## Marquee

Por cada fila visible se mide el ancho del texto a 20 px. El ancho útil de la
banda es `Size - 2 * SidePadding` = 130 px.

- **Entra** (`width <= 130`): se dibuja centrado y quieto. Esa fila no anima.
- **No entra**: se dibuja recortado a la banda (`Graphics.SetClip`) y desplazado
  por un offset horizontal que oscila entre `0` y `-(width - 130)`.

Máquina de estados por fila, avanzada un paso por frame:

```
HoldStart (10 frames) → ScrollLeft (offset -= speed, hasta -overflow)
   → HoldEnd (10 frames) → ScrollRight (offset += speed, hasta 0) → HoldStart
```

Las pausas de 10 frames (~1 s) en cada extremo existen para poder leer las
puntas. Cada fila corre su propia máquina con su propio `overflow`, así que van
desfasadas — es lo esperado, no un bug.

El offset se resetea a `HoldStart` cuando cambia el texto de esa fila.

## Loop de animación

Hoy `PluginHost.cs:237` levanta un solo ticker de 1 s que comparten todos los
actions. Un marquee a 1 fps es inusable.

**Decisión: tick rápido opt-in.** Un segundo hilo ticker a 100 ms encola un
`FastTickJob` en el `Inbox`; el pump lo despacha a `FastTickAll()`, que recorre
los actions y llama `FastTick()` sólo en los que devuelven `WantsFastTick`.

- `ActionBase` gana `public virtual bool WantsFastTick { get { return false; } }`
  y `public virtual void FastTick() { }`. Ningún action existente cambia.
- `MediaPlayerAction.WantsFastTick` es true sólo mientras alguna fila de algún
  contexto esté desbordada. Se recalcula en cada render y se guarda en un campo,
  para no medir texto desde el chequeo.
- El hilo encola siempre, sin condiciones. Un objeto por `BlockingCollection` cada
  100 ms con un `FastTickAll()` que sale por un `if` es costo despreciable, y
  evita el problema de leer `Contexts` desde otro hilo. Toda la disciplina actual
  se mantiene: GDI+, COM y el socket siguen corriendo únicamente en el hilo STA
  del pump.

**Sólo se emite `setImage` cuando el frame cambió de verdad.** El action guarda
por contexto una `_lastFrameKey` armada con estado de reproducción, los dos
textos, la geometría y los offsets. Si la clave nueva es igual, no se renderiza ni
se manda nada. Durante las pausas en los extremos, y con textos que entran, el
tráfico es cero. Esto también deduplica el tick de 1 s.

Alternativas descartadas:

- Bajar el ticker global a 100 ms: obliga a que cada action se autolimite por
  intervalo y multiplica por 10 los despertares del pump para todos.
- Hilo propio del action: rompe la regla de que GDI+ y el socket viven sólo en el
  hilo STA.

**Riesgo conocido, sin mitigación en este spec.** El marquee corre en el mismo
pump STA que todo lo demás. Si hay un tile de sysmon en el mismo deck y `Metrics`
se cuelga leyendo `MainModule` de un proceso con anti-cheat, el pump se bloquea y
el marquee se congela ese rato. Es un problema preexistente del host, pero recién
acá se vuelve visible. Queda documentado como limitación; arreglarlo es otro
trabajo.

## Lectura de la sesión

`Media.cs` no bloquea el pump. Patrón de lector único:

- En cada tick de 1 s, si no hay lectura en vuelo, se encola una al thread pool.
- La lectura obtiene el manager (una sola vez, cacheado), `GetCurrentSession()`,
  `GetPlaybackInfo()` y `TryGetMediaPropertiesAsync()` drenado con `Wait(...)` y
  timeout de 3 s.
- El resultado vuelve al hilo STA con `Host.Invoke(...)`, que lo guarda y
  redibuja.

Medido en 3-8 ms, así que en la práctica nunca hay dos en vuelo; el guard existe
para el caso en que un reproductor colgado no conteste.

`Press` llama `TryTogglePlayPauseAsync()` también en background. No espera al
próximo tick: apenas vuelve, fuerza una relectura y redibuja, así el glyph cambia
al toque. Si no hay sesión o `IsPlayPauseToggleEnabled` es false, `ShowAlert`.

Superficie de `Media.cs`:

```csharp
internal struct MediaState
{
    public bool HasSession;
    public string Artist;
    public string Title;
    public bool Playing;
    public bool CanToggle;
}

internal static class Media
{
    public static MediaState Read();          // bloqueante, sólo desde background
    public static bool TogglePlayPause();     // bloqueante, sólo desde background
}
```

## Settings

| Clave | Tipo | Default | Notas |
|---|---|---|---|
| `artistShow` | bool | `true` | |
| `artistPos` | string | `"top"` | `"top"` \| `"bottom"` |
| `titleShow` | bool | `true` | |
| `titlePos` | string | `"bottom"` | `"top"` \| `"bottom"` |
| `speed` | int | `3` | px por frame: 2 lenta, 3 normal, 5 rápida |

## Property Inspector

`mediaplayer.html`, con el markup y las clases de `sysmon.html`. No hace falta
tocar `pi.js`: ya expone `checkbox()` y `radios()`, que es todo lo que se usa.

```
Artista    [x] Mostrar    (•) Arriba  ( ) Abajo
Canción    [x] Mostrar    ( ) Arriba  (•) Abajo
Velocidad  [ Normal ▾ ]
```

Arriba están los defaults. El select de velocidad tiene tres entradas —
`Lenta` = 2, `Normal` = 3, `Rápida` = 5 — y guarda el número, no la etiqueta.

Los radios de posición se deshabilitan cuando el check correspondiente está
apagado. Nota al pie explicando que un texto que no entre oscila en vez de
achicarse, y que el icono se acomoda a lo que quede libre.

## Manifest

```json
{
  "Icon": "images/media",
  "Name": "Now Playing",
  "States": [{ "Image": "images/media", "TitleAlignment": "top", "FontSize": "10" }],
  "Controllers": ["Keypad"],
  "SupportedInMultiActions": true,
  "Tooltip": "Play or pause whatever is playing, with the artist and title on the tile",
  "UUID": "com.knupwns.audioknob.media",
  "PropertyInspectorPath": "mediaplayer.html"
}
```

`States` tiene un solo estado. El plugin dibuja su propia imagen, así que el
play/pausa no pasa por `setState` — se resuelve en el render, igual que hace
`BlackoutAction` con `screens` / `screens-off`.

`OS.MinimumVersion` ya dice `10`, que cubre el requisito de SMTC (Windows 10
1809+). No hace falta cambiarlo.

## Archivos

**Nuevos**

- `src/Media.cs` — envoltorio de SMTC.
- `src/ActionsMedia.cs` — `MediaPlayerAction`.
- `plugin/com.knupwns.audioknob.sdPlugin/mediaplayer.html` — PI.

**Modificados**

- `src/KeyImage.cs` — `NowPlaying(...)`, glyphs vectoriales de play/pausa, dibujo
  de fila con clip y offset.
- `src/PluginHost.cs` — `WantsFastTick` / `FastTick()` en `ActionBase`, hilo
  ticker de 100 ms, `FastTickAll()`, y `Register(new MediaPlayerAction())`.
- `plugin/com.knupwns.audioknob.sdPlugin/manifest.json` — la entrada de arriba.
- `build.ps1` — `src\Media.cs` y `src\ActionsMedia.cs` en las fuentes de
  `DeckTools.exe`, más las cinco referencias nuevas.
- `tools/GenIcons.cs` — `DrawMedia` → `images/media.png` para la lista de
  actions.
- `README.md` — documentar la action.

`KeyImage.cs` ya está en 512 líneas y le entran un renderer más y los glyphs sin
quedar desbalanceado respecto de los otros archivos del repo. Si al implementarlo
el bloque de marquee se pasa de ~120 líneas, se corta a `src/Marquee.cs` con la
máquina de estados sola, que es una unidad testeable de por sí.

## Testing

`test/FakeApp.cs` es el harness que emula al host. Lo verificable sin deck:

1. **Geometría** — para cada una de las 5 combinaciones de posición, que
   `iconTop`, `iconBottom` y `side` den los valores de la tabla, y que las
   ranuras no se pisen.
2. **Máquina del marquee** — con un `overflow` dado, que la secuencia de offsets
   recorra `HoldStart → ScrollLeft → HoldEnd → ScrollRight` y vuelva a 0 sin
   pasarse de los extremos; que cambiar el texto la resetee.
3. **Desborde** — que un texto que entra en 130 px no anime y no emita frames.
4. **Dedup de frames** — que dos ticks seguidos sin cambios no llamen `SetImage`.
5. **Sin sesión** — que renderice el play apagado y no tire excepción.
6. **Render visual** — volcar los PNG de las 5 combinaciones a `bin/` y mirarlos.

La lectura real de SMTC se valida a mano con Spotify sonando: play/pausa desde la
tecla, y que artista y canción cambien al pasar de tema.

## Fuera de alcance

- Elegir reproductor. Siempre la sesión activa.
- Pista anterior/siguiente, y soporte de perilla.
- Carátula del álbum de fondo.
- Barra de progreso de la canción.
- Arreglar el bloqueo del pump por `Metrics`/`MainModule`.
