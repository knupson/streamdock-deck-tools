#!/bin/sh
# One line of machine metrics for the Deck Tools Remote Monitor, cheap enough to
# run every few seconds over SSH:
#
#   cpu=12.4 mem=63.1 temp=48.9 disk=71.2 up=184523
#
# Percentages, degrees Celsius, and uptime in seconds. A metric this machine
# cannot report comes back as -1 rather than being left out, so the tile can say
# so instead of the plugin guessing which field is missing.
#
# POSIX sh and awk only: this runs on a Raspberry Pi with nothing installed on
# top of the base image. It is what the plugin's key is restricted to executing,
# so it takes no arguments and reads nothing from its caller.

# CPU is a rate, not a reading: two samples of /proc/stat with a gap, then the
# share of that gap which was not idle.
cpu_percent() {
    [ -r /proc/stat ] || { echo -1; return; }

    set -- $(awk '/^cpu /{idle=$5+$6; total=0; for (i=2; i<=NF; i++) total+=$i; print total, idle}' /proc/stat)
    total1=$1 idle1=$2

    sleep 0.3

    set -- $(awk '/^cpu /{idle=$5+$6; total=0; for (i=2; i<=NF; i++) total+=$i; print total, idle}' /proc/stat)
    total2=$1 idle2=$2

    awk -v t1="$total1" -v i1="$idle1" -v t2="$total2" -v i2="$idle2" 'BEGIN {
        dt = t2 - t1
        if (dt <= 0) { print -1; exit }
        printf "%.1f", 100 * (1 - (i2 - i1) / dt)
    }'
}

# MemAvailable rather than MemFree: cache and buffers are memory you can still
# have back, and counting them as used reads as a machine permanently at 90%.
mem_percent() {
    [ -r /proc/meminfo ] || { echo -1; return; }

    awk '/^MemTotal:/{t=$2} /^MemAvailable:/{a=$2}
         END { if (t > 0 && a != "") printf "%.1f", 100 * (1 - a / t); else print -1 }' /proc/meminfo
}

# Largest of the readings on stdin, ignoring anything that is not a plain number.
# Prints nothing at all when there was nothing to take, so the caller's check is
# for a number rather than for a sentinel.
hottest() {
    awk 'BEGIN { best = -1 } /^[0-9]+$/ { if ($1 > best) best = $1 }
         END { if (best > 0) print best }'
}

# Whether a reading is a plain positive integer. Tested by pattern and not with
# [ -le ], which errors out on an empty string and leaves the caller believing
# the comparison was false - that is how a machine with no sensor at all ended up
# reporting 0.0 degrees instead of saying it had nothing.
usable() {
    case "$1" in
        ''|*[!0-9]*) return 1 ;;
    esac
    [ "$1" != "0" ]
}

# The processor's own sensor if the machine names one, and the hottest thermal
# zone otherwise. Taking the first zone that exists is not enough: a laptop can
# carry several, some reporting 0 because they are not measuring anything, and on
# a desktop distribution the reading worth having is often not a thermal zone at
# all.
temp_celsius() {
    # coretemp and k10temp are the Intel and AMD package sensors; cpu_thermal is
    # what a Raspberry Pi calls its own. Named drivers only - the rest of hwmon is
    # NVMe, battery and wireless, and the hottest of those is not a CPU reading.
    raw=$(
        for hwmon in /sys/class/hwmon/hwmon*; do
            [ -r "$hwmon/name" ] || continue

            case "$(cat "$hwmon/name" 2>/dev/null)" in
                coretemp|k10temp|zenpower|cpu_thermal) ;;
                *) continue ;;
            esac

            for input in "$hwmon"/temp*_input; do
                [ -r "$input" ] && cat "$input"
            done
        done | hottest
    )

    if ! usable "$raw"; then
        raw=$(
            for zone in /sys/class/thermal/thermal_zone*/temp; do
                [ -r "$zone" ] && cat "$zone"
            done | hottest
        )
    fi

    # Last resort: the hottest hwmon reading of any name, kept to a range a
    # machine could plausibly be at. Laptop vendors ship sensors under names no
    # list will ever have all of, and a drive or a chipset reading is still a real
    # temperature - a great deal more use than admitting nothing.
    if ! usable "$raw"; then
        raw=$(
            for input in /sys/class/hwmon/hwmon*/temp*_input; do
                [ -r "$input" ] || continue

                value=$(cat "$input" 2>/dev/null)
                case "$value" in ''|*[!0-9]*) continue ;; esac

                # 20 to 120 C, in the millidegrees these are reported in.
                [ "$value" -ge 20000 ] && [ "$value" -le 120000 ] && echo "$value"
            done | hottest
        )
    fi

    if ! usable "$raw"; then echo -1; return; fi

    # Millidegrees on every kernel that exposes this, but a few report whole
    # degrees, and 45000 C would be a strange reading to print.
    # Parenthesised: bare, awk reads the ">" as redirecting printf to a file
    # called 1000 instead of comparing against it.
    awk -v raw="$raw" 'BEGIN { printf "%.1f", (raw > 1000 ? raw / 1000 : raw) }'
}

# The root filesystem: the one that filling up takes the machine down with it.
disk_percent() {
    df -P / 2>/dev/null | awk 'NR == 2 { sub(/%/, "", $5); print $5 + 0; found = 1 }
                               END { if (!found) print -1 }'
}

uptime_seconds() {
    [ -r /proc/uptime ] || { echo -1; return; }
    awk '{ printf "%d", $1 }' /proc/uptime
}

printf 'cpu=%s mem=%s temp=%s disk=%s up=%s\n' \
    "$(cpu_percent)" "$(mem_percent)" "$(temp_celsius)" "$(disk_percent)" "$(uptime_seconds)"
