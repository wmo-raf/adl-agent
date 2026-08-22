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
- **the station list** — every station ADL has linked to this device, with its
  local folder binding, what the last cycle did for it, and the sentence
  explaining any station that collected nothing.
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

## Structure

```
src/AdlAgent.Core      platform-neutral: everything that makes the agent the agent
src/AdlAgent.Windows   the Windows head: service host, Windows providers, named pipe
src/AdlAgent.Tray      the technician's window and notification-area icon
tests/AdlAgent.TestSupport  the fake ADL server and the fake platform providers
tests/AdlAgent.Core.Tests   behaviour, driven at the seams
packaging/             the MSI, the per-user package, and the release index
```

The tray compiles on any operating system (`EnableWindowsTargeting`), which
is why it is in the solution rather than beside it: a broken binding is found
by the Linux CI job and by whoever is working on a Mac, not by the next
person who happens to be on Windows. Running it needs Windows.

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

Run `adl-agent.exe` with no arguments to run it as a console process. On a
real machine it is installed rather than run, and the installer sets the URL
for you:

```powershell
msiexec /i AdlAgent-0.2.0-x64.msi ADLURL=https://adl.example.org
```

That is the service tier. A technician without administrator rights runs
`AdlAgent-0.2.0-Setup.exe` instead, which installs under `%LocalAppData%` and
starts the agent at logon rather than at boot. Both are built by
[`packaging/pack.ps1`](packaging/README.md), and both keep themselves current
from the ADL instance they are paired with.

### The tray

`adl-agent-tray.exe` is what a station technician uses. It puts an icon in the
notification area — green when the machine is paired, synced and ADL is
answering; amber when something wants a person; red when the service is not
running — and opens a window with three tabs: **Pairing**, **Stations**, and
**Status**.

It is a per-user program and asks for no administrator rights. The installer
puts it in the Start menu and starts it at logon. It can be closed at any time; the service goes on collecting and
sending with nobody logged on, which is what it is a service for.

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
pairing code, then paste it into the tray's Pairing tab — or, on a machine
with no desktop:

```powershell
adl-agent pair KX7M-93QA
adl-agent status
```

Both talk to the already-running service over the `adl-agent` named pipe,
using the same control protocol the tray uses. The device appears in the ADL
admin's fleet listing within a heartbeat.

### The control surface

One protocol, one JSON object per line, five commands — served over a named
pipe on Windows and (later) a unix socket on Linux, and implemented once in
the core so that both heads mean the same thing by each of them:

| Command | What it does |
|---|---|
| `status` | What this machine is: pairing state, fleet status, cadences, last error |
| `pair` | Redeem a pairing code |
| `stations` | Every station ADL linked to this device, its local binding, and its last cycle |
| `preview` | Count what a folder and a pattern would match, saving nothing |
| `configure` | Write one station's app-tier settings through to ADL |

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
- **The tray's view models have no tests.** The spec leaves the window
  unautomated and its layout is not worth automating, but the view models
  underneath it hold real decisions -- which fields differ from what ADL
  sent, when a poll may replace a row somebody is typing into -- and those
  are only covered by reading. They are in a `net10.0-windows` assembly,
  which the `net10.0` test project cannot reference; moving them into a
  platform-neutral library would fix that.
- **Neither installer has been run on a Windows VM yet.** The MSI authoring,
  the Velopack packaging and the two ways an update is applied were all
  written and reviewed on machines that cannot execute any of them: WiX
  refuses to build outside Windows, and `vpk` packs Windows releases only
  there. Everything above that line — the feed, the pin, the hash check, what
  is fetched and what is refused — is under test on every push; everything
  below it is CI-built and unproven until somebody installs it on a clean
  Server 2016 box, which is what
  [#282](https://github.com/wmo-raf/adl/issues/282)'s first two acceptance
  criteria ask for.
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
  the window binds to — is under test.
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
code, list these stations, count this pattern, write this folder to ADL. The
WPF window above that line is not automated, per the spec — what it holds is
layout.
