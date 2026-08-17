# The Windows half of deckstats: the same single line of metrics that
# remote/deckstats.sh prints, for a machine with no /proc to read.
#
#   cpu=12.4 mem=63.1 temp=-1 disk=71.2 up=184523
#
# A metric this machine cannot report comes back as -1. Temperature usually is
# one: the ACPI thermal zone is optional, and most laptops do not expose it to
# Windows without the vendor's own driver.
#
# CIM rather than Get-Counter, which takes a second per sample and is the whole
# runtime of this script otherwise. No arguments and no input: the plugin's key
# is restricted to running exactly this.

$ErrorActionPreference = 'SilentlyContinue'

function Get-CpuPercent {
    # Already averaged by the performance counter, so this needs no second sample.
    $cpu = Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'"
    if ($null -eq $cpu) { return -1 }

    return [math]::Round([double] $cpu.PercentProcessorTime, 1)
}

function Get-MemPercent {
    $os = Get-CimInstance Win32_OperatingSystem
    if ($null -eq $os -or $os.TotalVisibleMemorySize -le 0) { return -1 }

    $used = $os.TotalVisibleMemorySize - $os.FreePhysicalMemory
    return [math]::Round(100 * $used / $os.TotalVisibleMemorySize, 1)
}

function Get-TempCelsius {
    # Tenths of a Kelvin, when the machine reports it at all - and a machine may
    # report several zones, not all of which mean anything. A zone whose critical
    # trip point is below 60 C is not measuring a processor: this laptop has one
    # claiming 16 C with a trip point of 20 C, which is neither a temperature
    # anything runs at nor one anything shuts down over.
    $zones = Get-CimInstance -Namespace 'root/WMI' -ClassName MSAcpi_ThermalZoneTemperature |
             Where-Object { $_.CurrentTemperature -gt 0 -and $_.CriticalTripPoint -ge 3330 }
    if ($null -eq $zones) { return -1 }

    # The hottest of the believable ones: what you want to see is whatever part
    # of the machine is closest to trouble.
    $hottest = ($zones | Measure-Object -Property CurrentTemperature -Maximum).Maximum
    return [math]::Round($hottest / 10 - 273.15, 1)
}

function Get-DiskPercent {
    # The system drive: the one that filling up takes the machine down with it.
    $letter = ($env:SystemDrive).TrimEnd(':')
    $drive = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$letter`:'"
    if ($null -eq $drive -or $drive.Size -le 0) { return -1 }

    return [math]::Round(100 * ($drive.Size - $drive.FreeSpace) / $drive.Size, 1)
}

function Get-UptimeSeconds {
    $os = Get-CimInstance Win32_OperatingSystem
    if ($null -eq $os -or $null -eq $os.LastBootUpTime) { return -1 }

    return [int] ((Get-Date) - $os.LastBootUpTime).TotalSeconds
}

# Formatted against the invariant culture, not the machine's: this laptop is
# es-AR and would otherwise print "mem=45,2", which is a different number to
# anything reading the line.
[string]::Format([cultureinfo]::InvariantCulture,
    'cpu={0} mem={1} temp={2} disk={3} up={4}',
    (Get-CpuPercent), (Get-MemPercent), (Get-TempCelsius), (Get-DiskPercent), (Get-UptimeSeconds))
