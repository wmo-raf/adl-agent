#!/usr/bin/env python3
"""Diff what the generator wrote against what ADL holds.

    ADL_API_KEY=... ./check.py

Reads ``config.json`` for the base URL and the five station link ids, pulls
``/api/data/timeseries/<id>/`` for each, and compares it row by row against
the ledger. Four things can be wrong and they are counted separately,
because they mean different things:

* **missing** -- the generator wrote a minute ADL has no record of. The
  agent did not collect it, or the drain did not decode it.
* **mismatch** -- ADL holds a different number. Decode, or unit conversion:
  a pressure out by a factor of a hundred is the Pa -> hPa mapping.
* **ghost** -- a reading written as the -999 sentinel that ADL stored
  anyway. Core drops a ``None`` value rather than storing a null, so the
  correct outcome is no record at all for that parameter at that minute.
* **extra** -- ADL holds a minute nothing wrote. Usually residue from an
  earlier run against the same station.

Exits non-zero if anything is wrong, so it can be the last line of a script.
"""

import json
import os
import sys
from collections import defaultdict
from datetime import datetime, timedelta, timezone
from urllib.error import HTTPError
from urllib.parse import urlencode
from urllib.request import Request, urlopen

import simdata
from simdata import COLUMNS, NO_DATA, STATION_CODES, UTC

HERE = os.path.dirname(os.path.abspath(__file__))
CONFIG_PATH = os.path.join(HERE, "config.json")
LEDGER_ROOT = os.path.join(HERE, "ledger")

#: Absolute tolerance per column, in the ADL parameter's own unit. Generous
#: enough for float noise through a pint conversion, far tighter than any
#: real error.
TOLERANCE = 1e-6


def convert(column, value):
    """The vendor's number in ADL's unit."""
    if column == "Press_Pa":
        return value / 100.0  # Pa -> hPa
    return value


def load_config():
    if not os.path.exists(CONFIG_PATH):
        sys.exit(f"No {CONFIG_PATH}. Copy config.sample.json and fill in the station link ids.")
    with open(CONFIG_PATH) as handle:
        config = json.load(handle)

    key = os.environ.get("ADL_API_KEY") or config.get("api_key")
    if not key:
        sys.exit("No API key. Set ADL_API_KEY, or put `api_key` in config.json.")
    config["api_key"] = key

    missing = [c for c in STATION_CODES if c not in config.get("station_links", {})]
    if missing:
        sys.exit(f"config.json is missing station link ids for: {', '.join(missing)}")
    return config


def get(config, path, params=None):
    url = config["base_url"].rstrip("/") + path
    if params:
        url += "?" + urlencode(params)
    request = Request(url, headers={"Authorization": f"Api-Key {config['api_key']}"})
    try:
        with urlopen(request, timeout=60) as response:
            return json.load(response)
    except HTTPError as error:
        if error.code == 404:
            return None
        body = error.read().decode("utf-8", "replace")[:300]
        sys.exit(f"{error.code} from {url}\n{body}")


def parameter_ids(config):
    """Map each vendor column to the ADL parameter id it should land on."""
    payload = get(config, "/api/data-parameters/")
    if not payload:
        sys.exit("Could not read /api/data-parameters/.")

    by_name = {p["name"]: p["id"] for p in payload["data_parameters"]}
    ids = {}
    for column, name in config["parameters"].items():
        if name not in by_name:
            sys.exit(
                f"ADL has no parameter named {name!r} (for column {column}). "
                f"Known: {', '.join(sorted(by_name))}"
            )
        ids[column] = by_name[name]
    return ids


def observations(config, link_id, start, end):
    """``{aware datetime: {parameter_id: value}}`` from the timeseries API."""
    payload = get(
        config,
        f"/api/data/timeseries/{link_id}/",
        {"start_date": start.isoformat(), "end_date": end.isoformat()},
    )
    if payload is None:
        return {}

    out = {}
    for entry in payload.get("results", []):
        ts = datetime.fromisoformat(entry["time"]).astimezone(UTC).replace(second=0, microsecond=0)
        out[ts] = {int(k): v for k, v in entry.get("data", {}).items()}
    return out


def compare(code, expected, actual, ids):
    """Every way this station could be wrong, counted."""
    faults = defaultdict(list)

    for ts in sorted(expected):
        written = expected[ts]
        held = actual.get(ts)

        if held is None:
            faults["missing"].append(ts)
            continue

        for column in COLUMNS:
            pid = ids[column]
            value = held.get(pid)

            if written[column] == NO_DATA:
                if value is not None:
                    faults["ghost"].append((ts, column, value))
                continue

            if value is None:
                faults["missing_param"].append((ts, column))
                continue

            want = convert(column, written[column])
            if abs(value - want) > TOLERANCE + 1e-9 * abs(want):
                faults["mismatch"].append((ts, column, want, value))

    for ts in sorted(set(actual) - set(expected)):
        faults["extra"].append(ts)

    return faults


def describe(code, expected, actual, faults):
    total = len(expected)
    bad = sum(len(v) for v in faults.values())
    mark = "PASS" if bad == 0 else "FAIL"
    print(f"\n{mark}  {code}: {total} minutes written, {len(actual)} in ADL")

    if faults["missing"]:
        stamps = faults["missing"]
        print(f"      missing minutes: {len(stamps)}  e.g. {', '.join(f'{t:%H:%M}' for t in stamps[:6])}")
    if faults["missing_param"]:
        by_column = defaultdict(int)
        for _, column in faults["missing_param"]:
            by_column[column] += 1
        print(f"      minutes present but a parameter absent: {dict(by_column)}")
    if faults["mismatch"]:
        print(f"      value mismatches: {len(faults['mismatch'])}")
        for ts, column, want, got in faults["mismatch"][:5]:
            ratio = f"  (x{got / want:.4g})" if want else ""
            print(f"        {ts:%H:%M} {column}: expected {want!r}, ADL has {got!r}{ratio}")
    if faults["ghost"]:
        print(f"      sentinel readings stored instead of dropped: {len(faults['ghost'])}")
        for ts, column, got in faults["ghost"][:5]:
            print(f"        {ts:%H:%M} {column}: {got!r}")
    if faults["extra"]:
        stamps = faults["extra"]
        print(f"      minutes in ADL nothing wrote: {len(stamps)}  e.g. {', '.join(f'{t:%H:%M}' for t in stamps[:6])}")

    return bad


def late_drop_verdict(config, ids):
    path = os.path.join(LEDGER_ROOT, "latedrop.json")
    if not os.path.exists(path):
        return None
    with open(path) as handle:
        state = json.load(handle)
    if not state.get("dropped"):
        print("\n----  late drop: never dropped, nothing to verify")
        return None

    ts = datetime.strptime(state["timestamp"], simdata.TIMESTAMP_FORMAT).replace(tzinfo=UTC)
    link_id = config["station_links"][state["code"]]
    held = observations(config, link_id, ts - timedelta(minutes=1), ts + timedelta(minutes=1))

    if ts in held:
        print(f"\nPASS  late drop: {state['code']} {ts:%H:%M} arrived (below the watermark, fresh mtime)")
        return 0
    print(f"\nFAIL  late drop: {state['code']} {ts:%H:%M} never arrived")
    return 1


def main():
    config = load_config()
    ids = parameter_ids(config)
    print("Column -> ADL parameter id: " + ", ".join(f"{c}={ids[c]}" for c in COLUMNS))

    faults_total = 0
    for code in STATION_CODES:
        expected = simdata.ledger_read(LEDGER_ROOT, code)
        if not expected:
            print(f"\n----  {code}: no ledger, nothing was written")
            continue

        start, end = min(expected), max(expected)
        actual = observations(config, config["station_links"][code], start - timedelta(minutes=1), end + timedelta(minutes=1))
        faults = compare(code, expected, actual, ids)
        faults_total += describe(code, expected, actual, faults)

    late = late_drop_verdict(config, ids)
    if late:
        faults_total += late

    print()
    if faults_total:
        print(f"FAILED with {faults_total} faults.")
        sys.exit(1)
    print("All five stations agree with the ledger.")


if __name__ == "__main__":
    main()
