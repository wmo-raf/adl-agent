#!/usr/bin/env python3
"""
Write the release index every ADL instance mirrors from.

An ADL instance is one country's deployment, and an agent can only be updated
from the instance it is paired with. Without a single index for the instances
themselves to follow, publishing a version would mean an operator in each of
twenty-six countries uploading the same file by hand.

So the build publishes this document beside the packages, and each instance's
nightly mirror reads it. It is the only place the two repositories meet, and
it is deliberately tiny: a version, a package per install tier, and the digest
each package must have.

    {"releases": [
      {"version": "0.2.0",
       "released_at": "2026-08-21T10:00:00Z",
       "notes": "...",
       "artifacts": [
         {"kind": "msi",
          "url": "https://github.com/.../AdlAgent-0.2.0-x64.msi",
          "sha256": "9f2c...",
          "size": 43210987}]}]}

The digests are computed here, from the files that are about to be uploaded.
Everything downstream checks against them -- the mirror before it stores a
package, and every agent before it installs one -- so this is the point at
which what a fleet will run is decided.
"""

import argparse
import hashlib
import json
import pathlib
import sys

#: How a built file is recognised as one of the packages an ADL instance
#: serves. Anything not matched here (delta packages, the portable zip,
#: Velopack's own feed files) is part of the release but not part of the
#: index: the instances do not serve it and the agents never ask for it.
KINDS = (
    ("-full.nupkg", "velopack_full"),
    ("Setup.exe", "velopack_setup"),
    (".msi", "msi"),
)

#: How many releases the index carries. An instance meeting it for the first
#: time mirrors the recent end and no more; older versions stay available as
#: release assets for an operator who wants one to pin a machine to.
KEEP = 5


def kind_of(name):
    for suffix, kind in KINDS:
        if name.endswith(suffix):
            return kind

    return None


def sort_key(version):
    parts = (version or "").split(".")

    if len(parts) != 3:
        return (-1, -1, -1)

    try:
        return tuple(int(part) for part in parts)
    except ValueError:
        return (-1, -1, -1)


def digest(path):
    sha = hashlib.sha256()

    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            sha.update(chunk)

    return sha.hexdigest()


def release_entry(directory, version, url_base, released_at, notes):
    artifacts = []

    for path in sorted(directory.iterdir()):
        if not path.is_file():
            continue

        kind = kind_of(path.name)

        if kind is None:
            continue

        artifacts.append({
            "kind": kind,
            "url": f"{url_base.rstrip('/')}/{path.name}",
            "sha256": digest(path),
            "size": path.stat().st_size,
        })

    if not artifacts:
        raise SystemExit(f"No installable packages found in {directory}.")

    return {
        "version": version,
        "released_at": released_at,
        "notes": notes,
        "artifacts": artifacts,
    }


def previous_releases(path):
    """What the last index said, or nothing.

    Merged rather than replaced so that an instance mirroring for the first
    time can still reach the version before this one -- which is what an
    operator pinning a machine one release back needs to exist.
    """
    if path is None or not path.exists():
        return []

    try:
        document = json.loads(path.read_text())
    except ValueError:
        print(f"warning: {path} is not readable as an index; starting fresh.",
              file=sys.stderr)

        return []

    releases = document.get("releases")

    return releases if isinstance(releases, list) else []


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts", required=True, type=pathlib.Path,
                        help="Directory holding the built packages.")
    parser.add_argument("--version", required=True)
    parser.add_argument("--url-base", required=True,
                        help="Where the packages will be downloadable from.")
    parser.add_argument("--released-at", required=True,
                        help="ISO 8601, e.g. 2026-08-21T10:00:00Z.")
    parser.add_argument("--notes", default="")
    parser.add_argument("--previous", type=pathlib.Path, default=None,
                        help="The index this one supersedes, if it was fetched.")
    parser.add_argument("--output", required=True, type=pathlib.Path)

    arguments = parser.parse_args()

    entry = release_entry(
        arguments.artifacts,
        arguments.version,
        arguments.url_base,
        arguments.released_at,
        arguments.notes,
    )

    releases = [
        release for release in previous_releases(arguments.previous)
        if isinstance(release, dict) and release.get("version") != arguments.version
    ]
    releases.append(entry)
    releases.sort(key=lambda release: sort_key(release.get("version")), reverse=True)

    arguments.output.write_text(
        json.dumps({"releases": releases[:KEEP]}, indent=2) + "\n"
    )

    print(f"{arguments.output}: {len(releases[:KEEP])} release(s), "
          f"{len(entry['artifacts'])} package(s) in {arguments.version}")


if __name__ == "__main__":
    main()
