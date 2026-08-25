# Packaging the agent

Two installers, from one script, for the two tiers decision
[#262](https://github.com/wmo-raf/adl/issues/262) ships.

| Tier | Package | What it installs | Who it is for |
|---|---|---|---|
| Service | `AdlAgent-<version>-x64.msi` | A Windows Service, delayed auto-start, restarting itself on failure | A technician with administrator rights |
| Per-user | `AdlAgent-<version>-Setup.exe` | A per-user install under `%LocalAppData%`, started at logon | A technician without them (story 3) |

Both carry the .NET runtime inside them. Nothing needs installing on the
target machine first, which is the point: a country server is not a machine
anyone can ask to install a framework over a phone call.

## Building

Windows, with the .NET SDK from `global.json`:

```powershell
./packaging/pack.ps1 -Version 0.2.0
```

It publishes both programs self-contained, builds the MSI with the WiX
toolset, packs the per-user tier with Velopack's `vpk`, and prints the
SHA-256 of everything it produced. Both tools are installed as .NET tools if
they are not already there, at the versions pinned in the script — a
packaging tool that moved under a release would change what a fleet installs
without anything in this repository having changed.

CI runs exactly this (`.github/workflows/ci.yml`), so a package can be
reproduced by hand, which is the only way to work out what went wrong with an
install nobody can reach.

## Installing

The service tier needs to be told where its ADL is:

```powershell
msiexec /i AdlAgent-0.2.0-x64.msi ADLURL=https://adl.example.org
```

That URL is written to `%ProgramData%\ADL Agent\agent.ini`, which the
service reads when it starts:

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
- **Same-version upgrades are allowed.** Every build that is not a tag calls
  itself the version in `Directory.Build.props`, and without this two of them
  would install side by side and fight over one service name.
- **The state directory's permissions are replaced, not inherited.** SYSTEM
  and Administrators only: the device token is stored in the clear, and on a
  shared vendor server every local account could otherwise read a credential
  that can send data to a national instance.

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
