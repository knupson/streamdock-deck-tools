# Lights off / back on for the Blackout key.
#
#   pythonw NightLights.py off   -> RGB to black, case panel and deck dark
#   pythonw NightLights.py on    -> everything back as it was
#
# Started by BlackoutAction next to the overlay. Each step is independent: one
# failing (OpenRGB closed, panel unplugged) never stops the others. Log in
# %APPDATA%\DeckTools\nightlights.log.

import datetime
import os
import subprocess
import sys
import time
import traceback

PYTHON = os.path.join(os.environ.get("LOCALAPPDATA", ""), r"Programs\Python\Python313\python.exe")
PANEL_REPO = r"E:\Repositorios\Personal\solarmax-cm-b600l-panel"
PANEL_PORT = "COM3"
PANEL_TASK = "PanelVitals"
RGB_PROFILE = "Naranja"  # the only colour scheme in use
DECK_VID, DECK_PID, DECK_USAGE_PAGE = 0x0200, 0x3001, 0xFFA0
DECK_ON_BRIGHTNESS = 100
NO_WINDOW = 0x08000000

LOG = os.path.join(os.environ.get("APPDATA", "."), "DeckTools", "nightlights.log")


def log(msg):
    try:
        os.makedirs(os.path.dirname(LOG), exist_ok=True)
        with open(LOG, "a", encoding="utf-8") as f:
            f.write(f"{datetime.datetime.now():%Y-%m-%d %H:%M:%S} {msg}\n")
    except OSError:
        pass


def step(name, fn):
    try:
        fn()
        log(f"{name}: ok")
    except Exception:
        log(f"{name}: FAILED\n{traceback.format_exc()}")


# --- RGB (coolers, motherboard, keyboard, GPU) through the OpenRGB SDK server --

def rgb_off():
    from openrgb import OpenRGBClient
    from openrgb.utils import RGBColor
    c = OpenRGBClient("127.0.0.1", 6742, "decktools-blackout")
    try:
        for d in c.devices:
            modes = [m.name.lower() for m in d.modes]
            if "off" in modes:
                d.set_mode("off")
                continue
            for m in ("direct", "static"):
                if m in modes:
                    d.set_mode(m)
                    break
            d.set_color(RGBColor(0, 0, 0))
    finally:
        c.disconnect()


def rgb_on():
    from openrgb import OpenRGBClient
    from openrgb.utils import ModeColors
    c = OpenRGBClient("127.0.0.1", 6742, "decktools-blackout")
    try:
        c.load_profile(RGB_PROFILE)
        # The motherboard's ARGB header (case and CPU coolers) stays dark after
        # the profile restores its Static mode; sending that mode and its
        # colour again wakes it up. The profile applies asynchronously, so the
        # state is read back only after it settles.
        time.sleep(1.5)
        c.update()
        for d in c.devices:
            mode = d.modes[d.active_mode]
            if mode.color_mode == ModeColors.MODE_SPECIFIC and mode.colors:
                colors = list(mode.colors)
                d.set_mode(mode.name)
                d.set_colors(colors)
    finally:
        c.disconnect()


# --- Case panel (vmaxpanel daemon on COM3) ------------------------------------

def panel_off():
    subprocess.run([PYTHON, "-m", "vmaxpanel", "--parar"], cwd=PANEL_REPO,
                   timeout=60, creationflags=NO_WINDOW, capture_output=True)
    import serial
    time.sleep(1)
    with serial.Serial(PANEL_PORT, 115200, timeout=1) as s:
        s.write(bytes([0xAA, 0xBB, 0, 0xCC, 0xDD]))
        s.flush()


def panel_on():
    subprocess.run(["schtasks", "/Run", "/TN", PANEL_TASK], timeout=30,
                   creationflags=NO_WINDOW, capture_output=True, check=True)


# --- Stream deck backlight, written straight to its HID interface ------------

def deck_brightness(value):
    import hid
    paths = [d["path"] for d in hid.enumerate(DECK_VID, DECK_PID)
             if d["usage_page"] == DECK_USAGE_PAGE]
    if not paths:
        raise RuntimeError("stream deck not found")
    h = hid.device()
    h.open_path(paths[0])
    try:
        h.write((b"\x00CRT\x00\x00LIG\x00\x00" + bytes([value])).ljust(1025, b"\x00"))
    finally:
        h.close()


def main():
    action = sys.argv[1] if len(sys.argv) > 1 else ""
    if action == "off":
        step("rgb off", rgb_off)
        step("panel off", panel_off)
        step("deck off", lambda: deck_brightness(0))
    elif action == "on":
        step("deck on", lambda: deck_brightness(DECK_ON_BRIGHTNESS))
        step("rgb on", rgb_on)
        step("panel on", panel_on)
    else:
        log(f"unknown action {action!r}")
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
