#!/usr/bin/env python3
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""Is the Demon's Souls in-world HUD on screen?

The health (red), magic (blue) and stamina (green) bars at the top left appear only once the
character is in the world, never on the loading screen, in a movie or on a menu, so their colours
in fixed fractions of the window are a signal that needs nothing from the game or the emulator.
Prints the three bar coverages; exits 0 when all are present, 1 when not, 2 on a bad image.

  hud-detect.py <screenshot.png>
"""
import sys

import numpy as np
from PIL import Image

# Fractions of the window image (x0, x1, y0, y1), measured on a 2000x1157 view of the window
# (title bar included) at the spawn point of Outpost Passage.
REGIONS = {
    "health": (0.050, 0.165, 0.052, 0.078),
    "magic": (0.055, 0.092, 0.087, 0.105),
    "stamina": (0.055, 0.140, 0.116, 0.136),
}


def coverage(pixels, name):
    x0, x1, y0, y1 = REGIONS[name]
    height, width = pixels.shape[:2]
    crop = pixels[int(y0 * height):int(y1 * height) + 1, int(x0 * width):int(x1 * width) + 1].astype(int)
    r, g, b = crop[..., 0], crop[..., 1], crop[..., 2]
    if name == "health":
        hit = (r > 100) & (r > g * 1.6) & (r > b * 1.6)
    elif name == "magic":
        hit = (b > 90) & (b > r * 1.4) & (b > g * 1.15)
    else:
        hit = (g > 70) & (g > r * 1.25) & (g > b * 1.25)
    return float(hit.mean())


def measure(path):
    with Image.open(path) as image:
        pixels = np.asarray(image.convert("RGB"))
    return {name: coverage(pixels, name) for name in REGIONS}


def present(measured, threshold=0.30):
    return all(value >= threshold for value in measured.values())


def main():
    if len(sys.argv) != 2:
        print(__doc__, file=sys.stderr)
        return 2
    try:
        measured = measure(sys.argv[1])
    except (OSError, ValueError) as failure:
        print(f"cannot read {sys.argv[1]}: {failure}", file=sys.stderr)
        return 2
    print(" ".join(f"{name}={value:.2f}" for name, value in measured.items()))
    return 0 if present(measured) else 1


if __name__ == "__main__":
    sys.exit(main())
