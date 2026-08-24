# The agent's icons

Everything here is made from `adl-logo.svg` by `render-icons.sh`, which is run
by hand and never by the build.

## Where the vector came from

`adl-logo.svg` is a verbatim copy of `docs/_static/images/logo.svg` in the
[ADL core repository](https://github.com/wmo-raf/adl). It is copied rather
than referenced because this is a separate repository, and a build that
reached into a sibling checkout would work only on the machines that happen
to have one.

Copied verbatim, so that `diff` against the core still means something. The
trimming, padding and tiling are done by the script, not by editing the file.

## The two colours

| Tile | Hex | Worn by |
|---|---|---|
| Teal | `#1F7D8C` | `adl-agent-tray.exe`, the window and its titlebar, the Start-menu and Startup shortcuts, Add/Remove Programs, `AdlAgent-<version>-Setup.exe` |
| Slate | `#2E3B4E` | `adl-agent.exe`, the Windows Service |

Teal is the product a technician meets. Slate exists for one reason: the two
programs appear side by side in Task Manager and Services.msc at sixteen
pixels, and at that size colour is the only thing that still reads.

Neither colour is in the tray's palette. The notification area uses green,
amber, red and grey to say what the machine is doing, and an application icon
that happened to be green would be a permanent claim that it is working.

## What the script writes

| File | What it is |
|---|---|
| `adl-agent-tray.ico` | Teal tile, 16/24/32/48/64/256. `<ApplicationIcon>` for the tray, the window's `Icon`, `ARPPRODUCTICON`, and `vpk --icon`. |
| `adl-agent.ico` | Slate tile, same sizes. `<ApplicationIcon>` for the service. |
| `adl-tile.png` | The teal tile at 256, for the two places the window draws the mark itself. |
| `adl-mark.png` | The mark alone, for the notification area. Recoloured at run time, so only its shape and alpha are used. |

## Regenerating

```bash
assets/render-icons.sh
```

Needs ImageMagick 7. Commit whatever changes — the outputs are in git on
purpose, so that `dotnet build` keeps having no dependencies on either CI leg
and on any machine somebody reproduces a release from.

## The lettering

`adl-mark.png` still contains the "ADL" lettering, and the tray still loses
it. The letters are `fill="#000"` at full alpha, so recolouring the mark makes
them the same colour as the discs.

That is the intended outcome rather than an accident worked around. In the
source the letters span 32% of the width by 11% of the height, which at a
sixteen-pixel icon is five pixels by two: there is no treatment that makes
them legible there, and a mark that drops them cleanly is better than one
that keeps them as three grey smudges. They are read at 32 pixels and above,
where the tiles are, and that is where they belong.
