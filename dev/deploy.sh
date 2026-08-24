#!/usr/bin/env bash
#
# Put a fresh build on the Windows test machine.
#
# The agent ships as two self-contained single files, and copying those is
# 115 MB of which 114.7 MB is the .NET runtime -- identical on every build.
# So the loop publishes framework-dependent against the .NET 10 Desktop
# Runtime installed on that machine once, and copies the 0.3 MB that
# actually changed. Pass --ship to build the real shape instead, which is
# what should be run once before a tag.
#
# Usage:
#   dev/deploy.sh                 # fast: framework-dependent
#   dev/deploy.sh --ship          # what the fleet installs
#   ADL_AGENT_DROP=/Volumes/x dev/deploy.sh
#
set -euo pipefail

repository="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# The Windows machine's shared folder, mounted here. Its bin/ is the only
# thing this writes: state/ holds the device token and pairing survives a
# deploy, which is the whole reason the loop is worth having.
drop="${ADL_AGENT_DROP:-/Volumes/adl-agent-dev}"

ship=false
[[ "${1:-}" == "--ship" ]] && ship=true

# Staged locally first, then mirrored. Publishing straight onto the share
# would push 3.3 MB over SMB on every build and would leave the folder
# half-written if Windows still had a file locked; rsync compares first and
# copies only the assemblies that changed, which after the first deploy is
# the three that are yours.
staging="$repository/.dev-publish/$([[ $ship == true ]] && echo ship || echo fast)"

rm -rf "$staging"
mkdir -p "$staging"

if [[ $ship == true ]]; then
    echo "==> Publishing self-contained (the shape the fleet installs)"

    # Separate directories, for the reason packaging/pack.ps1 gives: a
    # single-file tray bundles the service project it references, so the two
    # programs cannot share one output folder in this mode.
    dotnet publish "$repository/src/AdlAgent.Windows/AdlAgent.Windows.csproj" \
        -c Release -r win-x64 -o "$staging/service"
    dotnet publish "$repository/src/AdlAgent.Tray/AdlAgent.Tray.csproj" \
        -c Release -r win-x64 -o "$staging/tray"

    cp "$staging/service/adl-agent.exe" "$staging/"
    cp "$staging/tray/adl-agent-tray.exe" "$staging/"
    rm -rf "$staging/service" "$staging/tray"
else
    echo "==> Publishing framework-dependent (needs the .NET 10 Desktop Runtime on Windows)"

    common=(-c Release -r win-x64 --self-contained false
            -p:PublishSingleFile=false -o "$staging")

    # The tray first and the service second, into one folder. The tray
    # references the service project and brings a copy of its apphost along;
    # publishing the service afterwards means the deps.json and
    # runtimeconfig.json beside adl-agent.exe are the ones its own publish
    # wrote, rather than the ones it inherited as somebody else's reference.
    dotnet publish "$repository/src/AdlAgent.Tray/AdlAgent.Tray.csproj" "${common[@]}"
    dotnet publish "$repository/src/AdlAgent.Windows/AdlAgent.Windows.csproj" "${common[@]}"
fi

if [[ ! -d "$drop" ]]; then
    echo
    echo "The drop folder $drop is not there."
    echo "Mount the Windows share first:  open 'smb://<windows-host>/adl-agent-dev'"
    echo "or point somewhere else:        ADL_AGENT_DROP=... dev/deploy.sh"
    exit 1
fi

mkdir -p "$drop/bin"

echo "==> Copying to $drop/bin"

# --delete so a renamed assembly does not linger and get loaded. Scoped to
# bin/ on purpose: state/, run.cmd and the sample data folder live beside it
# and are none of this script's business.
if ! rsync -a --delete --itemize-changes "$staging/" "$drop/bin/"; then
    echo
    echo "The copy failed. Windows locks a running program's files:"
    echo "close the agent's console window and the tray, then run this again."
    exit 1
fi

# The launcher travels with the build. Copying it by hand once would mean
# every later change to it had to be remembered, and the one on the machine
# would quietly drift from the one in the repository.
cp "$repository/dev/run.cmd" "$drop/run.cmd"

echo
echo "Done. On the Windows machine, double-click run.cmd."
