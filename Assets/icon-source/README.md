# Icon sources

Editable vector artwork for the iMirror app icon (two overlapping rounded screen panels, blue and translucent silver).

- `iMirror_dark.svg` — dark background variant. Source of `Assets/iMirror.ico` and `Assets/iMirror.png`.
- `iMirror_light.svg` — light background variant. Source of `Assets/iMirror-light.ico`.
- `iMirror_symbol_transparent.svg` — background-free mark.

The `.ico` files contain PNG-compressed 16, 20, 24, 32, 40, 48, 64, 128 and 256 px frames rendered from the SVGs. To regenerate after editing an SVG, render each size (e.g. with `cairosvg` or `rsvg-convert`) and pack the PNGs into an `.ico`; `Assets/iMirror.png` is the 256 px dark render.
