#!/usr/bin/env python3
"""Drive the simulated vendor: seed history, then feed it a minute at a time.

    ./simulate.py backfill --hours 6
    caffeinate -i ./simulate.py live --minutes 60
    ./simulate.py latedrop

`backfill` deliberately withholds one STN01 minute and records which one in
``ledger/latedrop.json``; `latedrop` writes that file later, with an old
observation time and a brand new mtime. That is the case the agent's
candidate window -- max(last-write, creation) -- exists to catch, and
withholding the minute first is what makes its arrival provable rather than
merely plausible.

Everything writes to the SMB mount by default, so every cycle re-checks that
the mount is still there: a share that drops silently would otherwise look
exactly like an agent that stopped collecting.
"""

import argparse
import json
import os
import sys
import time
from datetime import datetime, timedelta, timezone

import simdata
from simdata import (
    ROLLING_FLUSH_MINUTES,
    STATION_BY_CODE,
    STATION_CODES,
    UTC,
)

DEFAULT_DATA_ROOT = "/Volumes/adl-agent-dev/data"
DEFAULT_LEDGER_ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ledger")


def log(message):
    print(f"{datetime.now(UTC).strftime('%H:%M:%S')}  {message}", flush=True)


def require_mount(data_root):
    """Fail loudly rather than skip minutes when the share goes away."""
    volume = "/Volumes/adl-agent-dev"
    if data_root.startswith(volume) and not os.path.ismount(volume):
        raise SystemExit(f"FATAL: {volume} is not mounted. Reconnect the share and start again.")
    if not os.path.isdir(data_root):
        raise SystemExit(f"FATAL: {data_root} does not exist.")
    if not os.access(data_root, os.W_OK):
        raise SystemExit(f"FATAL: {data_root} is not writable.")


def now_minute():
    return datetime.now(UTC).replace(second=0, microsecond=0)


def latedrop_file(ledger_root):
    return os.path.join(ledger_root, "latedrop.json")


# ---------------------------------------------------------------- backfill

def backfill(args):
    data_root, ledger_root = args.data_root, args.ledger_root
    require_mount(data_root)

    end = now_minute()
    start = end - timedelta(hours=args.hours)

    # Withheld from the backfill, dropped in mid-run by `latedrop`. Taken
    # from the middle of the window so it is always inside the collection
    # start date and always well below where the watermark will have
    # reached by the time it is dropped.
    withheld = (start + (end - start) / 2).replace(second=0, microsecond=0)

    os.makedirs(ledger_root, exist_ok=True)
    with open(latedrop_file(ledger_root), "w") as handle:
        json.dump(
            {"code": "STN01", "timestamp": withheld.strftime(simdata.TIMESTAMP_FORMAT), "dropped": False},
            handle,
            indent=2,
        )

    log(f"Backfilling {start:%Y-%m-%d %H:%M} .. {end:%Y-%m-%d %H:%M} UTC ({args.hours}h)")
    log(f"Withholding STN01 {withheld:%Y-%m-%d %H:%M} for the late drop")

    stamps = list(simdata.minutes_between(start, end))
    total_files = 0

    for code in STATION_CODES:
        style = STATION_BY_CODE[code]["style"]
        rows = {}

        if style == "perminute":
            for ts in stamps:
                if code == "STN01" and ts == withheld:
                    continue
                _, written = simdata.write_perminute(data_root, code, ts)
                rows[ts] = written
                total_files += 1
        else:
            # One file per UTC day the window touches, rows in order.
            by_day = {}
            for ts in stamps:
                by_day.setdefault(ts.strftime("%Y%m%d"), []).append(ts)
            for day in sorted(by_day):
                _, written_rows = simdata.append_rolling(data_root, code, by_day[day])
                rows.update(written_rows)
                total_files += 1

        simdata.ledger_append(ledger_root, code, rows)
        log(f"  {code}: {len(rows)} minutes")

    log(f"Backfill complete: {total_files} files on disk, ledger in {ledger_root}")
    log("Watch the next cycles, then start the live feed.")


# -------------------------------------------------------------------- live

def live(args):
    data_root, ledger_root = args.data_root, args.ledger_root
    require_mount(data_root)

    started = now_minute()
    deadline = started + timedelta(minutes=args.minutes)
    log(f"Live feed until {deadline:%H:%M} UTC ({args.minutes} min)")

    pending = {code: [] for code in STATION_CODES if STATION_BY_CODE[code]["style"] == "rolling"}
    emitted = 0

    while True:
        ts = now_minute()
        if ts >= deadline:
            break

        require_mount(data_root)

        for code in STATION_CODES:
            if STATION_BY_CODE[code]["style"] == "perminute":
                _, written = simdata.write_perminute(data_root, code, ts)
                simdata.ledger_append(ledger_root, code, {ts: written})
            else:
                pending[code].append(ts)

        # Flush the rolling stations on the interval, so their files are
        # quiet for long enough to clear the stability window.
        if any(len(stamps) >= ROLLING_FLUSH_MINUTES for stamps in pending.values()):
            for code in list(pending):
                ordered = sorted(pending[code])
                _, written_rows = simdata.append_rolling(data_root, code, ordered)
                simdata.ledger_append(ledger_root, code, written_rows)
                log(f"  flushed {len(ordered)} rows to {code}")
                pending[code] = []

        emitted += 1
        log(f"{ts:%H:%M} written ({emitted} minutes so far)")

        # Sleep to the top of the next minute rather than a fixed 60s, so a
        # slow SMB write never makes the feed drift off the grid.
        target = ts + timedelta(minutes=1)
        time.sleep(max(1.0, (target - datetime.now(UTC)).total_seconds()))

    # Anything the last partial batch is still holding.
    for code in list(pending):
        if pending[code]:
            ordered = sorted(pending[code])
            _, written_rows = simdata.append_rolling(data_root, code, ordered)
            simdata.ledger_append(ledger_root, code, written_rows)
            log(f"  final flush: {len(ordered)} rows to {code}")

    log(f"Live feed finished: {emitted} minutes")
    log("Wait two check intervals plus a drain, then run check.py")


# --------------------------------------------------------------- late drop

def latedrop(args):
    data_root, ledger_root = args.data_root, args.ledger_root
    require_mount(data_root)

    path = latedrop_file(ledger_root)
    if not os.path.exists(path):
        raise SystemExit("No latedrop.json -- run `backfill` first.")

    with open(path) as handle:
        state = json.load(handle)

    if state.get("dropped"):
        log("Already dropped; nothing to do.")
        return

    code = state["code"]
    ts = datetime.strptime(state["timestamp"], simdata.TIMESTAMP_FORMAT).replace(tzinfo=UTC)

    written_path, written = simdata.write_perminute(data_root, code, ts)
    simdata.ledger_append(ledger_root, code, {ts: written})

    state["dropped"] = True
    state["dropped_at"] = datetime.now(UTC).isoformat()
    with open(path, "w") as handle:
        json.dump(state, handle, indent=2)

    log(f"Dropped {os.path.basename(written_path)} -- observation time {ts:%H:%M} UTC, mtime now.")
    log("It should appear in ADL within a cycle or two despite being below the watermark.")


# --------------------------------------------------------------------- cli

def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--data-root", default=DEFAULT_DATA_ROOT)
    parser.add_argument("--ledger-root", default=DEFAULT_LEDGER_ROOT)
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("backfill", help="seed history and withhold the late-drop minute")
    p.add_argument("--hours", type=int, default=6)
    p.set_defaults(func=backfill)

    p = sub.add_parser("live", help="write a minute at a time")
    p.add_argument("--minutes", type=int, default=60)
    p.set_defaults(func=live)

    p = sub.add_parser("latedrop", help="drop the withheld file with a fresh mtime")
    p.set_defaults(func=latedrop)

    args = parser.parse_args()
    try:
        args.func(args)
    except KeyboardInterrupt:
        log("Interrupted. The ledger reflects exactly what reached the disk.")
        sys.exit(130)


if __name__ == "__main__":
    main()
