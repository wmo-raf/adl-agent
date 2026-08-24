# Testing a change on a real Windows machine

The agent is developed on a Mac and runs on Windows. This is the loop for
tier-A work — the tray, the control surface, folder scanning, upload cycles:
everything except the installers, which are `packaging/`'s business.

    edit on the Mac  ->  dev/deploy.sh  ->  double-click run.cmd on Windows

## Why it is shaped this way

Both programs, the WPF tray included, publish for `win-x64` from macOS —
`AdlAgent.Tray.csproj` sets `EnableWindowsTargeting`, so XAML compiles
anywhere and only *running* it needs Windows. So the Mac stays the only
machine with the source, the SDK and the git history on it, and the Windows
machine is a place to run a program.

What ships is two self-contained single files, 115 MB together. Of that,
114.7 MB is the .NET runtime, WPF and WinForms, byte-identical on every
build; 0.29 MB is `AdlAgent.Core.dll`, `adl-agent-tray.dll` and
`adl-agent.dll`. Copying the runtime across the LAN to test a quarter of a
percent of changed code is what makes the loop too slow to use, so the loop
publishes framework-dependent against a runtime installed on the machine
once, and `--ship` builds the real shape for the run before a tag.

## One-time setup

**On the Windows machine**

1. Install the **.NET 10 Desktop Runtime** (x64) from Microsoft. Desktop,
   not the plain runtime: the tray is WPF.
2. Make the folder and share it:

   ```powershell
   mkdir C:\adl-agent-dev\bin, C:\adl-agent-dev\state, C:\adl-agent-dev\data
   New-SmbShare -Name adl-agent-dev -Path C:\adl-agent-dev -FullAccess $env:USERNAME
   ```

   A path with no spaces in it, because `run.cmd` passes it on a command
   line.
3. Next to it, create `C:\adl-agent-dev\dev.local.cmd` — this machine's ADL, and not the
   project's business:

   ```bat
   set ADL_URL=https://adl.example.org
   ```

   `run.cmd` itself is put there by the first `deploy.sh`, and kept current
   by every one after it. It must be `https`. The agent refuses plain HTTP to anywhere but the
   machine it is running on, because the device token travels on every call.

**On the Mac**

Mount the share once — Finder, Go → Connect to Server,
`smb://<windows-host>/adl-agent-dev`. It appears at
`/Volumes/adl-agent-dev`, which is where `deploy.sh` looks unless
`ADL_AGENT_DROP` says otherwise.

## The loop

```bash
dev/deploy.sh          # ~3 s, copies only what changed
```

Then on the Windows machine, double-click `run.cmd`. It stops whatever is
still running, starts the agent in a console window where you can read its
log, and starts the tray.

Windows locks a running program's files, so close the agent's console window
and the tray before deploying. `deploy.sh` says so plainly if you forget.

## Pairing

Once, from the ADL admin: create the device, issue its pairing code, then on
the Windows machine:

```powershell
C:\adl-agent-dev\bin\adl-agent.exe pair KX7M-93QA
C:\adl-agent-dev\bin\adl-agent.exe status
```

Both talk to the already-running agent over its named pipe. The token is
kept in `C:\adl-agent-dev\state`, so pairing survives every redeploy — pair
once and the loop is just relaunching. Delete that folder to start again
unpaired.

State lives there rather than in `%ProgramData%\ADL Agent` on purpose: if
the MSI has ever been on this machine, that folder carries SYSTEM- and
Administrators-only permissions and a console build running as you cannot
write to it.

## The unconfigured machine

```bat
run.cmd --no-url
```

Starts the agent with no ADL address at all. That is the state a scripted
install which omitted the property leaves behind, and it is what
[wmo-raf/adl#294](https://github.com/wmo-raf/adl/issues/294) is about — so
the loop has to be able to reach it deliberately, rather than only being
able to describe it.

## Watching a cycle

Point a station link at `C:\adl-agent-dev\data` in the ADL admin, drop a
file in, and wait out the check interval.

## A broken binding

`run.cmd` always sets `ADL_AGENT_TRAY_BINDING_LOG`, so
`C:\adl-agent-dev\tray-bindings.log` is written every run. A WPF binding
whose path is wrong neither throws nor draws — the label is simply empty,
which looks exactly like one the agent did not fill in. Empty past its
header means every binding resolved.

## What this loop does not test

Worth knowing, because none of it fails loudly:

- **The installers.** MSI service registration, the `%ProgramData%` ACLs,
  the `ADLURL` property, whether the token survives a major upgrade, the
  per-user Velopack tier. Run `packaging/pack.ps1` on Windows for those.
- **Self-update.** `WindowsUpdateInstaller` reads
  `AppContext.BaseDirectory`, which is the one piece of production code that
  behaves differently outside a single-file bundle.
- **The named pipe's ACL.** In production `SYSTEM` creates the pipe and the
  technician's account connects to it. Run in console mode you are both
  ends, so a cross-account permission bug stays invisible. That ACL is
  deliberate enough to deserve one service-mode run before a release.
- **Boot behaviour** — delayed auto-start, restart-on-failure.

A `dev/deploy.sh --ship` and a real install cover these. Once per release,
not once per change.

Worth knowing which open work this does and does not reach:
[#294](https://github.com/wmo-raf/adl/issues/294) and
[#297](https://github.com/wmo-raf/adl/issues/297) are tray and
control-surface behaviour and are covered entirely.
[#293](https://github.com/wmo-raf/adl/issues/293) and
[#296](https://github.com/wmo-raf/adl/issues/296) are the MSI's own user
interface and are not reached at all.
[#292](https://github.com/wmo-raf/adl/issues/292) and
[#295](https://github.com/wmo-raf/adl/issues/295) turn on elevation, the
`%ProgramData%` permissions and restarting a real service, none of which a
console run with a dev state directory has.
