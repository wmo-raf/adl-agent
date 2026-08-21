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

Still to come: the WPF tray (#281), installers and auto-update (#282).

## Structure

```
src/AdlAgent.Core      platform-neutral: everything that makes the agent the agent
src/AdlAgent.Windows   the Windows head: service host, Windows providers, named pipe
tests/AdlAgent.TestSupport  the fake ADL server and the fake platform providers
tests/AdlAgent.Core.Tests   behaviour, driven at the seams
```

`AdlAgent.Core` contains no platform conditional, anywhere. Platform
specifics enter through four named seams, implemented per head and injected
at the composition root (`WindowsAgentHost.CreateBuilder`):

| Seam | Interface | Windows | Linux (later) |
|---|---|---|---|
| File metadata | `IFileMetadataSource` | streaming enumeration; window on max(last-write, creation) | statx birth time where available, else mtime |
| File readiness | `IFileReadinessProbe` | stability window + shared-read probe | stability window only |
| Host lifecycle | `IHostLifecycle` | Windows Service; state under `%ProgramData%` | systemd; state under `/var/lib` |
| Control surface | `IControlSurface` | named pipe | unix domain socket |

A platform check appearing in the core means a seam is missing. That rule is
enforced by a test (`ArchitectureTests`), not only by review.

## Build, test, publish

Requires the .NET SDK pinned in `global.json` (10.0).

```bash
dotnet build
dotnet test

# what gets installed on a country server: one self-contained file
dotnet publish src/AdlAgent.Windows/AdlAgent.Windows.csproj -c Release -r win-x64 -o publish
```

The published `adl-agent.exe` carries the .NET runtime inside it, so nothing
needs to be preinstalled on the target machine. Windows Server 2016 is the
tested floor; 2012/2012 R2 are best-effort legacy.

## Running it

Point the agent at an ADL instance in `appsettings.json` (or by environment
variable, `Agent__AdlBaseUrl`):

```json
{ "Agent": { "AdlBaseUrl": "https://adl.example.org" } }
```

It must be `https` — the device token travels on every call, and the agent
refuses to start against plain HTTP to anywhere but this machine. TLS 1.2 is
the floor.

Run `adl-agent.exe` with no arguments to run it as a console process, or
install it as a service:

```powershell
sc.exe create "ADL Agent" binPath= "C:\Program Files\ADL Agent\adl-agent.exe" start= auto
sc.exe start "ADL Agent"
```

(The MSI that does this properly, and the per-user tier for technicians
without administrator rights, come with #282.)

### Pairing

Ask your ADL administrator to create the device in the admin and give you the
pairing code, then, on the machine:

```powershell
adl-agent pair KX7M-93QA
adl-agent status
```

Both talk to the already-running service over the `adl-agent` named pipe,
using the same control protocol the tray app will use. The device appears in
the ADL admin's fleet listing within a heartbeat.

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
  `%ProgramData%\ADL Agent`, with whatever ACL that folder inherits. Locking
  the directory down belongs with the installer (#282); until then, treat any
  local account on the machine as able to read it.
- **The named pipe uses the default ACL**, which is enough for an
  administrator running the agent interactively but not for a technician's
  logon session reaching a service running as LocalSystem. The explicit ACL
  lands with the tray (#281).
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
