# Deck Tools — plugin for Redragon Stream Station (SS-552)

A `.sdPlugin` for the Redragon Stream Station software (a MiraBox / HotSpot
StreamDock build with an Elgato-compatible SDK).

| Action | Controller | Rotate | Press |
|---|---|---|---|
| **Audio Output Knob** | knob | system volume | swap default output between two devices |
| **App Volume Knob** | knob | one application's own volume | mute that application |
| **Screen Brightness Knob** | knob | real backlight over DDC/CI | toggle a dim preset |
| **Launcher Wheel** | knob | scroll a folder of shortcuts | open the one shown |
| **Host Wake & Watch** | key | — | wake a machine over the network |
| **SSH Connect** | key | — | open an SSH session to a machine in a terminal |
| **Remote Monitor** | key + knob | — | read the machine now |
| **RustDesk Connect** | key | — | open a saved RustDesk peer |
| **Separator** | key | — | nothing, on purpose |
| **Blackout Screens** | key | — | black overlay on every monitor, or displays off |
| **System Monitor** | key + knob | cycle metric | open Task Manager |

Every tile draws its own artwork and label, so the value you care about is on the
key itself: the live output device, the app's volume, the panel's brightness, or
a metric with a sparkline.

## Install

```powershell
.\build.ps1                      # compile + generate icons
.\install-plugin.ps1 -Restart    # copy into the plugins folder, restart the app
```

Then drag the actions onto knobs or keys. The plugin folder is still called
`com.knupwns.audioknob.sdPlugin` on purpose — renaming it would orphan action
instances already placed in a profile.

## The actions

### Audio Output Knob

Pick **Device A** and **Device B** in the panel; pressing swaps between them.
Leave them unset and a press cycles every active output. Devices that are powered
off or disconnected never appear and are skipped.

The stock `switchAudio` plugin that ships with the software can only be placed on
a key: its `manifest.json` declares no `Controllers`, so the software never offers
it for a knob. This one declares `"Controllers": ["Knob"]` and handles
`dialRotate` / `dialPress`. Knob only, deliberately — half of the action is
rotation, so it must not be offered for a plain key.

### App Volume Knob

Targets either a fixed application or **whatever window is in focus**. Matching is
by process *name*, not pid: Chrome, Discord and Edge spread their audio over
several processes and a user who picks "Discord" means all of it. Apps holding no
audio session show `--`.

Process names are internal — `WhatsApp.Root`, `msedgewebview2` — so the label
comes from the shortest sensible candidate among the process name, the
executable's file description and its product name, after stripping `.exe`,
bitness suffixes and trailing plumbing after a dot:

```
WhatsApp.Root  -> WhatsApp     chrome          -> Chrome
msedgewebview2 -> WebView2     steamwebhelper  -> Steam
```

`bin\AudioToggle.exe --apps` prints the list exactly as the panel shows it.

### Screen Brightness Knob

Drives the monitor's actual backlight through DDC/CI — the same channel the
monitor's own buttons use. Support is per-panel and often switched off in the
OSD: on this machine two of three monitors answer and the third returns
`ERROR_GEN_FAILURE`, so those are listed as **no DDC** and skipped. Pressing
drops to the dim level and pressing again restores the previous brightness.

### Launcher Wheel

Point it at a folder; rotating scrolls its contents one item at a time, showing
each with **its own icon** and name, and pressing opens the one on screen and
remembers it for next time. One knob replaces a page of launcher keys.

Anything Windows can open works — `.lnk`, `.url`, executables — sorted by name,
with `%USERPROFILE%`-style variables expanded. Resolving the artwork is the
interesting part, because a shortcut has no icon of its own:

| Kind | Where the icon comes from |
|---|---|
| `.lnk` | `IShellLink.GetIconLocation`, falling back to the target from `GetPath` |
| `.url` | the `IconFile` / `IconIndex` keys in the file |
| anything else | the file itself |

Whatever that resolves to is then pulled out at 256px with `PrivateExtractIcons`
and cached per path, so spinning the knob costs nothing. Items with no icon at all
get the pack's shortcut glyph instead. The folder listing is cached for a few
seconds — long enough that a fast spin never hits the disk, short enough that a
newly added shortcut appears on its own; the panel's button forces a re-read.

Icons keep their real colours here rather than being restyled: recognising the app
at a glance matters more than matching the pack.

### Host Wake & Watch

A live tile for one machine: its round-trip time while it answers, `DOWN` when it
does not, and `WAKE` for 75 seconds after a wake packet — during that window it is
polled every 3 seconds so you watch it come up. The sparkline is latency against a
200 ms ceiling, so a LAN box sits flat and something across a VPN visibly rides
higher.

Pressing wakes it when it is down and re-checks when it is already up, rather than
shouting at a machine that is plainly awake.

The magic packet is the standard 102 bytes — six `0xFF` then the MAC sixteen times
— sent to the limited broadcast address *and* to every local subnet's directed
broadcast, because which of the two a given router or switch forwards varies. The
target's own address is added as well for setups that answer there.

**Check port** at `0` pings. Set it to `22` or `3389` for a host that drops ICMP
but serves something: a firewalled machine would otherwise always read as asleep.

Probes run on a pool thread and come back through `IHost.Invoke`, which queues work
onto the thread every handler shares. A ping that times out would otherwise freeze
every other action on the deck for the best part of a second.

### SSH Connect

One machine per key: the padlock with its name above and, underneath, the
round-trip time in green while it is accepting SSH or `DOWN` in red while it is
not. Pressing opens the session.

What gets dialled is the SSH port itself rather than ICMP, so a box that answers
a ping but has no `sshd` reads as down — which is the thing this key actually
cares about. **Watch host** turns the probing off for a plain launch key.

A host that is down is still dialled on a press: the connection error belongs in
the terminal, where it can be read, not swallowed here.

**Open in** picks how the session appears — Windows Terminal (the default),
`ssh.exe` in its own console window, PowerShell, Command Prompt, or a whole
command line of your own with `{host}`, `{user}`, `{port}` and `{target}`
substituted in. That last one is where an identity file, a jump host or PuTTY
goes. Nothing is passed through a shell, so a quoted path is the only escaping
anything needs.

Host and user may only contain letters, digits, dots, dashes and underscores.
They end up on a command line, so they are held to what a hostname can legitimately
contain instead of being escaped after the fact; anything else is refused.

This replaces a folder of one-line `.bat` files, each hard-coding a host and
launching `wt.exe ssh`: the same three keys are now three instances of one action
that also tell you whether the machine is up before you press.

### Remote Monitor

The System Monitor, pointed at a machine across the network. Tick as many of CPU,
RAM, temperature, disk and uptime as you want and they share the one key: a row
each, label left, figure right, a bar underneath. Tick a single one instead and it
gets the whole tile — big value and a sparkline of where it has been. Past four
rows the bars are dropped and the rows go text-only, which is the point at which
they stop being readable across a desk anyway.

**Host name** can be turned off. Sat directly above or below an **SSH Connect**
key for the same machine, the name is already on the deck, and the caption costs a
whole row of figures. Pressing reads the machine straight away instead of waiting
for the interval.

Each figure and its bar are coloured by what they are saying: green, amber, red.
CPU is allowed to run higher than the rest before it counts as anything — a
machine at 80% CPU is working, while a disk at 80% is a problem you have not had
yet — and temperature is pitched at a Raspberry Pi, which throttles itself at 80.

| | amber at | red at |
|---|---|---|
| CPU | 75% | 90% |
| RAM, disk | 80% | 92% |
| Temperature | 65 °C | 80 °C |

Uptime is left in the ordinary ink: it has no bad value, and colouring it would
only suggest it does.

Every tile pointed at the same machine shares one reading. Four keys watching
`pi4` are one SSH connection per interval, not four, which matters because the
Windows OpenSSH client has no `ControlMaster` — each poll is a whole handshake,
and on a Raspberry Pi over the LAN that runs 1 to 6 seconds. Intervals start at 5
seconds for the same reason.

`DOWN` means the machine did not answer with a reading; the log says whether it
was unreachable, refused the key, or has no `deckstats` installed. Uptime is the
one metric drawn without a sparkline: it has no ceiling to plot against.

#### Setting a host up

```
.\remote\install-deckstats.ps1 -Target youruser@pi3.lan
```

It installs `remote/deckstats.sh` to `~/.local/bin/deckstats` on the target — no
sudo, nothing outside your own home directory — and authorises a dedicated key:

```
restrict,command="/home/youruser/.local/bin/deckstats" ssh-ed25519 AAAA... decktools
```

`restrict` turns off port forwarding, agent forwarding, X11 and the pty; the
forced command means the key cannot run anything else. That is what makes an
unencrypted key on the deck machine acceptable: a copy of it is worth one line of
numbers, not a shell. Your existing keys are untouched, and `-Remove` undoes it.

The script is POSIX `sh` and `awk` against `/proc` and `df`, so it needs nothing
installed on a stock Raspberry Pi image. `remote/deckstats.ps1` is the Windows
equivalent, reading the same figures through CIM.

Two things the installer learned the hard way, both preserved as checks rather
than comments:

- `ssh-keygen -N '""'` in PowerShell 7 creates a key whose passphrase is two
  literal quote characters. Under `BatchMode` that fails as an ordinary
  `Permission denied (publickey)`, which is a long way from the truth. It must be
  `-N ""`, and the generated key is verified openable before anything is
  installed.
- Verification connects with `-T` and a command of its own, against the address
  `ssh -G` resolves rather than the alias you typed. Without `-T`, a host that
  lets some *other* key through unrestricted opens an interactive shell and the
  script sits inside it forever. The command it sends is one the forced command
  should swallow: if it comes back, the restriction is not in effect and the
  installer says so instead of reporting success.

### RustDesk Connect

One saved peer per key. Paste the ID with or without the spaces RustDesk shows it
in; with no name of its own the tile prints it grouped the way RustDesk does.
Pressing runs `rustdesk.exe --connect <id>` and gets out of the way, so the
password prompt and any saved credentials are the ones you already have.

Turning **Label** off gives the mark on its own at full size. The artwork ships in
two sizes for exactly this: one fitted to the band the hand-drawn glyphs occupy,
so a captioned key sits at the same scale as **Host Wake & Watch** next to it, and
one filling the box the icon pack centres its glyphs in.

`NO APP` on the tile means the executable is not where the key thinks it is —
worth saying up front, since a key that looks perfectly fine right up until it
does nothing is the worse failure. The path defaults to
`C:\Program Files\RustDesk\rustdesk.exe` and expands environment variables.

This replaces a `.bat` that hard-coded one peer ID.

### Separator

A bar the length of the tile, and a press that does nothing. That is the whole
feature: an empty key in a profile is still a key, and one that launches something
when your thumb lands on it by mistake is worse than a bar. **Vertical** divides
columns, **horizontal** divides rows.

Keys only — a knob hides the bottom of its screen, which would cut the bar short.
Both bars are in the icon pack as well, for putting the same divider on a key this
plugin does not own.

### Blackout Screens

**Black overlay** puts a black window on every monitor and keeps the machine
awake — press the key again, or any key or click, to dismiss. **Displays off**
asks Windows to power the panels down instead; any input wakes them.

The overlay is a separate process (`Blackout.exe`): the plugin backend owns a
single STA thread for COM and its event pump, and a window message loop cannot
live there.

### System Monitor

CPU, RAM, GPU, VRAM, disk, network or temperature, refreshed on its own timer,
with a sparkline of recent samples. Everything comes from APIs Windows already
exposes — no driver, no ring-0 helper, no third-party library:

| Metric | Source |
|---|---|
| CPU | `GetSystemTimes` deltas |
| RAM | `GlobalMemoryStatusEx` |
| GPU | `GPU Engine` counters, `engtype_3D` instances summed |
| VRAM | `GPU Adapter Memory\Dedicated Usage` of the adapter holding the most |
| Disk | `PhysicalDisk(_Total)\% Disk Time` |
| Network | `Network Interface\Bytes Total/sec` |
| Temperature | ACPI thermal zone |

That last one is a **system** reading, not a CPU or GPU sensor: Windows exposes
no per-chip temperature without a kernel driver. It is labelled `TEMP` rather
than pretending otherwise.

### Now Playing

Play/pause for whatever is sounding, with the artist and the track name on the
key. It reads the transport controls every media app registers with Windows —
the same ones behind the volume flyout — so Spotify, a YouTube tab, VLC and the
rest all work without being configured, and without a hotkey or a script in
sight.

The glyph says what the **press** will do, not what the player is doing: a pause
bar while it plays, a play triangle while it is stopped. Greyed out when nothing
is playing at all.

Artist and track each have their own show/hide and their own side of the key:

```
0 lines           artist top        both top          split             both bottom
┌──────────┐      ┌──────────┐      ┌──────────┐      ┌──────────┐      ┌──────────┐
│          │      │ Artist   │      │ Artist   │      │ Artist   │      │          │
│          │      │          │      │ Track    │      │          │      │  GLYPH   │
│  GLYPH   │      │  GLYPH   │      │          │      │  GLYPH   │      │   68px   │
│  128px   │      │   98px   │      │  GLYPH   │      │   68px   │      │          │
│          │      │          │      │   68px   │      │          │      │ Artist   │
│          │      │          │      │          │      │ Track    │      │ Track    │
└──────────┘      └──────────┘      └──────────┘      └──────────┘      └──────────┘
```

The glyph takes whatever room the lines leave, so it grows when you hide one and
shrinks when both land on the same side. It is traced at runtime rather than
loaded from a PNG, which is what lets it go from 68 px to 128 px without turning
to mush. When both lines share a band the artist is always the upper of the two.

A line too long for the key **scrolls left and right** instead of shrinking to
fit — the font size never changes — pausing about a second at each end so the
ends can be read. Three speeds in the property inspector.

Only a line that actually overflows animates, and a frame is only sent when the
key would genuinely look different, so a track whose text fits costs nothing
between songs. The scrolling runs on a second 100 ms ticker that stays idle
unless something is moving; see [How it works](#how-it-works).

> The marquee shares the plugin's single pump thread with every other action. If
> a System Monitor key on the same deck stalls reading a process guarded by
> anti-cheat, the scrolling freezes for as long as that takes.

### Discord Deafen & Screen Share

Two native keys against Discord's own RPC pipe (`\\.\pipe\discord-ipc-0`),
replacing the equivalent keys from HotSpot's third-party plugin
(`com.hotspot.streamdock.discord.sdPlugin`). **Deafen** mutes what you
*hear* — Discord's deafen, not a microphone mute. **Screen Share** toggles
Go Live. Both keys share one RPC connection and one Property Inspector
(`discord.html`); authorizing from either key's panel authorizes both.

Screen Share always opens Discord's own screen/window picker. The command
behind the key, `TOGGLE_SCREENSHARE`, takes no arguments, and no other
screenshare command exists on Discord's RPC surface, so there is no way to
hand it a specific window directly. Automating the picker itself — clicking
Discord's dialog, sending keystrokes — is the kind of OS-level hack this
project avoids, so the dialog stays.

#### One-time setup

1. Create an app at [discord.com/developers/applications](https://discord.com/developers/applications).
2. Under **OAuth2 → General**, add `http://localhost` under **Redirects**.
   The token exchange checks it even though the authorization code arrives
   over RPC and no browser redirect ever actually happens.
3. Paste the app's **Client ID** and **Client Secret** into either key's
   Property Inspector and press **Autorizar**. Discord shows a consent
   dialog the first time.

From there the token renews itself — the whole point of the feature. The
third-party plugin these keys replace has no refresh token: its token dies
every 7 days and it cannot recover on its own once that happens. These keys
hold their own `refresh_token`, renew at 80% of the token's life rather than
waiting for it to expire, and write the new token to disk before discarding
the old one — a failed renewal attempt never leaves the key without a usable
credential, it just retries on the next tick.

`bin\DiscordProbe.exe` is the diagnostic tool when a key stops responding:

```powershell
.\bin\DiscordProbe.exe status    # is a token stored, and when does it expire
.\bin\DiscordProbe.exe connect   # is Discord's RPC pipe reachable at all
.\bin\DiscordProbe.exe watch     # print deaf / screensharing state changes live
```

## Icon pack

The same artwork is also shipped as an `.sdIconPack`, so the icons show up in the
software's icon library and can be dropped on **any** key by hand, plugin or not.
It installs alongside the packs that come with the app:

```
%APPDATA%\HotSpot\StreamDock\icons\com.knupwns.decktools.sdIconPack
```

| Icon | Tag |
|---|---|
| Headphones, Headphones muted, Speakers, App volume, App muted | audio |
| Brightness, Screens, Blackout screens, Share screen, Stop sharing | display |
| System stats | system |
| Knob | function |
| Claude, RustDesk, Remote desktop | apps |
| Monogram | custom |

Pack icons are the same glyphs redrawn **centred and full-tile** — the plugin's
own key images deliberately leave bands empty for the caption and value, which
would look off-centre as a general-purpose icon.

**Brand and app marks are never drawn from memory.** Two folders feed the pack
automatically, so adding a service is a matter of dropping a file in and
rebuilding:

| Folder | Contents | How it is used |
|---|---|---|
| `tools/brands/` | single-path SVG outlines | parsed and filled with the pack gradient |
| `tools/brands-art/` | PNG logos | traced to a silhouette, then filled the same way |

The SVG reader in `tools/GenIcons.cs` handles move, line, horizontal, vertical,
cubic, smooth cubic, elliptical arc and close, absolute and relative — which is
what real brand outlines use. Arcs are converted from SVG's endpoint form to
centre form and approximated with quarter-turn cubics, since GDI+ has no rotated
elliptical arc. The flag pair in an arc needs its own reader: `a5 5 0 015 5` is
`0`, `1`, `5`, `5`, not a number `015`.

Marks that ship with the pack come from [Simple Icons](https://simpleicons.org)
(CC0 outlines; the marks themselves belong to their owners). `SSH.png` is flagged
`TitleRoom`, which fits it below the caption band instead of centring it on the
tile — those keys are usually labelled with a hostname.

Three other icons are not hand-drawn geometry either:

- **Claude.png** renders the real mark's outline. Its path (via Simple Icons,
  CC0) is vendored as `tools/claude.svg` and filled with the pack's gradient by a
  small SVG path reader in `tools/GenIcons.cs`. Drawing it from memory produced
  something that only looked vaguely like a starburst; the outline is all straight
  segments, so rendering the real thing costs nothing.
- **RustDesk.png** is drawn here, echoing the shape of their mark in this pack's
  style rather than copying their artwork: one thick ring with a single diagonal
  slit through it. Two separate arcs look wrong — in the real mark the four cut
  edges are parallel, not radial, because it is one cut.
- **Monogram.png** is traced from `tools/monogram.png`: the build separates ink
  from background, keeps every blob within an order of magnitude of the largest
  one (dropping the nebula's speckles while preserving the letterform's own
  pieces and holes), and paints the pack's gradient through that mask.

  Which test finds the ink depends on the art, and getting it wrong is visible:
  a plain luminance cut erased the descender of the monogram's **P**, because the
  lower stem is warm but darker than the rest of the stroke. Artwork on a dark
  background is therefore found by warmth, and a mark on a light plate by its
  distance from white.

## How it works

The software launches the plugin as its own process:

```
DeckTools.exe -port <n> -pluginUUID <uuid> -registerEvent registerPlugin -info <json>
```

`DeckTools.exe` opens `ws://127.0.0.1:<port>`, registers, and routes every event
to the handler whose action UUID matches. It answers with `setImage`, `setState`,
`setSettings` and `sendToPropertyInspector`. COM, GDI+ and all handlers run on one
STA thread; the receive loop and the two tickers only enqueue work onto it.

There are two tickers. The once-a-second one drives every live tile. A second one
at 100 ms exists for the Now Playing marquee: it enqueues unconditionally, and
the pump skips it in a single `if` unless an action reports `WantsFastTick`, so
the decision is made on the thread that owns the actions' state rather than from
the timer. Nothing else in the plugin asks for it.

Three things cost real debugging time and are worth knowing:

**WinRT from a plain `csc.exe`.** The media session API is WinRT, and there is no
Windows SDK involved in this build. The type metadata comes from the copy Windows
ships in `C:\Windows\System32\WinMetadata`, and `System.Runtime.dll` has to be
referenced alongside it or `Windows.Foundation.winmd` cannot resolve
`System.Attribute` and nothing compiles at all. Against that metadata `await` also
does not work: the `GetAwaiter` extensions in `System.Runtime.WindowsRuntime`
refuse to bind, because the identity of `IAsyncOperation<T>` in the OS metadata is
not the one the facade expects — referencing the facade out of the GAC does not
fix it either. Every asynchronous call in `src/Media.cs` is therefore drained by
hand through its `Completed` handler.

**`IPolicyConfig` is gone on new Windows 11 builds.** Changing the default audio
endpoint has no public API. Every tool uses the undocumented `IPolicyConfig` COM
interface, and Windows 11 builds around 26xxx stopped answering `QueryInterface`
for it — only `IPolicyConfigVista` responds. Both are tried in order, so tools
pinned to the old interface simply fail on those builds.

**The host freezes a plugin's `States` into the profile.** Image, font size and
`TitleAlignment` are snapshotted when the action instance is created, and the
manifest is never re-read for instances that already exist:

```json
"States": [
    { "FontSize": "11", "Image": "images/headphones", "TitleAlignment": "bottom" }
]
```

A manifest fix therefore only reaches instances added afterwards. Compositing the
label into the image (`src/KeyImage.cs`) and clearing the host title puts the
layout under the plugin's control for every instance, old or new.

### Knobs hide their bottom strip

The knob screens tilt, so from where the user sits their bottom strip is
foreshortened into invisibility and anything drawn there reads as cut off. The
keys are viewed straight on and have no such problem — so this is a property of
*where an instance landed*, not of the artwork:

```
        knob                        key
 0 ┌──────────────┐        0 ┌──────────────┐
   │   caption    │          │   caption    │
   │    glyph     │          │    glyph     │
   │    value     │          │              │
124 ├──────────────┤          │    value     │
   │  (reserved)  │          │              │
144 └──────────────┘      144 └──────────────┘
```

`willAppear` reports `controller` as `Knob` or `Keypad`, so `ActionBase` records
it per context and `KeyImage` composes accordingly — one artwork set, two
layouts. Baking the offset into the artwork instead raises it on keys too, where
it just looks off-centre.

The generated glyphs occupy y 48–96, which already clears the hidden strip; only
the value beneath them and the tiles drawn wholly at runtime (metric gauges,
launcher icons) have to move. Captions stop at y=40 so they never touch the
artwork below.

Everything compiles with the C# compiler already present in Windows
(`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`) — no SDK, no NuGet, no
downloads. Icons are drawn with GDI+ at build time, so no binary assets are
checked in. They follow the Blade icon pack that ships with the software: black
tile, chunky filled silhouette, one brushed-chrome gradient down the whole glyph.

## Layout

```
src/AudioCore.cs        output devices, default endpoint, system volume
src/AudioSessions.cs    per-application volume and mute
src/Monitors.cs         DDC/CI brightness
src/Metrics.cs          system metric sampling
src/Shortcuts.cs        folder listing, shortcut icon resolution, launching
src/Labels.cs           shortening device and process names for a 72px key
src/KeyImage.cs         composes key artwork: glyph + caption + value + sparkline
src/PluginHost.cs       WebSocket transport, event routing, ticker
src/ActionsAudio.cs     output switcher, app volume
src/ActionsSystem.cs    brightness, blackout, system monitor
src/ActionsLauncher.cs  launcher wheel
src/Network.cs          wake-on-LAN packets and reachability probes
src/ActionsNetwork.cs   host wake & watch
src/AudioToggle.cs      CLI companion for diagnostics
test/FakeApp.cs         harness that impersonates the host software
tools/GenIcons.cs       icon generator: plugin artwork and the icon pack
tools/claude.svg        vendored outline, rendered into the pack
tools/monogram.png      source art, traced into the pack
tools/Blackout.cs       full-screen overlay process
iconpack/com.knupwns.decktools.sdIconPack/
    manifest.json, icons.json, icons/   all generated by build.ps1
plugin/com.knupwns.audioknob.sdPlugin/
    manifest.json       the five action definitions
    pi.css, pi.js       shared property-inspector glue
    *.html              one property inspector per action
    DeckTools.exe       built by build.ps1
    Blackout.exe        built by build.ps1
    images/             generated by build.ps1
```

## Testing without the hardware

`FakeApp.exe` opens a WebSocket server, launches the plugin exactly the way the
host does, feeds it events and checks what actually happened on the system:

```powershell
.\bin\FakeApp.exe .\plugin\com.knupwns.audioknob.sdPlugin\DeckTools.exe
```

```
  [ok]   registered
  [ok]   output: rotate changed the volume
  [ok]   output: press switched A -> B
  [ok]   output: press switched B -> A
  [ok]   output: plugin reported setState
  [ok]   output: key images sent (9 saved)
  [ok]   app volume: rotate moved one app's level
  [ok]   brightness: rotate reached the panel over DDC/CI
  [ok]   system monitor: tile refreshed on its own
RESULT: PASS
```

It restores the volume, the default device, the app's level and the brightness it
started from, and writes every distinct tile to `bin\key-preview-*.png` so the
artwork can be eyeballed. The blackout overlay is not covered — the test would
black out the screens it runs on.

## Diagnostics

```powershell
.\bin\AudioToggle.exe --list          # active outputs, * marks the default
.\bin\AudioToggle.exe --apps          # apps with a session, as the panel lists them
.\bin\AudioToggle.exe --next          # switch to the next one
.\bin\AudioToggle.exe --set "JBL"     # switch to a specific one
```

Logs:

- plugin: `%LOCALAPPDATA%\StreamDeckAudio\audioknob.log`
- host: `%APPDATA%\HotSpot\StreamDock\logs\` — a working load prints
  `Plugin <id> com.knupwns.audioknob.sdPlugin is now connected`

## Uninstall

```powershell
.\install-plugin.ps1 -Remove -Restart
```
