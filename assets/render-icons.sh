#!/usr/bin/env bash
#
# Turn adl-logo.svg into everything the two programs and the two installers
# need, and write the results next to it.
#
# Run by hand, when the brand changes -- never by the build. `dotnet build`
# and `dotnet restore` have no dependencies today, on either CI leg or on any
# machine an NMHS technician might reproduce a release from, and a rasteriser
# in the build would be a dependency on all of them for a picture that
# changes about once. So the outputs are committed and this is the record of
# how they were made.
#
# Usage:
#   assets/render-icons.sh
#
# Needs ImageMagick 7 (`magick`). On macOS: brew install imagemagick.
#
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source_svg="$here/adl-logo.svg"

command -v magick >/dev/null 2>&1 || {
    echo "render-icons.sh needs ImageMagick 7 (magick). Nothing was written." >&2
    exit 1
}

# The two tiles. Teal is the product a technician meets -- the tray, the
# window, the shortcut they click, the installer they ran. Slate is the
# background service, and it exists only so that the two entries in Task
# Manager and Services.msc can be told apart at 16 pixels, where colour is
# the only thing that still reads and shape is not.
teal="#1F7D8C"
slate="#2E3B4E"

# The mark inside the tile, and the tile's corner radius, on a 256 canvas.
# 176 leaves the margin a Windows application icon is expected to have; the
# radius matches what Windows itself draws around a modern app icon.
mark_size=176
canvas=256
radius=46

# Every size Windows asks for. 16 is the notification area and Task Manager,
# 32 the taskbar and Alt-Tab, 48 the desktop, 256 the Explorer preview and
# Add/Remove Programs.
sizes=(16 24 32 48 64 256)

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# The mark on its own, trimmed of the empty space the source SVG carries
# around it and re-centred, so that what fills the tile is the drawing rather
# than the artboard.
magick -background none "$source_svg" \
    -trim +repage \
    -resize "${mark_size}x${mark_size}" +repage \
    -gravity center -extent "${canvas}x${canvas}" +repage \
    "$work/mark.png"

# One tile, one .ico. Written with every size in one file, because Windows
# picks the frame it wants and a single-size icon is the one that looks
# resampled in half the places it appears.
render_ico() {
    local colour="$1" out="$2" frames=()

    magick -size "${canvas}x${canvas}" xc:none \
        -fill "$colour" \
        -draw "roundrectangle 6,6 $((canvas - 7)),$((canvas - 7)) $radius,$radius" \
        +repage "$work/tile.png"

    magick "$work/tile.png" "$work/mark.png" -gravity center -composite +repage "$work/tiled.png"

    for size in "${sizes[@]}"; do
        magick "$work/tiled.png" -resize "${size}x${size}" +repage "$work/frame-$size.png"
        frames+=("$work/frame-$size.png")
    done

    magick "${frames[@]}" "$out"
}

render_ico "$teal"  "$here/adl-agent-tray.ico"
render_ico "$slate" "$here/adl-agent.ico"

# The teal tile as a plain raster, for the two places the window puts the
# mark on screen itself: the header, and the block heading the Status tab.
# WPF can render a frame out of an .ico, but which frame it picks depends on
# the size it is laid out at, and both of those are drawn at a size no frame
# has. A PNG it can scale is one fewer thing to be surprised by.
magick -size "${canvas}x${canvas}" xc:none \
    -fill "$teal" \
    -draw "roundrectangle 6,6 $((canvas - 7)),$((canvas - 7)) $radius,$radius" \
    +repage "$work/tile.png"
magick "$work/tile.png" "$work/mark.png" -gravity center -composite +repage \
    "$here/adl-tile.png"

# And the mark alone, white on nothing, for the notification area. The tray
# recolours this to whatever the state is, so what matters here is the shape
# and the alpha; the colour in the file is thrown away.
#
# Note what happens to the lettering. "ADL" is fill="#000" in the source, at
# full alpha, so recolouring the whole thing makes the letters the same
# colour as the discs and they vanish. That is deliberate: the letters are
# 32% of the width by 11% of the height, which is 5 by 2 pixels at tray size
# -- they were never going to be read there, and a mark that quietly loses
# them is better than one that keeps them as noise.
magick -background none "$source_svg" \
    -trim +repage \
    -resize "232x232" +repage \
    -gravity center -extent "${canvas}x${canvas}" +repage \
    "$here/adl-mark.png"

echo "Wrote, from $(basename "$source_svg"):"
for file in adl-agent-tray.ico adl-agent.ico adl-tile.png adl-mark.png; do
    printf '  %-22s %8d bytes\n' "$file" "$(wc -c <"$here/$file")"
done
