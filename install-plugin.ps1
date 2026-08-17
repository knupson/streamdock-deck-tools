# Installs the plugin into the Redragon Stream Station (StreamDock) plugin
# folder. The software only rescans plugins at startup, so it has to be
# restarted afterwards; -Restart does that for you.
#
#   .\install-plugin.ps1
#   .\install-plugin.ps1 -Restart
#   .\install-plugin.ps1 -Remove -Restart
#
# The host is meant to run elevated - see Start-App for why the volume knob needs
# it. -SetupAutostart puts that in place and -RemoveAutostart undoes it; both need
# an elevated shell and both are opt-in, since they change how Windows starts the
# software rather than anything about this plugin.
#
#   .\install-plugin.ps1 -SetupAutostart
#   .\install-plugin.ps1 -RemoveAutostart

[CmdletBinding()]
param(
    [switch] $Restart,
    [switch] $Remove,
    [switch] $SetupAutostart,
    [switch] $RemoveAutostart
)

$ErrorActionPreference = 'Stop'

$id      = 'com.knupwns.audioknob.sdPlugin'
$source  = Join-Path $PSScriptRoot "plugin\$id"
$dest    = Join-Path $env:APPDATA "HotSpot\StreamDock\plugins\$id"
$packId  = 'com.knupwns.decktools.sdIconPack'
$packSrc = Join-Path $PSScriptRoot "iconpack\$packId"
$packDst = Join-Path $env:APPDATA "HotSpot\StreamDock\icons\$packId"
$appExe  = 'C:\Program Files (x86)\Redragon Stream Station\Redragon Stream Station.exe'
$appName = 'Redragon Stream Station'

# Logon task that starts the host elevated. It replaces the vendor's HKCU Run
# entry, which started it medium-IL; see Start-App for why that matters.
$taskName = 'Redragon Stream Station (elevated)'
$runKey   = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
$runValue = 'Redragon Stream Station'
$appArgs  = '--RunInBackground'

# Kept beside the plugin's log rather than in the repo: it records what this
# machine had before the task replaced it, so the revert is not guesswork.
$runBackup = Join-Path $env:LOCALAPPDATA 'StreamDeckAudio\autostart-before-task.txt'

# The host keeps plugin backends alive as child processes; AudioKnob is the name
# the backend used before it grew past audio.
$helpers = @('DeckTools', 'AudioKnob', 'Blackout')

function Clear-Folder {
    param([string] $Path)

    if (Test-Path $Path) { Get-ChildItem $Path -Force | Remove-Item -Recurse -Force }
    else { New-Item -ItemType Directory -Force $Path | Out-Null }
}

function Stop-App {
    $running = Get-Process -Name $appName -ErrorAction SilentlyContinue
    if ($running) {
        Write-Output 'stopping Redragon Stream Station...'
        $running | Stop-Process -Force
        Start-Sleep -Seconds 2
    }
    foreach ($helper in $helpers) {
        Get-Process -Name $helper -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
    }
}

function Start-App {
    Write-Output 'starting Redragon Stream Station...'

    # The host has to come up elevated, and it must not depend on who runs this
    # script. The volume knob taps the media keys, and UIPI silently discards
    # synthetic input from a medium-IL process while an elevated window - a game -
    # owns the foreground, which costs the Windows volume overlay. A logon task
    # holds that elevation, so start through it rather than inheriting whatever
    # token this shell happens to carry: Start-Process used to leave the host
    # elevated when run from an admin prompt and plain when run from a normal one,
    # which changed the knob's behaviour with nothing in the repo to explain it.
    & schtasks /Query /TN $taskName 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        & schtasks /Run /TN $taskName | Out-Null
    }
    else {
        Write-Warning "scheduled task '$taskName' is missing - starting directly, so the media keys will only reach an elevated foreground if this shell is elevated. Register it with -SetupAutostart"
        Start-Process $appExe -ArgumentList $appArgs
    }

    # It has silently failed to come back before, so confirm rather than assume.
    $up = $null
    foreach ($attempt in 1..15) {
        Start-Sleep -Seconds 1
        $up = Get-Process -Name $appName -ErrorAction SilentlyContinue
        if ($up) { break }
    }
    if ($up) { Write-Output "  running (pid $($up[0].Id))" }
    else { Write-Warning 'the app did not come back up - start it from the Start menu' }
}

function Test-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal $identity).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Replaces the vendor's medium-IL autostart with a logon task that starts the host
# elevated, so the volume knob's media keys survive an elevated foreground.
function Set-Autostart {
    if (-not (Test-Elevated)) {
        throw 'this needs an elevated shell: registering a HighestAvailable task and editing the Run key both do'
    }
    if (-not (Test-Path $appExe)) { throw "the host is not where this script expects it: $appExe" }

    $user    = "$env:USERDOMAIN\$env:USERNAME"
    $workDir = Split-Path $appExe

    # Spelled out rather than assembled with the ScheduledTasks cmdlets, so the
    # whole definition is reviewable here and RunLevel cannot be quietly
    # defaulted. The delay keeps the host from racing USB enumeration at logon.
    $xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Starts the deck software elevated at logon, so the DeckTools plugin can inject the volume media keys (and with them Windows' own volume overlay) even while an elevated application owns the foreground. Replaces the vendor's HKCU Run entry.</Description>
    <URI>\$taskName</URI>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>$user</UserId>
      <Delay>PT15S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>$user</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
    <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>$appExe</Command>
      <Arguments>$appArgs</Arguments>
      <WorkingDirectory>$workDir</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
"@

    # schtasks reads the definition as UTF-16.
    $temp = Join-Path $env:TEMP 'decktools-host-task.xml'
    [IO.File]::WriteAllText($temp, $xml, [Text.Encoding]::Unicode)
    try {
        & schtasks /Create /TN $taskName /XML $temp /F | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "schtasks /Create failed ($LASTEXITCODE)" }
    }
    finally { Remove-Item $temp -Force -ErrorAction SilentlyContinue }
    Write-Output "registered task: $taskName  (at logon, highest privileges, 15s delay)"

    # The Run entry would start a second, medium-IL instance alongside the task's,
    # so it goes - but only after recording it, so the revert restores what this
    # machine actually had instead of what the script assumes.
    $existing = (Get-ItemProperty $runKey -Name $runValue -ErrorAction SilentlyContinue).$runValue
    if ($existing) {
        New-Item -ItemType Directory -Force (Split-Path $runBackup) | Out-Null
        Set-Content -Path $runBackup -Value $existing -Encoding UTF8
        Remove-ItemProperty -Path $runKey -Name $runValue
        Write-Output "removed Run entry '$runValue', saved to $runBackup"
    }
    else { Write-Output "no Run entry '$runValue' to remove" }
}

function Remove-Autostart {
    if (-not (Test-Elevated)) { throw 'this needs an elevated shell' }

    & schtasks /Query /TN $taskName 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        & schtasks /Delete /TN $taskName /F | Out-Null
        Write-Output "deleted task: $taskName"
    }
    else { Write-Output "no task '$taskName' was registered" }

    # The recorded value wins. The literal is a floor, so a lost backup leaves the
    # software still starting at logon rather than not starting at all.
    $value = if (Test-Path $runBackup) { (Get-Content $runBackup -Raw).Trim() }
             else { "$appExe $appArgs" }

    Set-ItemProperty -Path $runKey -Name $runValue -Value $value
    Write-Output "restored Run entry '$runValue' = $value"
    Write-Warning 'the knob loses the Windows volume overlay whenever an elevated window is in front; the volume itself still moves, through the endpoint API'
}

if ($SetupAutostart -and $RemoveAutostart) {
    throw '-SetupAutostart and -RemoveAutostart are opposites - pick one'
}

# Autostart is its own operation: it touches how Windows starts the software, not
# what this repo installs, so it does not ride along with a plugin update.
if ($SetupAutostart) {
    Stop-App
    Set-Autostart
    Start-App
    return
}

if ($RemoveAutostart) {
    Stop-App
    Remove-Autostart
    Write-Output ''
    Write-Output 'start the software from the Start menu, or log back on to let the Run entry do it.'
    return
}

if ($Remove) {
    Stop-App
    foreach ($target in @($dest, $packDst)) {
        if (Test-Path $target) { Remove-Item $target -Recurse -Force; Write-Output "removed: $target" }
        else { Write-Output "nothing installed at $target" }
    }
    if ($Restart) { Start-App }
    return
}

if (-not (Test-Path (Join-Path $source 'DeckTools.exe'))) {
    throw "DeckTools.exe missing - run .\build.ps1 first"
}

Stop-App

# Wiped rather than merged, so files dropped from the plugin do not linger. The
# contents go rather than the folder itself: an indexer or a scanner holding a
# handle on the directory blocks removing it long after the software that put it
# there has exited, and there is no reason to make the install depend on that.
Clear-Folder $dest
Copy-Item (Join-Path $source '*') $dest -Recurse -Force

Write-Output "installed: $dest"
Get-ChildItem $dest -Recurse -File | ForEach-Object {
    Write-Output ("  " + $_.FullName.Replace("$dest\", ''))
}

# The icon pack is independent of the plugin: it just adds the artwork to the
# software's icon library so any key can use it.
if (Test-Path $packSrc) {
    Clear-Folder $packDst
    Copy-Item (Join-Path $packSrc '*') $packDst -Recurse -Force

    $count = (Get-ChildItem (Join-Path $packDst 'icons') -File).Count
    Write-Output "installed icon pack: $packDst ($count icons)"
}

if ($Restart) { Start-App }
else {
    Write-Output ''
    Write-Output 'Restart Redragon Stream Station to pick up the plugin (or re-run with -Restart).'
}
