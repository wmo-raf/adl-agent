#!/usr/bin/env bash
#
# Put the source on the Windows test machine, so it can build the installers.
#
# dev/deploy.sh sends a build. This sends the thing that makes one, because
# the installers cannot be made here: the WiX toolset says so itself the
# moment it starts on a Mac -- "The WiX Toolset only supports Windows ... all
# behavior after this point is undefined" -- and then fails on the path
# separators. Velopack's `vpk` packs Windows releases only there too.
#
# What travels is small. The repository is 439 MB and all but 1.7 MB of that
# is bin/, obj/ and .git; the sources, the two .wxs files and the icons are
# what an installer is built from and they fit in a couple of seconds of SMB.
#
# The Mac stays the only machine with the git history on it. The share gets a
# working tree and nothing else, which is deliberate: it is somewhere to run
# a compiler, not somewhere to edit or commit, and a second checkout that
# could be committed from is a second place work can be lost.
#
# Usage:
#   dev/sync-source.sh
#   ADL_AGENT_DROP=/Volumes/x dev/sync-source.sh
#
set -euo pipefail

repository="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
drop="${ADL_AGENT_DROP:-/Volumes/adl-agent-dev}"

if [[ ! -d "$drop" ]]; then
    echo "The drop folder $drop is not there."
    echo "Mount the Windows share first:  open 'smb://<windows-host>/adl-agent-dev'"
    echo "or point somewhere else:        ADL_AGENT_DROP=... dev/sync-source.sh"
    exit 1
fi

echo "==> Copying the source to $drop/src-mirror"

# Everything the packaging needs and nothing else. bin/ and obj/ are excluded
# rather than merely unnecessary: they hold this Mac's intermediate build, and
# MSBuild on the Windows machine reading a project.assets.json written by a
# different OS with different paths fails in ways that read as a broken
# repository rather than as a stale folder.
#
# --delete so a file deleted here is deleted there. A .wxs that was renamed
# and left behind is the exact shape of bug that gets an old dialog into a
# package nobody thought contained one.
#
# -rlt rather than -a, and the three --no-* with it: the share is SMB, which
# has no Unix permissions or ownership to keep, so -a spends every run
# re-applying them to every file and reports each one as changed. Told not
# to, an unchanged tree costs one directory listing.
rsync -rlt --no-perms --no-owner --no-group --modify-window=1 \
    --delete --itemize-changes \
    --exclude '.git/' \
    --exclude 'bin/' \
    --exclude 'obj/' \
    --exclude 'publish/' \
    --exclude 'artifacts/' \
    --exclude 'TestResults/' \
    --exclude '.dev-publish/' \
    --exclude '.idea/' \
    --exclude '.DS_Store' \
    "$repository/" "$drop/src-mirror/"

# The build launcher travels with the source, for run.cmd's reason: copied by
# hand once, every later change to it would have to be remembered, and the one
# on the machine would quietly drift from the one in the repository.
cp "$repository/dev/pack.cmd" "$drop/pack.cmd"

echo
echo "Done. On the Windows machine, double-click pack.cmd."
