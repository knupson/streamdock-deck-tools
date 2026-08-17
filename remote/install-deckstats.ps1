# Installs the metrics script on a Linux host and authorises a key that can run
# nothing else, so the Remote Monitor tiles can poll it unattended.
#
#   .\remote\install-deckstats.ps1 -Target youruser@pi3.lan
#   .\remote\install-deckstats.ps1 -Target pi4
#   .\remote\install-deckstats.ps1 -Target pi4 -Remove
#
# You will be asked for the host's password once - that is the point of the
# exercise, since afterwards the plugin never needs one. Nothing is installed
# outside your own home directory on the target and no sudo is used.
#
# The key is generated on first run if it is not there yet, and everything is
# idempotent: running it twice replaces the entry rather than adding a second.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Target,
    [string] $KeyPath = (Join-Path $env:USERPROFILE '.ssh\id_decktools'),
    [switch] $Remove
)

$ErrorActionPreference = 'Stop'

$script = Join-Path $PSScriptRoot 'deckstats.sh'
if (-not (Test-Path $script)) { throw "deckstats.sh not found next to this script" }

# The comment is what identifies the entry on the far side, so replacing it later
# does not need to know anything else about the file.
$tag = 'decktools'

function Invoke-Remote {
    param([string] $Shell)

    # Sent as one base64 argument: the remote script is full of quotes, and every
    # layer between here and the far shell would want its own say about them.
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Shell))
    ssh -o ConnectTimeout=10 $Target "echo $encoded | base64 -d | sh"

    if ($LASTEXITCODE -ne 0) { throw "the remote half failed (exit $LASTEXITCODE)" }
}

if ($Remove) {
    Write-Output "removing deckstats from $Target..."

    Invoke-Remote @"
set -e
rm -f "`$HOME/.local/bin/deckstats"
if [ -f "`$HOME/.ssh/authorized_keys" ]; then
    grep -v ' $tag`$' "`$HOME/.ssh/authorized_keys" > "`$HOME/.ssh/authorized_keys.new" || true
    mv "`$HOME/.ssh/authorized_keys.new" "`$HOME/.ssh/authorized_keys"
    chmod 600 "`$HOME/.ssh/authorized_keys"
fi
echo "removed"
"@

    Write-Output "done - the key itself is still at $KeyPath"
    return
}

# A key the plugin can actually use: present, and openable without a passphrase.
function Test-KeyUsable {
    param([string] $Path)

    if (-not (Test-Path $Path)) { return $false }

    $null = ssh-keygen -y -f $Path -P '' 2>&1
    return $LASTEXITCODE -eq 0
}

if (-not (Test-KeyUsable $KeyPath)) {
    if (Test-Path $KeyPath) {
        # PowerShell 7 passes -N '""' through as two literal quote characters, so
        # a key generated that way has a passphrase of "" and fails silently under
        # BatchMode. Kept rather than deleted, in case it is authorised somewhere.
        Write-Warning "$KeyPath cannot be opened without a passphrase - setting it aside as .broken"
        foreach ($suffix in @('', '.pub')) {
            if (Test-Path "$KeyPath$suffix") { Move-Item "$KeyPath$suffix" "$KeyPath$suffix.broken" -Force }
        }
    }

    Write-Output "generating $KeyPath..."

    # No passphrase: the plugin runs unattended and has nobody to ask. What keeps
    # this honest is the restriction on the far side, not a secret here. The empty
    # string must be written as -N "" exactly: -N '""' is a passphrase.
    ssh-keygen -t ed25519 -f $KeyPath -N "" -C $tag -q
    if ($LASTEXITCODE -ne 0) { throw "ssh-keygen failed ($LASTEXITCODE)" }

    if (-not (Test-KeyUsable $KeyPath)) { throw "the generated key still wants a passphrase" }
}

$public = (Get-Content "$KeyPath.pub" -Raw).Trim()
$payload = [Convert]::ToBase64String([IO.File]::ReadAllBytes($script))

Write-Output "installing on $Target (you will be asked for its password)..."

# restrict turns off port forwarding, agent forwarding, X11 and the pty; the
# forced command means this key cannot run anything but the metrics script. A
# copy of it is worth one line of numbers, not a shell.
Invoke-Remote @"
set -e
mkdir -p "`$HOME/.local/bin" "`$HOME/.ssh"
chmod 700 "`$HOME/.ssh"

echo '$payload' | base64 -d > "`$HOME/.local/bin/deckstats"
chmod 755 "`$HOME/.local/bin/deckstats"

touch "`$HOME/.ssh/authorized_keys"
grep -v ' $tag`$' "`$HOME/.ssh/authorized_keys" > "`$HOME/.ssh/authorized_keys.new" || true
printf 'restrict,command="%s/.local/bin/deckstats" %s\n' "`$HOME" '$public' >> "`$HOME/.ssh/authorized_keys.new"
mv "`$HOME/.ssh/authorized_keys.new" "`$HOME/.ssh/authorized_keys"
chmod 600 "`$HOME/.ssh/authorized_keys"

echo "installed to `$HOME/.local/bin/deckstats"
"@

# What the plugin will dial. It has no reason to read your ssh_config - it is
# given a host, a user and a key outright - so the check has to be against the
# resolved address rather than the alias, or it proves the wrong thing.
$effective = ssh -G $Target
$remoteHost = ($effective | Select-String '^hostname ').Line -replace '^hostname ', ''
$remoteUser = ($effective | Select-String '^user ').Line -replace '^user ', ''
$remotePort = ($effective | Select-String '^port ').Line -replace '^port ', ''

$direct = "$remoteUser@$remoteHost"

Write-Output "verifying as the plugin will connect ($direct)..."

# -T and a command of its own, both on purpose. Without -T a target that lets the
# key through unrestricted opens an interactive shell and this sits there waiting
# for a prompt nobody is watching. The command is one the forced command should
# swallow: seeing it come back means some *other* key authenticated - which is
# what happens when the target is an alias whose ssh_config block names an
# IdentityFile, since that key is offered alongside this one and may win.
$reading = (ssh -i $KeyPath -o IdentitiesOnly=yes -o BatchMode=yes -o ConnectTimeout=10 `
                -p $remotePort -T $direct 'echo UNRESTRICTED' 2>&1) -join ' '

if ($LASTEXITCODE -ne 0) { throw "the restricted key did not work (exit $LASTEXITCODE): $reading" }

if ($reading -match 'UNRESTRICTED') {
    throw "$direct let the key run an arbitrary command - the forced command is not in effect"
}

if ($reading -notmatch '^cpu=') { throw "unexpected reply from $direct : $reading" }

Write-Output "  $reading"
Write-Output ''
Write-Output 'ready. In the Remote Monitor key:'
Write-Output "  Host      $remoteHost"
Write-Output "  User      $remoteUser"
if ($remotePort -ne '22') { Write-Output "  Port      $remotePort" }
Write-Output "  Identity  $KeyPath"
