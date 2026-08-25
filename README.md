# ADL Agent

The country-server end of ADL's push-based file delivery.

Many NMHSs cannot expose anything to the internet — no public IP, no inbound
ports — so ADL cannot reach in to collect their AWS files. The ADL Agent
inverts the direction: it is installed on the vendor server, paired once
against an ADL instance, and pushes raw station files outbound over HTTPS.
That outbound call is the only network path it ever needs.

The server side lives in [`adl-agent-plugin`](https://github.com/wmo-raf/adl-agent-plugin);
the design this implements is [wmo-raf/adl#269](https://github.com/wmo-raf/adl/issues/269).

## What is built so far

The skeleton and first live vertical
([wmo-raf/adl#278](https://github.com/wmo-raf/adl/issues/278)):

- **pairing** — exchange a pairing code for a device token and store it
- **sync with an offline cache** — fetch the device's configuration every
  cycle, and keep working from the last one when ADL is unreachable
- **heartbeat** — the 5-minute liveness report, on a loop deliberately
  isolated from the scan loop
- **revocation** — a `401` stops the machine sending and surfaces
  "re-pair needed" locally

The upload cycle ([wmo-raf/adl#279](https://github.com/wmo-raf/adl/issues/279)),
which is the thing the product is: **sync → scan → manifest → upload**, on the
check interval ADL sets.

- **`ENUMERATE`** — each distinct folder is walked once per cycle and its
  entries are routed to station links by pattern, so forty stations sharing a
  vendor's dump directory cost one walk, not forty.
- **candidate window** — a file is a candidate when the timestamp the metadata
  seam reports (on Windows, the later of last-write and creation) is at or
  after ADL's watermark for that station. The `max()` is what catches both a
  logger appending to today's file and a backfill copied in weeks late.
- **hash memo cache** — only candidates are hashed, and only those whose
  `(size, timestamp)` is not already known. A settled folder costs the walk and
  nothing else. The cache is pure optimisation: losing it costs one re-hashing
  pass and never data, so it is never written to disk.
- **partial files** — anything written inside the station's stability window
  (default 60 s), or that the readiness seam says is held open, is left for the
  next cycle. It counts as backlog, never as a failure.
- **newest first** — a fresh install facing months of backlog offers today's
  files in its first manifest page and drains history behind them.
- **paging** — manifests are sent in pages of the size ADL states (~500).
- **retry is the next cycle** — the agent keeps no record of what it delivered.
  The vendor's folder is its only state; a refused upload, a dead link, or a
  power cut mid-cycle all resolve by offering the same files again.

`DIRECT_FETCH` and the reconciliation sweep
([wmo-raf/adl#280](https://github.com/wmo-raf/adl/issues/280)) — the escape
hatch for folders where listing is itself the problem, and the backstop that
lets the cheap path be cheap.

- **`DIRECT_FETCH`** — a station ADL puts on this strategy never lists its
  folder. It builds the filenames its vendor's clock implies (prefix +
  datetime + extension, on the interval and in the filename timezone ADL
  holds) and asks the filesystem about those exact names. A million files in
  the directory cost nothing, because nothing reads them. An expected file
  that is not there is a quiet non-event — the interval that has not finished
  yet is missing on every cycle for ever — but a station that finds *none* of
  its names says so.
- **filename grid** — instants are aligned to the interval as the vendor sees
  it, measured from local midnight in the station's filename timezone, and
  re-aligned each step so a daylight-saving change moves the grid rather than
  the backlog. A country on a fractional UTC offset writes `…1440`, not
  `…1445`.
- **bounded** — one cycle constructs at most 20 000 names per station, newest
  first, so a one-minute cadence with a start date two years back cannot cost
  a million stat calls every ten minutes. What the bound cuts off is picked
  up by the reconciliation below.
- **reconciliation sweep** — once a day a station stops trusting the cheap
  path. An enumerating one offers everything its pattern matches back to its
  collection start date rather than only what is above ADL's watermark, which
  is what catches a file whose timestamps make it look old however it
  arrived. A `DIRECT_FETCH` one has no folder to re-walk and no lower floor
  to find anything with, so what it reconciles is its own reach: it asks
  about every name back to the start date (up to 500 000) instead of the
  newest 20 000 — without which a file copied in three weeks late would be
  looked for on no cycle at all.
- **a sweep is only a lower floor** — the same single walk, the same
  readiness check, the same hashes; ADL's ledger diff decides what it was
  worth. A sweep cut short by an unreachable ADL stays owed. A station the
  scan turned away (no folder, no pattern, half-configured Direct Fetch) is
  not recorded as swept, so fixing it does not mean waiting another day. The
  record survives a restart, so a service that restarts hourly does not offer
  its whole folder hourly.
- **cadence** — daily by default. The agent reads
  `reconciliation_interval_hours` from the device block of the sync response
  when ADL sends it (`0` switches sweeps off); the plugin does not serve that
  field yet, so today every install reconciles daily. Adding it server-side
  is a companion change in
  [`adl-agent-plugin`](https://github.com/wmo-raf/adl-agent-plugin).

The tray and configuration app ([wmo-raf/adl#281](https://github.com/wmo-raf/adl/issues/281))
— everything a station technician does on the machine itself, without an ADL
login.

- **pair** — paste the code, see the device name ADL knows this machine by.
- **the station list, split by connection** — the connections ADL has given
  this device down the left, the stations under the selected one on the right,
  each with its local folder binding, what the last cycle did for it, and the
  sentence explaining any station that collected nothing. Every connection
  row carries its own standing — *3 stations need a folder*, *switched off in
  ADL*, *no stations linked* — so nobody has to click through vendors to find
  out there is nothing in them.
- **live pattern validation** — the count of files a folder and a pattern
  would match, answered while they are being typed, by the same glob and the
  same filename builder the cycle itself uses. "Two files here, none of them
  matching" and "nothing here at all" are told apart, because they are
  different mistakes. A `DIRECT_FETCH` station is previewed by asking about
  the newest few hundred names it expects, and still lists nothing.
- **config written through to ADL** — the app tier (folder, pattern,
  strategy, the Direct Fetch settings, the stability window) is written to
  ADL and read straight back, so an administrator sees it in the admin and
  the `config_version` moves. A write ADL did not accept did not happen: it
  is never applied locally instead.
- **re-pair on revocation** — a `401` becomes an instruction rather than a
  failed action, on the same screen that just refused.

The tray is thin by construction. Its only route to any fact is a command
the service implements, so it cannot show something the service does not
know; and the commands are what the tests drive, because the window itself
is layout.

Installers and auto-update ([wmo-raf/adl#282](https://github.com/wmo-raf/adl/issues/282))
— how the agent gets onto a machine, and how it stays current on one nobody
can reach.

- **two installers** — an MSI that installs a Windows Service (delayed
  auto-start, restarting itself on failure, its state directory locked to
  SYSTEM and Administrators) and a Velopack per-user install for a technician
  without administrator rights. Both self-contained: nothing to install first.
  See [`packaging/`](packaging/README.md).
- **the feed is ADL** — these machines have one network path, so the update
  feed is served by the instance they already talk to. The agent asks on every
  cycle, as the tier it was installed as, and ADL picks the package that tier
  takes.
- **the hash is checked, always** — pilots ship unsigned, so the digest ADL
  states is the whole of what stands between a corrupted or tampered download
  and a service binary running as LocalSystem. A package that does not match
  is deleted rather than installed, and is not fetched again until ADL offers
  something else — not forty megabytes every ten minutes, for ever, to fail
  the same check.
- **the pin is ADL's** — an operator pinning a device in the admin means that
  machine is never *told* a newer release exists. Honouring it is therefore
  not something an agent could forget. The agent only moves forwards: a pin
  below the running version holds the machine where it is rather than rolling
  it back.
- **updating does not cost the pairing** — the token and the configuration
  cache live under `%ProgramData%`, which the MSI marks permanent, so the
  uninstall half of a major upgrade cannot take them with it.
- **it is the least urgent thing the agent does** — its own loop, its own
  failures, and nothing it can do to the cycle that ships a country's
  observations.

The guided window ([wmo-raf/adl#297](https://github.com/wmo-raf/adl/issues/297))
— the tray told a technician what the machine *is*, and never what to do about
it. It opened on Pairing whatever the machine was, and a paired device whose
administrator had not yet linked any stations showed the same empty grid as
one whose sync was failing.

- **it opens on the tab that matters** — Status while there is something to
  do to this machine, Stations once there is not. Chosen once, from the first
  answer the service gives, and never moved again: a window that re-picked on
  every poll would take somebody off the tab they had just opened, five
  seconds after they opened it. (It opened on a Pairing tab then; that tab has
  since been folded into Status — see below.)
- **one line, always right, on every tab** — what to do now and who has to do
  it. Paste the code your administrator gave you; wait while ADL links
  stations to this device; bind a folder to *Kisumu*; nothing, this machine is
  working. It moves on the poll the window already makes, so an administrator
  linking a station in another building arrives on the screen by itself.
- **not a wizard, on purpose** — the moments that need guidance are not only
  on day one. A station linked six months later lands in exactly the same
  unbound state as the first one, and a wizard that ran once is not there for
  it. A line that is always right guides both, and it does not put a second
  copy of the folder-binding screen beside the first to drift away from it.
- **an empty station list says which emptiness it is** — "ADL has not linked
  any stations to this device yet", "ADL is not answering" and "the ADL Agent
  service is not running" are three problems wanting three different people,
  and until this they were one blank rectangle. A connection with nothing
  under it says so beside itself, and only while the list can be trusted: on a
  configuration read off the disk during an outage the tab explains the outage
  instead, rather than letting a cached connection blame an administrator for
  a broken network.
- **the dot in the corner is the line's colour** — taken from the same
  sentence rather than decided again beside it, so the tray cannot sit amber
  above a window saying there is nothing to do.
- **and the decisions are under test** — the view models moved out of the
  `net10.0-windows` tray assembly into one the `net10.0` test project can
  reference, which is what makes any of the above assertable. See
  *Testing approach*.

The Pairing tab, folded in — choosing which tab to open on treated the
symptom. The disease was that a code box a machine uses once had the leftmost
tab of the window to itself, beside a copy of four facts the Status tab
already carried, so a technician on a server that paired months ago could
still walk into a screen with nothing on it to do. It is now a row on Status,
under the state it is the remedy for.

- **the row carries state, history and remedy** — `Paired` / `Not paired yet`
  / `Revoked by ADL`, the moment it last paired beneath that, and the code box
  beneath *that* when there is a code to type. It is the shape the ADL row
  above it already had, where an address that has not been set carries what to
  do about it — so the page has one rule rather than two, and `RePairNeeded`
  is no longer printed at a technician in the enum's own words.
- **a working machine can still pair again** — quietly, from a *Pair again…*
  line rather than a standing box. This is not a courtesy: ADL rotates a
  pairing code **without revoking the token it replaces**, deliberately, so a
  machine still shipping data does not stop between an administrator's click
  and a technician getting round to typing the code in (see
  `AgentDevice.issue_pairing_code` in `adl-agent-plugin`). A machine being
  rotated therefore stays `Paired` and never asks for anything, and a box that
  appeared only on failure would leave whoever is holding that code with
  nowhere to put it. It closes again on **Cancel**, taking the half-typed code
  with it — offered only on a machine that is paired, because the window hides
  rather than closes and a box opened by mistake would otherwise still be
  standing open tomorrow.
- **ADL's facts appear once ADL has said anything** — gated on the machine
  having *ever* paired, and pointedly not on its being paired now. The two
  read the same on a new install and come apart on a revocation: a machine cut
  off this morning wants its last heartbeat, its last sync and its last
  problem on screen more than at any other time in its life. The strip above
  every tab is gated on the same question, so it cannot go on announcing a
  scan interval the machine is not keeping.
- **ADL's verdict is in words** — the heartbeat answer carries the state ADL
  stores, `cycle_stuck`, and the plugin keeps the words for it on its own side
  of the wire — so the row and the sentence above it printed an identifier at
  a technician. The tray renders them instead: *Collecting and sending*,
  *Heartbeats are late*, *No heartbeats arriving*, *Alive but not scanning*,
  *Nothing reported yet*. The pair worth separating is the middle two: both
  mean nothing is arriving, and the difference is whether somebody has to walk
  to the machine. Rendered here rather than asked for, because 26 instances do
  not upgrade together and a phrase that only arrived from a new enough ADL
  would leave the old ones showing exactly what this fixes; a state this build
  has never heard of loses its underscores rather than its readability.
- **and one Refresh, not two** — the button on Status re-read the local
  service, which the window already does every five seconds on its own, while
  the similarly-named one above the connection list was the one that actually
  called ADL. The first is gone; the second is **Sync with ADL**, which is the
  word the rest of the window uses for it.

Repointing a machine ([wmo-raf/adl#292](https://github.com/wmo-raf/adl/issues/292))
— there was no supported way to change a machine's ADL address after it was
installed. The URL is written to `agent.ini` by the MSI and read once at
start-up, so changing it meant an administrator editing a file inside a folder
whose permissions the MSI has replaced with SYSTEM and Administrators, and then
restarting the service by hand. `adl-agent set-url` is all three, run elevated.

- **it refuses what the agent would refuse** — the same rule
  `AgentOptions.ResolveApiBaseAddress` enforces, asked before anything is
  written. A verb that accepted plain HTTP to somewhere other than this machine
  would produce a machine that installs cleanly and never reports.
- **the pairing goes with the address** — a device token is issued by one
  instance, so by default the repoint drops it, along with the configuration
  cache and the sweep log that came from the same place. `--keep-pairing` is
  the door for a country moving its instance to a new domain with the same
  database, where clearing would mean re-pairing a whole fleet one
  admin-issued code at a time.
- **it restarts the service itself** — it is already elevated, so there is no
  reason to leave a half-applied change and a sentence asking somebody to
  restart something.
- **it is not a control-surface command** — the pipe can pair the device and
  rebind a station's folder, and any interactive logon session can reach it.
  Redirecting a machine's entire outbound path belongs behind the operating
  system's own consent.

The installer asks ([wmo-raf/adl#293](https://github.com/wmo-raf/adl/issues/293))
— the MSI had no user interface at all. Double-clicking it gave an elevation
prompt and a progress bar, installed the service, started it, and produced a
machine that had no idea where to report and said nothing about it. The
supported way to configure one was `msiexec /i … ADLURL=…`: a line somebody has
to get right, unquoted, over a phone, on a machine they cannot see. The person
running it is a station technician who was told to run the installer, and they
double-click it.

- **one screen, one field** — welcome, the address, confirm, install. Four
  screens is already more than a technician should have to read, and every one
  that is not the address is one they click through.
- **it refuses what the agent would refuse, and says why** — the same rule
  `AgentOptions.ResolveApiBaseAddress` enforces, said again in Windows
  Installer's condition syntax, with *Next* unavailable until the field holds
  an address the service would accept. The two copies are checked against each
  other over a table of addresses (`InstallerDialogTests`), because nothing
  else could: WiX stores a control condition as an opaque string and never
  parses it.
- **it does not contact the address** — that would be a network call inside a
  Windows Installer transaction, and it would strand every site that installs
  before its firewall rule, its DNS entry or its certificate exists. Whether
  the instance answers is what the tray's *Status* tab says, live, for the
  machine's whole life rather than once.
- **the unattended path is untouched** — a self-update is `msiexec /i … /qn`
  with no properties, which shows no dialog and, because `ADLURL` stays empty,
  does not install the component that writes the setting. That is the step
  that could have quietly broken every automatic upgrade in the fleet, so it
  is installed and upgraded for real on the packaging job's Windows runner
  rather than assumed.
- **somebody who has already given an address is not asked for it** — a
  package passed `ADLURL=` goes straight to the confirmation, so the command
  line every existing document uses is no worse than it was before the screen
  existed.
- **an upgrade run by hand asks again, and is not offered a guess** — a major
  upgrade is a fresh install as far as Windows Installer is concerned, so it
  arrives with an empty field. The installer's own registry value is sitting
  right there, but `adl-agent set-url` writes `agent.ini` without touching it,
  so it goes stale: a screen that offered it would quietly send a machine back
  to the instance somebody had deliberately moved it away from, and without
  clearing the pairing that move cleared.

The tray can do it too ([wmo-raf/adl#295](https://github.com/wmo-raf/adl/issues/295))
— the Status tab showed the machine's ADL address as its first row, read-only,
beside a hint naming a command. **Change…** beside it opens that address for
editing and saves it through the verb above.

- **the tray does not elevate, and never will** — its manifest is `asInvoker`,
  because a technician without administrator rights is who the window is for.
  Saving launches `adl-agent set-url` with the `runas` verb: Windows raises its
  own consent prompt, the verb does the whole job, and the tray goes back to
  polling and shows the machine reconnecting.
- **the prompt is the design** — the alternative, a sixth control command
  letting the service write the address on the tray's behalf, would let
  anything running in an interactive session point the agent at a host of its
  choosing, silently, while the window went on looking healthy. The pipe can
  pair this device and rebind a station's folder; it must not be able to move
  the machine.
- **the button is never hidden** — including from whoever cannot use it. The
  consent prompt is exactly where an administrator standing beside a technician
  types a password, which is how these visits actually go. What the window must
  not do is hide the button, or pretend the change succeeded: an administrator
  who declines the prompt is told nothing was changed, and the dialog stays
  open.
- **it refuses before it prompts** — an address the agent would refuse is
  refused in the window, by the agent's own rule, so nobody is asked for a
  password to write something that was never going to work.
- **the pairing choice is stated, not buried** — *"The same ADL at a new
  address — keep the pairing"* is off by default and says underneath it what
  either reading will cost. After a change with the pairing cleared the window
  moves to the Status tab, where the code box is.
- **deliberately a thin caller** — everything that decides anything is the
  verb, so a machine with no desktop and a machine with one end up in the same
  state by the same code. What is tested here is the handful of decisions that
  are the window's own.

## Structure

```
src/AdlAgent.Core      platform-neutral: everything that makes the agent the agent
src/AdlAgent.Windows   the Windows head: service host, Windows providers, named pipe
src/AdlAgent.Tray      the technician's window and notification-area icon
src/AdlAgent.Tray.ViewModels  what that window shows and what its buttons do
tests/AdlAgent.TestSupport  the fake ADL server and the fake platform providers
tests/AdlAgent.Core.Tests   behaviour, driven at the seams
packaging/             the MSI, the per-user package, and the release index
```

The tray compiles on any operating system (`EnableWindowsTargeting`), which
is why it is in the solution rather than beside it: a broken binding is found
by the Linux CI job and by whoever is working on a Mac, not by the next
person who happens to be on Windows. Running it needs Windows.

The split between the last two of those is about testing rather than about
platforms. `AdlAgent.Tray` is `net10.0-windows` because WPF is, and a
`net10.0` test project cannot reference one of those — so everything that
holds a decision (which state the machine is in, what to tell somebody to do
about it, which fields differ from what ADL sent, when a poll may replace a
row somebody is typing into) lives in `AdlAgent.Tray.ViewModels`, which is
plain `net10.0`, and what is left in the window is layout. `ArchitectureTests`
checks that it stays that way, because the way it comes undone is somebody
adding the next view model to the project the window is in, where it compiles
perfectly and can never be tested.

`AdlAgent.Core` contains no platform conditional, anywhere. Platform
specifics enter through five named seams, implemented per head and injected
at the composition root (`WindowsAgentHost.CreateBuilder`):

| Seam | Interface | Windows | Linux (later) |
|---|---|---|---|
| File metadata | `IFileMetadataSource` | streaming enumeration; window on max(last-write, creation) | statx birth time where available, else mtime |
| File readiness | `IFileReadinessProbe` | stability window + shared-read probe | stability window only |
| Host lifecycle | `IHostLifecycle` | Windows Service; state under `%ProgramData%` | systemd; state under `/var/lib` |
| Control surface | `IControlSurface` | named pipe | unix domain socket |
| Replacing itself | `IUpdateInstaller` | Windows Installer, or Velopack for a per-user install | package manager or unit file |

A platform check appearing in the core means a seam is missing. That rule is
enforced by a test (`ArchitectureTests`), not only by review.

## Build, test, publish

Requires the .NET SDK pinned in `global.json` (10.0).

```bash
dotnet build
dotnet test

# the two self-contained programs
dotnet publish src/AdlAgent.Windows/AdlAgent.Windows.csproj -c Release -r win-x64 -o publish/service
dotnet publish src/AdlAgent.Tray/AdlAgent.Tray.csproj -c Release -r win-x64 -o publish/tray
```

```powershell
# and what a country server is actually installed from (Windows only)
./packaging/pack.ps1 -Version 0.2.0
```

`adl-agent.exe` and `adl-agent-tray.exe` each carry the .NET runtime inside
them, so nothing needs to be preinstalled on the target machine. Windows
Server 2016 is the tested floor; 2012/2012 R2 are best-effort legacy.

## Running it

Point the agent at an ADL instance. An installed machine is configured by its
installer, which writes `%ProgramData%\ADL Agent\agent.ini`:

```ini
[Agent]
AdlBaseUrl=https://adl.example.org
```

A developer's build reads `appsettings.json`, the environment
(`Agent__AdlBaseUrl`), or the command line, in that order of increasing
precedence — the machine's own settings file sits below all three, so
pointing a build somewhere else never means editing the installed
configuration back afterwards.

```json
{ "Agent": { "AdlBaseUrl": "https://adl.example.org" } }
```

It must be `https` — the device token travels on every call, and the agent
refuses to start against plain HTTP to anywhere but this machine. TLS 1.2 is
the floor.

A machine with **no** address is a state the agent knows it is in, rather than
one it fails in. It starts, holds its control surface open so the tray and
`adl-agent status` can say what is wrong, and runs none of its network loops —
there is nowhere to send, so there is no call to make and nothing to retry.

```
ADL:      not configured
Problem:  No ADL URL is configured. Set Agent:AdlBaseUrl to the address of the ADL instance this machine sends to.
Fix:      An administrator must run, from an elevated command prompt: adl-agent set-url https://your-adl.example.org -- which writes AdlBaseUrl under [Agent] in C:\ProgramData\ADL Agent\agent.ini and restarts the ADL Agent service.
Version:  0.2.0
```

The *Fix* line is the tier's own: the per-user tier is told `setx
Agent__AdlBaseUrl …` instead, because it has no administrator to call on. An
address that is refused — plain HTTP to somewhere other than this machine, or
something unparseable — reads the same way and says which it was; the agent
will not quietly send a device token over a link it does not trust.

Nothing re-reads the setting in place: `agent.ini` is read once at start-up
and the environment is taken at logon, so whatever sets the address restarts
the agent, and it comes up working. On an installed service tier that whatever
is [`adl-agent set-url`](#changing-where-a-machine-reports).

Run `adl-agent.exe` with no arguments to run it as a console process. On a
real machine it is installed rather than run, and the installer asks for the
URL: double-click `AdlAgent-0.2.0-x64.msi` and one screen in the middle of it
wants the address of the ADL instance this machine reports to. It refuses an
address the agent would refuse, and says why, while somebody is still standing
there — but it does not contact it, because a site is often installed before
its firewall rule or its certificate exists. Whether ADL answers is what the
tray's *Status* tab is for.

For a fleet, or a script, the same setting is still a property:

```powershell
msiexec /i AdlAgent-0.2.0-x64.msi ADLURL=https://adl.example.org
```

Given it, the installer does not ask; given `/qn`, it shows nothing at all,
which is how the agent installs a new version over itself.

That is the service tier. A technician without administrator rights runs
`AdlAgent-0.2.0-Setup.exe` instead, which installs under `%LocalAppData%` and
starts the agent at logon rather than at boot. Both are built by
[`packaging/pack.ps1`](packaging/README.md), and both keep themselves current
from the ADL instance they are paired with.

### The tray

`adl-agent-tray.exe` is what a station technician uses. It puts an icon in the
notification area — green when the machine is paired, synced and ADL is
answering; amber when it is not yet doing its job, whether or not the person
who can change that is standing at it; red when the service is not running —
and opens a window with two tabs: **Stations** and **Status**.

Amber covers waiting as well as acting, deliberately. A machine paired ten
seconds ago whose administrator has not linked a station to it yet is not
collecting anything, and green there would say it was. The line in the window
is what says whether the next move is the technician's or somebody else's;
the colour only says whether this machine is working.

It opens on the tab that matches the machine — Status while there is something
to do to the machine itself, Stations once there is not — and every tab carries
one line at the top saying what to do now and who has to do it, including when
the answer is that there is nothing to do. The line follows the machine on the window's own
poll, so nobody has to press anything to find out that an administrator has
linked a station.

It is a per-user program and asks for no administrator rights. The installer
puts it in the Start menu and starts it at logon. It can be closed at any time; the service goes on collecting and
sending with nobody logged on, which is what it is a service for.

#### Binding a station to a folder

The **Stations** tab is two panes. On the left, under the heading
**Connections**, are the connections ADL has given this machine — a country
server often hosts two vendors' folders — each row naming the connection, how
many stations are under it, and what it needs in one line. Beneath the heading
until somebody clicks a row, and then never again for that session, is the one
sentence the pane needs: *"Click one to see its linked stations."* It cannot be
keyed on nothing being selected, because the window picks a connection from
the machine's first answer and so there is no such moment; it is keyed on the
technician having chosen rather than the window having.

On the right, headed **Station links for** whichever connection is selected,
are that connection's stations: one row each, carrying a status dot, the
station link's ID — the number a support conversation is conducted in, and
named *Link ID* because the row also has the station's own identifier, the one
a vendor's filenames usually carry — the folder and pattern it currently holds,
when ADL last received anything for it, what the last cycle did, and what went
wrong if anything did. The heading is there because the Connection column went
when the pane arrived, which left the grid's scope stated nowhere but by a
highlight in another control.

#### Whether anything is actually arriving

Every other column on that row is about what this machine *did*. All of them
read healthy for a station that is configured perfectly and sending nothing —
the logger died, the share was unmounted, the vendor changed what it writes and
the pattern stopped matching. Nothing fails; nothing arrives.

The machine cannot answer that on its own. It deliberately keeps no record of
what it delivered — the vendor's folder is its only state — so after a restart
its memory of every station is empty. ADL's ledger is the only party that
remembers, so ADL sends it: each station link carries when ADL last received
anything for it, and each connection carries how long one of its stations may
say nothing before that counts as quiet.

The **Status** column is the reading of that, in the same four marks the
connection pane uses:

| | |
|---|---|
| 🟢 green | ADL received a file inside this vendor's window |
| 🟠 amber | configured, blaming nothing, and silent — including never having sent |
| 🔴 red | nothing can arrive and it is visible from here: no folder bound, or the last cycle reported a problem |
| ⚪ grey | switched off in ADL, so there is nothing to judge |

Grey is the absence of a verdict rather than a fourth one. Green would claim
data is flowing for a station nothing is scanned or sent for, and amber would
send a technician hunting a fault that is an administrator's deliberate choice.
It is also the only place in the grid that says a station is switched off at
all.

A station that has never sent anything is amber rather than a state of its own,
which makes the dot a confirmation signal for the commonest reason this window
is open: bind a folder, watch the row turn green on the next cycle.

Beside it, **ADL last received** is the moment itself — absolute, in this
machine's own timezone like every other moment in the window, because a
relative string is only ever as fresh as the poll that wrote it. The age is on
the tooltip, and it advances on the window's own poll rather than only when
something else about the station moves.

The window is six hours by default and is set per connection in the ADL admin,
because a cadence belongs to the vendor's software and not to the station it
happens to be writing for. Raise it for a vendor that legitimately writes one
file a day: a row that is amber every night by design is a row people stop
reading.

A quiet station reaches the line at the top of the window and the icon in the
notification area, like every other thing that wants a person — naming one
station, and saying what to do about it:

> **Kakamega, under Vaisala AWS, has sent nothing to ADL since 24/08 06:10.**
> Open the Stations tab, select it, and check status — the folder may be empty,
> or its pattern may no longer match what the vendor is writing.

That is a check rather than a fix, deliberately. Nobody standing at the machine
can know from here which of the three it is, but all three are answered by
looking.

No column is starred, so the grid overflows and scrolls sideways rather than
squeezing: a problem message has no useful maximum length, and a starred
column always shrinks to fit the window, which is what stopped one from ever
being readable in full. Each column has a ceiling as well as a floor, so one
pathological path cannot push the rest off the right-hand edge.

Above the pane, beside its heading, is **Refresh**: it asks ADL for this
machine's configuration now. Machine-wide, and so there rather than repeated
down the rows, because ADL's sync serves the whole device in one answer and a
Refresh on each connection would promise a scope it does not have. It asks for
the configuration and nothing else — no scan, no upload — because "what is this
machine meant to be doing" and "do it now" are different questions with
different waits, and the second one is on the station rows.

It is grey while an answer is owed, and the answer lands in the line along the
bottom: *"Synced with ADL. Configuration is now at version 42."*, or *"ADL is
not answering, so this machine is still working from the configuration it last
received."* The second sentence is why the attempt is tracked at all — a sync
against an unreachable ADL comes back with the configuration off the disk
rather than with nothing, which is right for the cycle and would otherwise
read here as a refresh that succeeded and changed nothing.

Arrow keys move between connections and Enter or Right moves into the stations
beside them. Right-clicking a row selects it and offers **Edit settings…**,
**Check status…** and **Collect now…**.

The split is not a filter. A connection was a value repeated down a column
before this, which left two facts with nowhere to be said: a connection ADL
had switched off arrived only as a false on each of its stations, so the
window blamed the stations for an administrator's decision; and a connection
with no station links left no trace at all, so an administrator who had made
one and not yet linked to it looked, from the machine, exactly like one who
had done nothing. Both are sentences on a connection row now.

The window opens on the connection the next-step line is pointing at, and
that line names it — *"Bind a folder to Kisumu, under Vaisala AWS"* — because
"open the Stations tab and select it" stopped being a complete instruction
the moment the list had two levels. It is picked once, from the first answer
the service gives, and never moved again: a pane that re-chose on every poll
would drag somebody off the connection they were reading, five seconds after
they opened it.

**Edit settings…** on a row — or double-clicking it, or pressing Enter on it —
opens that station's settings in a window of its
own, titled with the station *and* its connection, because pointing a station
at the wrong vendor's folder is a mistake nothing refuses and which surfaces a
cycle later as "scanned 0".

What is set there is where this station's files are and how they are named;
the decoder, the variable mappings and the collection start date stay in the
ADL admin. **Browse…** beside the folder box opens Windows' own folder picker,
starting at the folder the box already names (or, if that one is not there, at
the nearest folder above it that is). Underneath the settings, a live count
says how many files these settings would pick out of that folder, updated a
moment after typing stops and once as the window opens.

Nothing leaves the machine until **Save to ADL**, which is grey until a box
differs from what ADL sent — the line beside it says which of those two it is,
and afterwards says what ADL answered. The window closes once ADL has taken
the settings; a refusal keeps it open, because what it is showing is the thing
that has to change. Closing without saving throws the edits away and asks
first. The list behind the window stops refreshing while it is open, so the
row cannot move out from under it, but the header, the next-step line and the
colour of the icon in the corner all go on following the machine.

#### Reading a station without changing it

**Check status…** on a row opens the same station read-only. The grid holds
eight columns and a station has more than twice that many facts, so the ones
wanted least often — the WIGOS id, the timezone the filenames are written in,
the watermark ADL is asking from, whether HQ has this station switched off —
were reachable only by opening the settings window, which is a window for
changing things, over a station nobody wanted to change. *ADL last received* is
on it too: the dot's tooltip sends people here, and the number that dot is the
reading of should be on the window it sends them to.

At the top of it, above everything ADL sent, is a count of what the station's
folder holds *now*. Every other line on that window is a memory of the last
sync; this is the only one that is true of the machine at the moment somebody
is reading it, and *"scanned 0, no error"* is answered by it and by nothing
else. It runs as the window opens, with no settings laid over the stored ones,
so what is counted is exactly the configuration the cycle will use — and
**Check again** re-runs it, for the common case of having this window open on
one screen while a share is being granted on another.

Nothing on it writes, which is what makes it safe to open on a station a cycle
is in the middle of. Like the settings window it is modal and stops the list
behind it rebuilding, for the same reason: it holds a copy of a row.

#### Collecting one station now

**Collect now…** on a row runs a cycle for that station immediately, in a
window that shows where it has got to — syncing, scanning, offering — with the
counts moving and a **Cancel**. **Close** stops watching; the run belongs to
the service and goes on either way.

It is the scheduled cycle over a configuration narrowed to one station link,
which is the point: the sweep planner, the scanner, the pager and the uploader
are the ones the loop uses, so a station collected this way is collected
exactly as it would have been an hour later. Its neighbours' folders are never
walked, so a machine serving forty stations does not pay for thirty-nine of
them.

Three things about it are deliberately not the scheduled cycle's behaviour:

- **It always sweeps.** The reason somebody presses this is almost always that
  they have just put files in the folder, and a backfill copied in with its
  original timestamps preserved is invisible to the candidate window — so a
  collect-now that only looked at the window would report "nothing new" to the
  one person who knows there is something. It offers the whole folder back to
  the collection start date, and records the sweep, so the station's next daily
  one is a day from now rather than a day from whenever the loop last got to
  it. It does not prune the sweep log the way a full cycle does: its plan knows
  one station, and pruning on that would empty the log on every press.
- **It is refused rather than queued when a cycle is running.** *"A cycle is
  already running on this machine — Kisumu will be collected as part of it."*
  A queued run would start minutes after the button, against a window nobody
  still has open. The scheduled cycle, conversely, waits for a collect rather
  than being skipped: a cycle silently dropped because somebody was pressing a
  button is the sort of gap that reaches HQ as a machine that has quietly
  stopped.
- **Its result does not reach the heartbeat.** Recorded as a cycle, a run
  covering one station of forty would reach ADL as a cycle that had just
  finished having scanned one — and ADL's own cycle-stuck and coverage checks
  would read that as the machine having stopped collecting the rest. So it sits
  beside the cycle instead, and the row labels it: *"on request: 412 seen, 12
  sent, 0 failed"*, until a scheduled cycle overtakes it with fresher numbers
  and the row goes back to showing that.

The item is grey when there is nothing for it to do — the station is switched
off in ADL, or no folder is bound to it yet — with the reason on its tooltip.
The service refuses either case anyway, because HQ can switch a station off
between a row being drawn and the item on it being pressed, so the refusal
always comes from the thing that knows.

Nothing is repaired by cancelling, and nothing needs to be. The agent keeps no
record of what it delivered, so files a stopped run did not reach are offered
again by the next cycle exactly as if it had never run.

#### Changing where this machine reports

The **Status** tab's first row is the ADL address, and **Change…** beside it
opens it for editing. The tray never elevates — its manifest is `asInvoker`,
and a technician without administrator rights is who the window is for — so
saving launches `adl-agent set-url` with the `runas` verb and Windows raises
its own consent prompt. Approve it and the machine reports to the new address
without a reinstall; decline it and nothing changes, which the window says
rather than closing over.

That prompt is the design rather than a wart. Redirecting where a country's
observations are sent is an administrative act, and the alternative — a control
command letting the service write the address on the tray's behalf — would let
anything running in an interactive session point the agent at a host of its
choosing, silently, while the window went on looking healthy.

The button is there on every machine the service has answered for, including
one whose technician has no rights: the prompt is exactly where an
administrator standing beside them types a password, which is how these visits
actually go. It is there on a machine with no address at all, too — that is the
state the hint under this row is about, and the button is the same command with
the typing done.

Whether the pairing survives is stated on the dialog: **The same ADL at a new
address — keep the pairing** is off by default, with a line underneath saying
what either reading costs. Left off, the machine is unpaired when the service
comes back and the window moves to the Status tab, where the code box is. An
address the agent would refuse is refused here, before any prompt is raised.

Everything else is [`adl-agent set-url`](#changing-where-a-machine-reports)
itself — the validation, the pairing, the restart — so a machine with no
desktop and a machine with one end up in the same state by the same code. The
only thing this window cannot show is the verb's own output: `runas` requires
`UseShellExecute`, which forbids redirecting the standard streams, so a change
that does not finish reports its exit code and names the command to run in a
window where its words can be read.

#### Folders the service cannot see

The window browses as the technician standing at the machine. The service
collects as **LocalSystem**, and the two do not see the same filesystem:

- **A mapped drive letter cannot work.** Drive mappings belong to a logon
  session and LocalSystem has none, so `Z:\VendorData` is a real folder in the
  picker and a path that does not exist to the service, permanently. Browse
  rewrites a mapped letter to the `\\server\share` form it points at, where
  Windows will say what that is.
- **A share is reached as the machine, not as you.** `\\nas\met\garissa` that
  a technician can read is reached by the service as `DOMAIN\MACHINE$`, so the
  share has to grant that account read access.

Both are called out under the folder box when a path is one of them. Without
that, both come back from the file count as *"Nothing was found in this
folder. Check that the path is right and that this machine can read it."* —
which is true, and which is also what a mistyped folder name says.

#### Finding a broken binding

A WPF binding whose path is wrong neither throws nor draws: the label is
empty, and looks exactly like one whose value the service did not send.
Nothing catches that — XAML compiles with the path unchecked, and the window
is deliberately not automated — so the tray can be asked to write WPF's own
binding failures to a file:

```powershell
$env:ADL_AGENT_TRAY_BINDING_LOG = "$env:TEMP\adl-tray-bindings.log"
.\adl-agent-tray.exe
```

An empty file (past its header) means every binding in the window resolved.
Off unless that variable is set: a binding is right or wrong for the whole
fleet at once, so this is a tool for whoever is building or testing the tray,
not something to write to a technician's disk every session.

### Pairing

Ask your ADL administrator to create the device in the admin and give you the
pairing code, then paste it into the tray's Status tab, under **Pairing** —
or, on a machine with no desktop:

```powershell
adl-agent pair KX7M-93QA
adl-agent status
```

Both talk to the already-running service over the `adl-agent` named pipe,
using the same control protocol the tray uses. The device appears in the ADL
admin's fleet listing within a heartbeat.

### Changing where a machine reports

An installed machine's address is in `agent.ini`, in a folder only SYSTEM and
Administrators may write, and it is read once when the service starts. One
verb, run from an elevated command prompt, does the whole job:

```powershell
adl-agent set-url https://adl.example.org
```

```
ADL:      https://adl.example.org
Written:  C:\ProgramData\ADL Agent\agent.ini
Pairing:  cleared. Pair this machine again: adl-agent pair <code>
Service:  restarted, and reading the new address.
```

It refuses anything the agent itself would refuse — plain HTTP to anywhere but
loopback, something unparseable, nothing at all — with the reason, and writes
nothing when it does. The rule is the one `AgentOptions` enforces at start-up,
asked here instead of discovered on a machine that installed cleanly and then
never reported.

**The pairing goes with the address.** A device token was issued by one ADL
instance, and the next sync after a repoint would otherwise send it to a host
named by whoever typed the URL. So the default is: change the URL, lose the
pairing, pair again. The configuration cache and the sweep log go with it —
they are the old instance's stations, and its station link ids, which the new
instance issues to entirely different stations.

The service is stopped before any of that and started after it, in that order
rather than restarted at the end: it rewrites the configuration cache on every
sync and the token on a `401`, so clearing them underneath a running service is
a race the machine would sometimes lose — and come back paired to an instance
it is no longer pointed at.

```powershell
adl-agent set-url https://adl.example.org --keep-pairing
```

`--keep-pairing` is the one case where clearing is wrong: a country moving its
instance to a new domain, same database, same tokens, where the default would
mean re-pairing every machine in the fleet one admin-issued code at a time,
each code with a 72-hour life.

The tray's Status tab calls this verb, and only this verb: **Change…** on
the ADL row launches it with the `runas` verb, so the machine a technician
repoints from the window and the machine an administrator repoints from a
command prompt end up in the same state by the same code.

Run without administrator rights it says so and changes nothing, rather than
failing on a file permission. A machine with no state folder is told it is not
an installed agent rather than having one created for it: the MSI locks that
folder to SYSTEM and Administrators because the device token is stored in it in
the clear, and a folder made here would inherit whatever `%ProgramData%` grants. Nothing else in the product can do this: it is
deliberately not a control-surface command, so a machine with no desktop — or
one whose tray will not open — still has a supported way to be repointed.

Only the settings file changes; everything else in it, including
`AutoUpdate=false` and any comments, is left where it was. The per-user tier
has no service and no elevation, and is still pointed by `setx
Agent__AdlBaseUrl` before the next logon (see *Known gaps*).

### The control surface

One protocol, one JSON object per line — served over a named pipe on Windows
and (later) a unix socket on Linux, and implemented once in the core so that
both heads mean the same thing by each of them:

| Command | What it does |
|---|---|
| `status` | What this machine is: pairing state, fleet status, cadences, last error |
| `pair` | Redeem a pairing code |
| `stations` | Every station ADL linked to this device, its local binding, and its last cycle |
| `preview` | Count what a folder and a pattern would match, saving nothing |
| `configure` | Write one station's app-tier settings through to ADL |
| `sync` | Ask ADL for this device's configuration now |
| `collect` | Run a cycle for one station now |
| `collect_status` | Where that run has got to |
| `collect_cancel` | Stop it |

The last four all **start** something and answer at once rather than waiting
for it, and that is the surface's shape rather than a preference. It serves
one client at a time and times out in three seconds, so a command that waited
for an HTTP call — let alone an upload of a station with months of backlog —
would hold the only slot for its duration: the tray's own status poll would
stall, and with it the header, the next-step line and the colour of the icon
in the corner. Worse, the poll would time out and report a working service as
absent.

So the answer to `sync` is the attempt, and its outcome arrives on the next
`status` as `requested_sync`. The answer to `collect` is the run, and its
progress arrives on `collect_status`, asked once a second in short round trips
that leave the surface free between them. That is also what makes
`collect_cancel` possible at all: a held connection has nowhere for a second
command to arrive.

The pipe carries an explicit ACL: the service's own account and the machine's
administrators in full, the technician's interactive logon session enough to
hold the conversation, and the network denied outright — Windows publishes
named pipes over SMB, and this one can pair the device and move where a
station's data is read from.

## Trying it against a local ADL

With the [`adl-agent-plugin`](https://github.com/wmo-raf/adl-agent-plugin) dev
stack running (`docker compose up`, admin on `http://127.0.0.1:8099`):

```bash
# 1. create a device in the ADL admin (Agent Devices -> Add) and issue its
#    pairing code, or from the plugin repo:
docker compose exec adl adl shell -c \
  "from adl_agent_plugin.models import AgentDevice; \
   d,_ = AgentDevice.objects.get_or_create(name='Dev laptop'); \
   print(d.issue_pairing_code())"

# 2. run the agent (loopback is the one place plain HTTP is allowed)
dotnet run --project src/AdlAgent.Windows -- \
  --Agent:AdlBaseUrl=http://127.0.0.1:8099 \
  --Agent:StateDirectory=/tmp/adl-agent-state

# 3. in another terminal
dotnet bin/.../adl-agent.dll pair XXXX-XXXX
dotnet bin/.../adl-agent.dll status
```

`status` should report `Paired` and, within a few seconds, `Fleet: online`.
The device then shows its version, last-seen and clock skew in the admin's
Agent Devices listing.

To watch a whole cycle, point a station link at a folder on this machine
(Agent Station Links → Local Folder Path and File Pattern in the admin, or the
config endpoint), drop a file into it, and wait out the check interval:

```bash
printf 'timestamp,temp\n2026-08-21 09:00:00,21.4\n' > /tmp/vendor/DEMO_20260821.csv

docker compose exec adl adl shell -c \
  "from adl_agent_plugin.models import AgentStationDataFile; \
   print([(f.file_name, f.size, f.status) for f in AgentStationDataFile.objects.all()])"
```

Append another row to the same file and it is offered again on the next cycle,
because its hash changed — one ledger row, updated in place. Leave it alone and
the next manifest offers it and ADL asks for nothing.

On macOS and Linux pass `--Agent:StateDirectory`: the Windows head keeps state
under the platform's common application data folder, which is not writable
there.

## Known gaps

- **The per-user tier has no way to be configured that does not need a
  command line.** The service tier asks: its MSI has a screen for the address
  and writes `agent.ini` from it. The per-user tier's installer has no screen
  of its own, no property to be given, and no elevation available to the
  technician it exists for, so the only route is an environment variable set
  before the next logon:

  ```powershell
  setx Agent__AdlBaseUrl https://adl.example.org
  ```

  That is a command line on the one tier whose whole reason for existing is
  somebody who should not need one. It is a knowing trade rather than an
  oversight — it needs no administrator, which is the property that tier
  cannot give up — and the agent says so on the machine itself: a machine
  with no address reports that it has none, the tray opens on the tab that
  says so, and its next-step line carries this command rather than the service
  tier's answer. What it still cannot do is take the address: closing this
  properly means the tray writing the setting itself, which is more than
  [#297](https://github.com/wmo-raf/adl/issues/297) did — it made the state
  legible, not fixable from the window.
- **A folder the technician can see is not necessarily a folder the service
  can read.** The tray is per-user and the service is LocalSystem, so a drive
  letter mapped in somebody's session does not exist for the thing that will
  do the collecting, and a share they can read is reached by it as this
  machine's own account. The window says so where it can — see *Folders the
  service cannot see* above — and Browse rewrites a mapped letter to its
  `\\server\share` form. What none of that fixes is a share that simply does
  not grant `DOMAIN\MACHINE$` read access: the station is configured, ADL
  accepts the path, and every cycle collects nothing. Deployments that read
  from a share have to grant the machine account, and there is nothing on the
  machine that can grant it for them.
- **The device token is stored unencrypted** in `state.json` under
  `%ProgramData%\ADL Agent`. The MSI replaces that folder's permissions with
  SYSTEM and Administrators, so on an installed machine the token is only
  readable by an administrator — but it is readable, in the clear, by any of
  them. A copy run from a folder somebody unzipped has whatever permissions
  that folder inherits.
- Pairing is not confirmed against ADL before `pair` reports success — the
  token is stored and proven by the sync and heartbeat that follow within a
  second or two, which `adl-agent status` then shows.
- **IANA timezone names need ICU, and Windows Server 2016 has none.** ADL
  sends a station's filename timezone as an IANA name (`Africa/Nairobi`), and
  Windows resolves those through a mapping in ICU — supplied by the operating
  system from Windows 10 / Server 2019 onwards. On the older machines in the
  best-effort tier a `DIRECT_FETCH` station whose filenames are written in
  local time may be unable to resolve it. It reports the timezone it could
  not resolve rather than looking for the wrong filenames, so the station
  shows a reason in the fleet listing instead of going quiet.
- **The check interval is shown but not editable.** Decision #260 puts it in
  the app tier, and it is per device; the plugin serves no endpoint for it
  (the only config write is per station link), so there is nothing for the
  tray to write it through. Editing it needs a companion change in
  [`adl-agent-plugin`](https://github.com/wmo-raf/adl-agent-plugin) first.
- **Nobody has watched the installer's screen.** The MSI is now installed and
  upgraded for real on the packaging job's Windows runner
  (`packaging/verify-msi-install.ps1`), which reads the `agent.ini` it wrote
  and checks a silent upgrade leaves it alone — but every install there is
  `/qn`, so the dialog itself is never drawn. What is checked is that the
  package carries it, that the rule it enforces is the agent's rule, and that
  nothing it needs runs when there is no screen. Whether the text fits its
  controls, at 96 DPI and at 150, is unproven until somebody double-clicks it.
- **The per-user installer has still not been run on a Windows VM.** The
  Velopack packaging and the update path it applies were written and reviewed
  on machines that cannot execute either: `vpk` packs Windows releases only
  there. Everything above that line — the feed, the pin, the hash check, what
  is fetched and what is refused — is under test on every push; everything
  below it is CI-built and unproven until somebody installs it on a clean
  Server 2016 box, which is what
  [#282](https://github.com/wmo-raf/adl/issues/282)'s first two acceptance
  criteria ask for.
- **`set-url`'s stop and start are the one part of it a test cannot reach.**
  What it writes, what it clears, the order it does them in and what a refused
  address left behind are all driven at their seams; the stop and start
  themselves are `net stop` and `net start` against the Service Control
  Manager, which no runner in this suite has. A machine
  without administrator rights, or without the service installed, is told so
  in a sentence — but the happy path's final line is unproven until somebody
  runs the verb on a machine with a real service on it.
- **The per-user tier shows a console window at logon.** It is the same
  console program as the service tier, started by a shortcut rather than by
  the SCM, so it appears as a window in the technician's session. That is
  honest about what the tier is — a logon process, not a service — but it is
  a window somebody will eventually close.
- **A self-update is a child process of the service it replaces.** The agent
  starts `msiexec`, and Windows Installer stops the service moments later.
  Nothing kills a child when its parent stops on Windows, so this is the
  ordinary way a service updates itself; it is also the step with the least
  margin, and the one to look at first if a machine ever comes back on its
  old version. Windows Installer's own verbose log is left beside the package
  under `%ProgramData%\ADL Agent\updates`.
- **Nothing is signed.** Pilots ship unsigned and attended, with feed-hash
  verification active from the first build (decision #262). SmartScreen will
  warn on both installers until
  [#283](https://github.com/wmo-raf/adl/issues/283) lands signing.
- **A download that fails part-way starts again from the beginning.** The
  package endpoint streams and the agent writes to a fresh file; neither
  resumes. On the links this product exists for, a forty-megabyte installer
  may take several cycles of luck to land — it costs bandwidth rather than
  correctness, because a partial file fails its hash and is deleted, but a
  machine on a bad link can be a long time updating.
- **Uninstalling leaves the state directory behind**, token included. That is
  the same permanence that lets an automatic update keep a machine paired, and
  the two cannot be had separately without the MSI knowing which of the two it
  is doing. Decommissioning a machine properly means revoking the device in
  ADL — which is what actually stops it sending — and deleting
  `%ProgramData%\ADL Agent` by hand.
- **The two tiers share one state directory.** Both write to
  `%ProgramData%\ADL Agent`, so installing the per-user tier on a machine
  that already has the service tier means two agents with one token file, and
  on a machine where the MSI has locked that folder to administrators the
  per-user install cannot write to it at all. Nothing stops it today; the two
  tiers are meant for different machines.
- **The tray's window is not automated.** That is the spec's decision and the
  window holds nothing worth automating, but it does mean layout mistakes are
  found by looking rather than by CI. Broken bindings at least announce
  themselves — see *Finding a broken binding* above — but nothing checks that
  the window is laid out sensibly except a person opening it. Everything
  underneath — the pipe, the protocol, the five commands, the typed answers
  the window binds to, and every decision the view models make about them —
  is under test.
- **Date-structured folders are not walked.** ADL lets a station link say its
  files sit under dated sub-folders (`dir_structured_by_date`, with a
  granularity and a month format); the cycle walks only the folder itself. No
  ticket covers this — #279 specified flat enumeration and #280 is
  `DIRECT_FETCH` plus the reconciliation sweep — and the range machinery it
  needs (every dated directory from the link's start date to now) is not
  what #280 brought — the sweep only lowers the floor of the one folder that
  is already walked, and `DIRECT_FETCH` builds names rather than directory
  trees. Until a ticket covers it, such a station reports the reason on every
  cycle, under either strategy, rather than quietly collecting nothing.

## Testing approach

Tests assert external behaviour at the seams, never internals: given these
server responses and this platform, these calls happen. The fake ADL server
is real HTTP on a loopback port — the bearer header, the version header, a
401 arriving as a 401, a refused connection arriving as a refused connection
are all things a substituted message handler would paper over. The fake
platform providers let a Linux CI runner describe Windows filesystem
behaviour (a backfilled file carrying an old last-write time, a vendor
process holding its output open) that it could not otherwise produce.

The update path is driven the same way. A test publishes a release to the
fake ADL, pins a device or does not, and reads what the agent did about it:
which package it asked for, whether it fetched one, and what it handed to the
installer. The one thing faked at that end is the platform installer itself,
because the real ones stop this process — so the assertions stop at the
handover, which is also exactly where the last decision the agent makes
is: these bytes, hashed, and they are the ones ADL described.

The local UI is tested the same way and at the same distance. The control
commands run against a real named pipe (a unix socket off Windows) with the
real control service on the other end and the fake ADL behind it, so what is
under test is the conversation a technician's window actually has: paste this
code, list these stations, count this pattern, write this folder to ADL.

The window's view models are driven over that same transport, and by the same
harness: a test arranges an ADL that has linked nothing to this device, or a
station with no folder, or a folder holding the wrong vendor's files, and
reads the sentence a technician would be looking at and the tab the window
would have opened on. That is why they are in a `net10.0` assembly of their
own — see *Structure*. The WPF window above that line is still not automated,
per the spec: what is left in it is layout.

The installer is tested at the same distance, and it is the one part of this
product nothing else could check. WiX stores a control condition as an opaque
string and never parses it, so a malformed one compiles, links, ships, and is
first evaluated on a country server. `InstallerDialogTests` reads the
condition off the dialog's *Next* button, expands it the way the WiX
preprocessor would, and runs it — in a small reader of Windows Installer's own
condition syntax — against the same addresses `AgentOptions` is run against,
so the rule the screen enforces and the rule the service enforces are checked
against each other rather than against a comment. Where they cannot be made to
agree, because that syntax has no regular expressions and cannot take a string
apart, both edges are pinned by tests of their own.

What that cannot say is what Windows Installer does with the package.
`packaging/verify-msi-install.ps1` installs it on the packaging job's Windows
runner, reads the `agent.ini` it wrote, builds the same sources at the next
version, and upgrades silently with no properties — exactly as `UpdateService`
does at three in the morning — then checks the address is still there.
