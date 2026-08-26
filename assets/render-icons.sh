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

# Written into every PNG ImageMagick makes, and the reason the outputs can be
# committed at all: without it each file carries a tIME chunk holding the
# moment it was rendered, so re-running this dirties four files that have not
# changed by a pixel. A record of how something was made is worth less if
# running it always shows a diff.
png_is_the_same_every_time=(-define png:exclude-chunk=time)

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
    "${png_is_the_same_every_time[@]}" "$here/adl-tile.png"

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
    "${png_is_the_same_every_time[@]}" "$here/adl-mark.png"

# ---------------------------------------------------------------------------
# The installers' own pictures
#
# WixUI draws two, at exactly these sizes, and Windows Installer stretches or
# clips anything else without saying so. 493x58 is the strip across the top of
# every screen; 493x312 is the panel down the left of the first and the last.
#
# BMP, and that is a constraint rather than a preference: a Windows Installer
# Bitmap control renders BMP out of the Binary table and nothing else. The
# depth is forced to 24 and compression off, because a BMP with an alpha
# channel or a run-length payload is one some versions draw as black.
# ---------------------------------------------------------------------------

banner_width=493
banner_height=58
panel_width=493
panel_height=312

# White, because both of these are drawn over the dialog rather than beside
# it: the banner control is the full width of the screen it sits on, and so is
# the panel control. Anything but white leaves a field of colour under text
# that was drawn expecting none.
paper="#FFFFFF"

# The banner. The mark on the right, because the left is not free -- every
# dialog paints its Title at X=15 and its Description at X=25 over this, in
# near-black, and a mark under either of them is a mark in the way of the
# words.
#
# 44 of the 58 leaves seven pixels of air above and below, so the mark reads
# as placed on the strip rather than as filling it.
magick -size "${banner_width}x${banner_height}" "xc:$paper" \
    \( "$work/mark.png" -background none -fill "$teal" -colorize 100 \
       -resize "44x44" \) \
    -gravity east -geometry +24+0 -composite \
    -alpha remove -alpha off -type TrueColor -compress none \
    "BMP3:$here/installer-banner.bmp"

# The panel, and the thing about it that is not a matter of taste.
#
# WixUI's WelcomeDlg and ExitDialog put this bitmap in a control that is the
# whole width of the dialog -- X=0, Width=370 -- and then paint the title and
# the body text on top of it, starting at X=135. So this is not a panel with a
# screen beside it: it is the screen, and only its left edge is a panel. Colour
# it all and the wizard's own words end up on a coloured field, which is what
# the toolset's own artwork avoids by being white everywhere except a band
# down the left.
#
# 135 units of 370 is 180 of these 493 pixels, so the band stops at 164 and
# leaves sixteen pixels of white before the first character.
band=164

# A framed tile near the top of the band, which is the arrangement the stock
# artwork uses and the one this was asked to follow. The frame is what stops
# the mark reading as a sticker: a white shape on teal with nothing around it
# floats, and the same shape inside a rule sits.
magick -size "${panel_width}x${panel_height}" "xc:$paper" \
    \( -size "${band}x${panel_height}" "xc:$teal" \) \
    -gravity northwest -geometry +0+0 -composite \
    -fill none -stroke "$paper" -strokewidth 3 -draw "rectangle 40,44 124,128" \
    \( "$work/mark.png" -background none -fill "$paper" -colorize 100 \
       -resize "64x64" \) \
    -gravity northwest -geometry +50+54 -composite \
    -alpha remove -alpha off -type TrueColor -compress none \
    "BMP3:$here/installer-panel.bmp"

# The per-user tier's splash, which is a different problem and keeps the
# shape the panel had before: Velopack draws this on its own, over nothing,
# with no text on it. A band down one side would be a band down one side of
# a floating window.
magick -size "${panel_width}x${panel_height}" "xc:$teal" \
    \( "$work/mark.png" -background none -fill "$paper" -colorize 100 \
       -resize "150x150" \) \
    -gravity center -geometry +0-20 -composite \
    "${png_is_the_same_every_time[@]}" "$here/installer-splash.png"

echo "Wrote, from $(basename "$source_svg"):"
for file in adl-agent-tray.ico adl-agent.ico adl-tile.png adl-mark.png \
            installer-banner.bmp installer-panel.bmp installer-splash.png; do
    printf '  %-22s %8d bytes\n' "$file" "$(wc -c <"$here/$file")"
done
