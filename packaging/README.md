# Packaging the agent

Two installers, from one script, for the two tiers decision
[#262](https://github.com/wmo-raf/adl/issues/262) ships.

| Tier | Package | What it installs | Who it is for |
|---|---|---|---|
| Service | `AdlAgent-<version>-x64.msi` | A Windows Service, delayed auto-start, restarting itself on failure | A technician with administrator rights |
| Per-user | `AdlAgent-<version>-Setup.exe` | A per-user install under `%LocalAppData%`, started at logon | A technician without them (story 3) |

**Only the first is built.** `pack.ps1` produces the MSI and nothing else
unless it is passed `-WithPerUserTier`, and nothing passes it — not CI, not a
release, not `dev/pack.cmd`. No site has asked for the per-user tier, it has
never been run on a Windows machine, and a tier nobody installs is one whose
packaging is proven by nothing.

Deferred rather than dropped: every line behind it is still here and still
under test on every push — `UpdateTiers.User`, `IUpdateInstaller.Tier`,
`WindowsUpdateInstaller`'s Velopack branch, the `velopack_*` kinds in
`release-index.py`, and the packaging step itself. Reviving it is that one
switch. What it would need first is a clean Windows machine to install it on,
which is [#282](https://github.com/wmo-raf/adl/issues/282)'s first two
acceptance criteria.

One live consequence, stated rather than discovered: `UpdateService` reports
*"ADL offers 0.3.1 but served no user-tier package for it"* when a per-user
install finds no artifact of its kind. That is what every per-user install
would now hit on every update check — and it costs nothing only because the
set of them is empty.

Both carry the .NET runtime inside them. Nothing needs installing on the
target machine first, which is the point: a country server is not a machine
anyone can ask to install a framework over a phone call.

## Building

Windows, with the .NET SDK from `global.json`:

```powershell
./packaging/pack.ps1 -Version 0.2.0
```

It publishes both programs self-contained, builds the MSI with the WiX
toolset, and prints the SHA-256 of everything it produced. With
`-WithPerUserTier` it also packs that tier with Velopack's `vpk`. The tools
are installed as .NET tools if they are not already there, at the versions
pinned in the script — a packaging tool that moved under a release would
change what a fleet installs without anything in this repository having
changed.

CI runs exactly this (`.github/workflows/ci.yml`), so a package can be
reproduced by hand, which is the only way to work out what went wrong with an
install nobody can reach.

A bare run is what ships, and that is the point of there being one script:
CI calls it bare, `dev/pack.cmd` calls it bare, and a person reproducing a
release calls it bare. A default nothing ever takes is the one that breaks
unnoticed, which is why the switch points the way it does.

## Installing

The service tier needs to be told where its ADL is, and it asks. Double-click
`AdlAgent-<version>-x64.msi`, accept the elevation prompt, and one screen in
the middle of the installer wants the address of the ADL instance this machine
reports to — the address the country's ADL operator opens ADL at.

The screen refuses an address the agent would refuse, and says why, while
somebody is standing there: `https`, or `localhost`, and no spaces. Pressing
*Next* on an address it will not take explains why and stays on the screen.

The check is on the press rather than on the button, and that is Windows
Installer's doing rather than a preference. An `Edit` control writes its
property when it loses the focus — not as a key is struck — and a control
condition is re-evaluated only when a property changes. A greyed-out *Next*
with an `EnableCondition` therefore never sees what is being typed into the
field beside it, and lights up only if the technician happens to press Tab:
which is to say it refuses every address, correct ones included. Pressing the
button is itself a focus change and it lands before the button's own events,
so a condition on what the press *does* has the address in hand. What it does
**not** do is
contact the address — that would be a network call inside a Windows Installer
transaction, and it would strand every site that installs before its firewall
rule, its DNS entry or its certificate exists. Whether the instance answers is
a question the tray's *Status* tab answers, live, seconds later.

Installing a newer package over a machine that is already configured asks
again, with an empty field. The installer's own registry value holds the
address, but `adl-agent set-url` writes `agent.ini` without touching it, so
offering it back would sometimes send a machine to the instance somebody had
just moved it away from.

For a fleet, a room full of machines, or a script, the property is still there
and unchanged:

```powershell
msiexec /i AdlAgent-0.2.0-x64.msi ADLURL=https://adl.example.org
```

Given `ADLURL`, the installer does not ask — it goes straight to the
confirmation. Given `/qn`, it shows nothing at all, which is not a nicety: it
is how the agent replaces itself.

An install a person watched ends with the tray open, on the tab with the
pairing code box — the next thing to do anyway. It leaves a shortcut to the
window on the all-users desktop, in the Start menu, and in the all-users
Startup folder, so it comes back at the next logon; an uninstall takes all
three away. The window is opened from the installer's finish screen rather
than from the install itself, so a `/qn` install and a self-update open
nothing.

Two of those are tick boxes, both ticked when the screen opens: **Create a
shortcut on the desktop**, and **Open ADL Agent now** on the finish screen.
The Start menu and Startup shortcuts are not offered as choices — the Startup
one is the whole of what "the window comes back at the next logon" means, on a
machine nobody logs into on purpose.

They are on different screens, and not by preference. Windows Installer makes
shortcuts inside the install transaction, so the desktop one has to be asked
about before the install runs — on the address screen. By the time the finish
screen is drawn the icon has been made or not, and offering it there would
mean a custom action creating a file outside the transaction, which an
uninstall would then leave behind. Only the second belongs on the last screen,
because opening a window is the one thing that genuinely happens after the
install.

`INSTALLDESKTOPSHORTCUT` defaults to `1` in the property table, and that
default is load-bearing rather than a courtesy: a self-update is passed no
properties and shows no screen, so a component conditioned on a tick box
nobody ticked would be a component not installed — and a major upgrade removes
the old product first. Without the default, every machine in every fleet would
lose its desktop icon the night it updated itself. The cost, stated rather than
hidden, is that a technician who unticks it gets the icon back at the next
upgrade: remembering the choice would need a registry search into the property,
and `AppSearch` runs in the execute sequence too.

Either way the URL is written to `%ProgramData%\ADL Agent\agent.ini`, which
the service reads when it starts:

```ini
[Agent]
AdlBaseUrl=https://adl.example.org
```

A file rather than a machine-wide environment variable, because of *when* the
installer runs: the Service Control Manager takes its environment block at
boot and does not re-read it, so a service installed and started in one
Windows Installer transaction would not see a variable that same transaction
had just set — and would crash-loop until the server was rebooted.

The setting is deliberately *permanent*: a silent self-update is passed no
properties at all, and a machine that forgot where to report after updating
itself at three in the morning would be a machine somebody has to visit.

Changing it afterwards is `adl-agent set-url`, run from an elevated command
prompt — it validates the address the way the service will, writes this file,
drops the device token (the old instance issued it) and restarts the service:

```powershell
adl-agent set-url https://adl.example.org
```

Add `--keep-pairing` for an instance that has only moved domain, with the same
database behind it. See *Changing where a machine reports* in the
[README](../README.md#changing-where-a-machine-reports).

The per-user tier has no installer property to pass, so it takes the URL from
the environment of the user it runs as — set before the next logon:

```powershell
setx Agent__AdlBaseUrl https://adl.example.org
```

Add `Agent__AutoUpdate=false` on either tier for a machine whose IT
department deploys software itself. Holding one machine back from the
operator's chair is a different thing and is done in the ADL admin, by
pinning its version.

## The screens' own artwork

The two pictures WixUI draws — the strip across the top of every screen and
the panel down the left of the first and the last — are this product's rather
than the toolset's, pointed at by `WixUIBannerBmp` and `WixUIDialogBmp` in
`msi/AdlAgentUI.wxs`. Nothing else changes: the control on the address screen
still asks for `WixUI_Bmp_Banner` and gets whatever those name.

Both are BMP, and that is Windows Installer's rule rather than WiX's — a
`Bitmap` control renders BMP out of the `Binary` table and nothing else, at
24-bit with no compression. Both are exactly the size WixUI draws them at,
493×58 and 493×312, because anything else is stretched or clipped with no
warning at link time or at run time.

Neither is a panel with a screen beside it. WixUI puts both in controls that
span the whole dialog — the banner at `X=0 Width=370`, the Welcome and Exit
bitmap likewise — and paints the wizard's own text on top, from `X=15` on the
banner and `X=135` on the panel. So each is mostly white, with the colour kept
to the part no text crosses: the right edge of the banner, and a 164px band
down the left of the panel, which is 180px in image pixels less a margin. Fill
either of them edge to edge and the wizard's words end up on a coloured field
— which is what the toolset's own artwork avoids, and the reason it looks the
way it does. `InstallerArtworkTests` reads those
dimensions out of the files' own headers, and `verify-msi-install.ps1` checks
that what the linker embedded is the same length as what this repository
holds — the way a dropped `WixVariable` would otherwise revert to the
toolset's artwork silently, still building and still installing.

They are made by `assets/render-icons.sh` from the same `adl-logo.svg` the two
program icons come from, and committed, so no leg of the build needs a
rasteriser. See [`assets/README.md`](../assets/README.md).

## What the MSI is careful about

The agent installs a new MSI over itself, unattended, on a machine nobody can
reach. Three details in `msi/AdlAgent.wxs` carry that:

- **`%ProgramData%\ADL Agent` is a permanent component.** A major upgrade
  uninstalls the old product before installing the new one, and the device
  token lives there. Without this, every automatic update would need somebody
  in-country to re-pair the machine.
- **The ADL URL component is permanent *and* conditioned on `ADLURL`.** An
  upgrade passes no properties, so the component is not installed and the
  existing setting is left alone.
- **The desktop shortcut's component is conditioned, and its property is
  defaulted.** The same mechanism pointed the other way: a tick box has to be
  able to turn the icon off, and an upgrade passed no properties must not.
  `INSTALLDESKTOPSHORTCUT` defaults to `1`, so a silent self-update installs
  the component exactly as it did before the tick box existed.
- **Same-version upgrades are allowed.** Every build that is not a tag calls
  itself the version in `Directory.Build.props`, and without this two of them
  would install side by side and fight over one service name.
- **The state directory's permissions are replaced, not inherited.** SYSTEM
  and Administrators only: the device token is stored in the clear, and on a
  shared vendor server every local account could otherwise read a credential
  that can send data to a national instance.

The screen in `msi/AdlAgentUI.wxs` had to be added without weakening either of
the first two, which is why the only thing that sets `ADLURL` is a control
event on a dialog, in the user interface sequence. The tempting alternative —
a registry search, so that an upgrade run by hand could offer back the address
the machine already has — would have set the property on every silent
self-update in the fleet too, because `AppSearch` runs in the execute sequence
as well.

Neither `.wxs` declares a custom action, for the same reason: `/qn` shows no
dialog whatever a package asks for, so the way a screen breaks an unattended
install is never the screen — it is something scheduled beside it, which then
runs on every silent upgrade in the fleet. The ones the package does carry all
come from the WiX util extension — the service's failure actions, the state
folder's permissions, and the one the finish screen presses to open the tray —
and `verify-msi-install.ps1` refuses any that do not.

That last one is referenced rather than authored, so it arrives unscheduled:
it sits in the `CustomAction` table and in no sequence, and the only thing
that reaches it is a control event on the Exit dialog. An install that shows
nobody anything therefore opens nothing, which is what a self-update on a
server with nobody logged on has to be. `verify-msi-install.ps1` reads both
halves back out of the built database.

## What is checked, and where

| Check | Where | Runs on |
|---|---|---|
| The screen's rule matches the agent's, and every refusal is explained | `InstallerDialogTests` | every commit, both platforms |
| The shortcuts and the finish screen's action are in the package, and the action is in no sequence | `InstallerFinishTests` | every commit, both platforms |
| The built package really carries the dialog, something leads to it, and no custom action of ours rides along | `verify-msi-install.ps1` | the packaging job |
| `ADLURL=` still works, `/qn` still installs, and a silent upgrade keeps the address | `verify-msi-install.ps1` | the packaging job |
| An install leaves all three shortcuts on the machine, and an uninstall leaves none | `verify-msi-install.ps1` | the packaging job |
| Windows will actually start what was packaged | `verify-tray-starts.ps1` | the packaging job |
| Every XML the repository ships parses, these two included | `ShippedXmlTests` | every commit, both platforms |

The dialog is the part of this product nothing else could check. WiX stores a
control condition as an opaque string and never parses it, so a malformed one
compiles, links, ships, and is first evaluated on a country server. The tests
read the condition out of the source and run it against the same addresses
`AgentOptions` is run against; the script installs the package and reads the
file it wrote.

Windows Installer's condition syntax has no regular expressions and cannot
take a string apart, so the match is close rather than exact, and both edges
are pinned by tests: the screen lets through a few addresses `Uri` would
refuse (a doubled dot, a port that is not a number), and refuses a few it
would take (`127.0.0.11`, and leading whitespace). A machine in the first
group is not silent about it — the service reports that its address is not
usable and the tray's *Status* tab says so.

## Publishing a release

Tag it. `v0.2.0` builds the packages, writes `agent-releases.json` beside
them, and publishes both to a GitHub release.

That index is the seam between this repository and
[`adl-agent-plugin`](https://github.com/wmo-raf/adl-agent-plugin): every ADL
instance mirrors it nightly, verifies each package against the digest it
states, and holds the release for its own fleet — staged, until that country's
operator publishes it. The agents themselves never see this repository; they
ask their own ADL.

An instance with no egress skips all of that and uploads the packages in its
admin instead. Same rows, same feed, and an agent cannot tell the difference.

## Signing

Nothing here is signed yet. Pilots ship unsigned, attended
(decision #262), and the update path verifies feed hashes from the first
build — see `UpdateService`. Signing is
[#283](https://github.com/wmo-raf/adl/issues/283): a SignPath Foundation
application, with a Certum certificate as the fallback.
