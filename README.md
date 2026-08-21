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

This repository currently holds the skeleton and the first live vertical
([wmo-raf/adl#278](https://github.com/wmo-raf/adl/issues/278)):

- **pairing** — exchange a pairing code for a device token and store it
- **sync with an offline cache** — fetch the device's configuration every
  cycle, and keep working from the last one when ADL is unreachable
- **heartbeat** — the 5-minute liveness report, on a loop deliberately
  isolated from the scan loop
- **revocation** — a `401` stops the machine sending and surfaces
  "re-pair needed" locally

Still to come: the scan and upload cycle (#279), `DIRECT_FETCH` and the
reconciliation sweep (#280), the WPF tray (#281), installers and
auto-update (#282).

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

## Testing approach

Tests assert external behaviour at the seams, never internals: given these
server responses and this platform, these calls happen. The fake ADL server
is real HTTP on a loopback port — the bearer header, the version header, a
401 arriving as a 401, a refused connection arriving as a refused connection
are all things a substituted message handler would paper over. The fake
platform providers let a Linux CI runner describe Windows filesystem
behaviour (a backfilled file carrying an old last-write time, a vendor
process holding its output open) that it could not otherwise produce.
