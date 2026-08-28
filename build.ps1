# Builds everything with the C# compiler that ships inside Windows.
# No SDK, no NuGet, no downloads.
#
#   .\build.ps1

$ErrorActionPreference = 'Stop'

$root   = $PSScriptRoot
$csc    = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$plugin = Join-Path $root 'plugin\com.knupwns.audioknob.sdPlugin'
$bin    = Join-Path $root 'bin'

if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }
New-Item -ItemType Directory -Force $bin | Out-Null
New-Item -ItemType Directory -Force (Join-Path $plugin 'images') | Out-Null

function Invoke-Csc {
    param([string] $Out, [string[]] $Sources, [string] $Target = 'exe', [string[]] $Refs = @())

    $arguments = @('/nologo', "/target:$Target", '/platform:x64', '/optimize+', "/out:$Out")
    foreach ($r in $Refs) { $arguments += "/reference:$r" }
    foreach ($s in $Sources) { $arguments += (Join-Path $root $s) }

    & $csc @arguments
    if ($LASTEXITCODE -ne 0) { throw "compilation of $Out failed ($LASTEXITCODE)" }
    Write-Output "  built $(Split-Path $Out -Leaf)"
}

Write-Output 'compiling:'

# The media session API is WinRT. There is no SDK here, so the type metadata is
# taken from the copy Windows itself ships; System.Runtime.dll has to come along
# or the winmd cannot resolve System.Attribute and nothing compiles.
$winmd = 'C:\Windows\System32\WinMetadata'
foreach ($n in 'Windows.Foundation', 'Windows.Media', 'Windows.Storage', 'Windows.ApplicationModel') {
    if (-not (Test-Path (Join-Path $winmd "$n.winmd"))) { throw "missing $n.winmd in $winmd" }
}
$winrt = @(
    (Join-Path $winmd 'Windows.Foundation.winmd')
    (Join-Path $winmd 'Windows.Media.winmd')
    (Join-Path $winmd 'Windows.Storage.winmd')
    (Join-Path $winmd 'Windows.ApplicationModel.winmd')
    'System.Runtime.dll'
)

# Plugin backend: one process serves every action in the manifest.
Invoke-Csc -Out (Join-Path $plugin 'DeckTools.exe') -Target 'winexe' `
           -Sources @('src\AudioCore.cs', 'src\AudioSessions.cs', 'src\Monitors.cs', 'src\Metrics.cs',
                      'src\Shortcuts.cs', 'src\Network.cs', 'src\Labels.cs', 'src\KeyImage.cs',
                      'src\Media.cs', 'src\Json.cs', 'src\PluginHost.cs', 'src\ActionsAudio.cs', 'src\ActionsSystem.cs',
                      'src\ActionsLauncher.cs', 'src\ActionsNetwork.cs', 'src\ActionsSsh.cs',
                      'src\ActionsRemote.cs', 'src\ActionsRustDesk.cs',
                      'src\ActionsSeparator.cs', 'src\ActionsMedia.cs') `
           -Refs (@('System.Web.Extensions.dll', 'System.Drawing.dll') + $winrt)

# Blackout overlay, started by the plugin as a separate process because it needs
# its own message loop.
Invoke-Csc -Out (Join-Path $plugin 'Blackout.exe') -Target 'winexe' `
           -Sources @('tools\Blackout.cs') `
           -Refs @('System.Windows.Forms.dll', 'System.Drawing.dll')

# Command-line companion, handy for checking the audio backend on its own.
Invoke-Csc -Out (Join-Path $bin 'AudioToggle.exe') -Target 'winexe' `
           -Sources @('src\AudioCore.cs', 'src\AudioSessions.cs', 'src\Labels.cs', 'src\AudioToggle.cs')

# Harness that emulates the host software.
Invoke-Csc -Out (Join-Path $bin 'FakeApp.exe') `
           -Sources @('src\AudioCore.cs', 'src\AudioSessions.cs', 'src\Monitors.cs',
                      'src\Labels.cs', 'test\FakeApp.cs')

# Tests offline. No necesitan Discord ni el deck.
Invoke-Csc -Out (Join-Path $bin 'DiscordTests.exe') `
           -Sources @('src\AudioCore.cs', 'src\Json.cs', 'src\DiscordRpc.cs',
                      'test\Assert.cs', 'test\DiscordTests.cs') `
           -Refs @('System.Web.Extensions.dll')

# Companion de consola para el backend de Discord.
Invoke-Csc -Out (Join-Path $bin 'DiscordProbe.exe') `
           -Sources @('src\AudioCore.cs', 'src\Json.cs', 'src\DiscordRpc.cs',
                      'src\DiscordAuth.cs', 'tools\DiscordProbe.cs') `
           -Refs @('System.Web.Extensions.dll', 'System.Security.dll')

# Icon generator, then the icons themselves.
$genIcons = Join-Path $bin 'GenIcons.exe'
Invoke-Csc -Out $genIcons -Sources @('tools\GenIcons.cs') -Refs @('System.Drawing.dll')

Write-Output 'generating icons:'
& $genIcons (Join-Path $plugin 'images') `
            (Join-Path $root 'iconpack\com.knupwns.decktools.sdIconPack') `
            (Join-Path $root 'tools')
if ($LASTEXITCODE -ne 0) { throw "icon generation failed ($LASTEXITCODE)" }

# Left behind by versions before the backend was renamed.
Remove-Item (Join-Path $plugin 'AudioKnob.exe') -Force -ErrorAction SilentlyContinue

Write-Output ''
Write-Output "plugin ready: $plugin"
Write-Output 'next: .\install-plugin.ps1 -Restart'
