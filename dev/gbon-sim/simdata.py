"""The simulated vendor: five stations, six GBON parameters, one minute apart.

Every value is a pure function of ``(station, minute, column)`` -- a blake2b
digest stands in for the random number generator -- so nothing here carries
state between runs and any row can be recomputed months later. What the
checker diffs against is still the ledger written beside the files, because
the ledger records what was *written*: a generator killed halfway through
leaves a ledger that stops where the files stop, and a run whose value model
was edited mid-flight is not silently re-derived into agreement.

Written units are the vendor's, not ADL's. Pressure is emitted in pascals
against an ADL parameter held in hectopascals, so a broken conversion shows
up as a factor of a hundred rather than as a rounding argument.
"""

import csv
import hashlib
import math
import os
from datetime import datetime, timedelta, timezone

# ---------------------------------------------------------------- constants

UTC = timezone.utc

#: The vendor's column names, deliberately unlike ADL's parameter names so
#: that the connection's variable mappings are actually doing something.
COLUMNS = ["Temp_C", "RH_pct", "Press_Pa", "WS_ms", "WD_deg", "Rain_mm"]
HEADER = ["timestamp"] + COLUMNS

#: Decimal places each column is written to. The ledger stores the value the
#: file got, parsed back from this same formatting, so the checker never has
#: to reason about round-half-even.
DECIMALS = {
    "Temp_C": 2,
    "RH_pct": 1,
    "Press_Pa": 1,
    "WS_ms": 2,
    "WD_deg": 0,
    "Rain_mm": 2,
}

TIMESTAMP_FORMAT = "%Y-%m-%d %H:%M:%S"

#: What the connection's CSV config calls ``no_data_value``. A reading
#: written as this must arrive in ADL as *no record at all* -- core drops a
#: ``None`` value rather than storing a null -- which is what the checker
#: asserts.
NO_DATA = -999.0

#: Fraction of individual readings replaced by the sentinel.
SENTINEL_RATE = 0.01

STATIONS = [
    # code    style         subdir   glob pattern (None => DIRECT_FETCH)
    ("STN01", "perminute", "",      "STN01_*.csv"),
    ("STN02", "perminute", "",      "STN02_*.csv"),
    ("STN03", "perminute", "STN03", None),
    ("STN04", "rolling",   "",      "STN04_*.csv"),
    ("STN05", "rolling",   "",      "STN05_*.csv"),
]

STATION_CODES = [s[0] for s in STATIONS]
STATION_BY_CODE = {s[0]: {"code": s[0], "style": s[1], "subdir": s[2], "pattern": s[3]} for s in STATIONS}

#: Rolling stations hold their rows and flush this many minutes at a time.
#: A file appended to every sixty seconds is never older than sixty seconds,
#: so with the agent's default stability window it would never once be
#: eligible -- the data stays minute-by-minute, only the flush is batched.
ROLLING_FLUSH_MINUTES = 5


# ------------------------------------------------------------ determinism

def _u(*parts):
    """A uniform in [0, 1) determined entirely by ``parts``."""
    digest = hashlib.blake2b("|".join(str(p) for p in parts).encode(), digest_size=8).digest()
    return int.from_bytes(digest, "big") / 2.0 ** 64


def _n(*parts):
    """Noise in [-1, 1)."""
    return _u(*parts) * 2.0 - 1.0


def _profile(code):
    """Give each station its own climate so five stations are not five copies."""
    i = int(code[-2:])
    return {
        "t_mean": 22.0 + 1.6 * i,
        "t_amp": 5.0 + 0.4 * i,
        "p_base": 101325.0 - 300.0 * i,  # stands in for altitude
        "w_base": 1.2 + 0.3 * i,
        "d_base": 40.0 * i,
    }


# ------------------------------------------------------------- value model

def _rainfall(code, ts):
    """Mostly nothing, occasionally a shower that builds and fades."""
    hour_key = ts.strftime("%Y%m%d%H")
    if _u(code, hour_key, "shower") > 0.12:
        return 0.0

    start = int(_u(code, hour_key, "start") * 40)
    duration = 5 + int(_u(code, hour_key, "dur") * 15)
    if not (start <= ts.minute < start + duration):
        return 0.0

    peak = 0.15 + 1.6 * _u(code, hour_key, "peak")
    through = (ts.minute - start + 0.5) / duration
    jitter = 0.6 + 0.8 * _u(code, ts.strftime("%Y%m%d%H%M"), "rain")
    return peak * math.sin(math.pi * through) * jitter


def true_values(code, ts):
    """The physical reading, before any sensor decides to fail."""
    p = _profile(code)
    hour = ts.hour + ts.minute / 60.0
    minute_key = ts.strftime("%Y%m%d%H%M")

    temperature = (
        p["t_mean"]
        + p["t_amp"] * math.sin(2 * math.pi * (hour - 9.0) / 24.0)
        + 0.35 * _n(code, minute_key, "t")
    )
    humidity = 92.0 - 2.2 * (temperature - (p["t_mean"] - p["t_amp"])) + 2.0 * _n(code, minute_key, "rh")
    humidity = min(100.0, max(12.0, humidity))

    pressure = (
        p["p_base"]
        + 120.0 * math.sin(2 * math.pi * (hour - 10.0) / 12.0)
        + 25.0 * _n(code, minute_key, "p")
    )

    speed = (
        p["w_base"]
        + 2.2 * max(0.0, math.sin(2 * math.pi * (hour - 6.0) / 24.0))
        + 0.9 * _n(code, minute_key, "ws")
    )
    speed = max(0.0, speed)

    direction = (p["d_base"] + 12.0 * hour + 25.0 * _n(code, minute_key, "wd")) % 360.0

    return {
        "Temp_C": temperature,
        "RH_pct": humidity,
        "Press_Pa": pressure,
        "WS_ms": speed,
        "WD_deg": direction,
        "Rain_mm": _rainfall(code, ts),
    }


def _is_sentinel(code, ts, column):
    return _u(code, ts.strftime("%Y%m%d%H%M"), column, "sentinel") < SENTINEL_RATE


def render_row(code, ts):
    """Return ``(cells, written)`` for one minute.

    ``cells`` is the CSV line as strings; ``written`` maps each column to the
    float those strings parse back to, which is exactly what goes in the
    ledger.
    """
    values = true_values(code, ts)
    cells = [ts.strftime(TIMESTAMP_FORMAT)]
    written = {}

    for column in COLUMNS:
        places = DECIMALS[column]
        raw = NO_DATA if _is_sentinel(code, ts, column) else values[column]
        text = f"{raw:.{places}f}"
        cells.append(text)
        written[column] = float(text)

    return cells, written


# -------------------------------------------------------------- filesystem

def minutes_between(start, end):
    """Every whole minute in ``[start, end)``, oldest first."""
    ts = start.replace(second=0, microsecond=0)
    while ts < end:
        yield ts
        ts += timedelta(minutes=1)


def station_dir(data_root, code):
    subdir = STATION_BY_CODE[code]["subdir"]
    return os.path.join(data_root, subdir) if subdir else data_root


def perminute_name(code, ts):
    return f"{code}_{ts.strftime('%Y%m%d%H%M')}.csv"


def rolling_name(code, ts):
    return f"{code}_{ts.strftime('%Y%m%d')}.csv"


def write_perminute(data_root, code, ts):
    """One file, one row, written aside and renamed into place.

    The rename means the agent can never see a half-written file, which
    leaves the stability window testing what it is for -- a vendor that
    writes in place -- rather than papering over this generator.
    """
    directory = station_dir(data_root, code)
    os.makedirs(directory, exist_ok=True)

    cells, written = render_row(code, ts)
    final = os.path.join(directory, perminute_name(code, ts))
    staging = final + ".tmp"

    with open(staging, "w", newline="", encoding="utf-8") as handle:
        writer = csv.writer(handle)
        writer.writerow(HEADER)
        writer.writerow(cells)
        handle.flush()
        os.fsync(handle.fileno())
    os.replace(staging, final)

    return final, written


def append_rolling(data_root, code, stamps):
    """Append minutes to today's file, creating it with a header if new.

    Appending in place is the point: the file's hash moves, ADL is offered
    it again, and every row it has already made an observation of has to
    come back through the upsert without duplicating anything.
    """
    if not stamps:
        return None, {}

    directory = station_dir(data_root, code)
    os.makedirs(directory, exist_ok=True)

    path = os.path.join(directory, rolling_name(code, stamps[0]))
    fresh = not os.path.exists(path)
    written_rows = {}

    with open(path, "a", newline="", encoding="utf-8") as handle:
        writer = csv.writer(handle)
        if fresh:
            writer.writerow(HEADER)
        for ts in stamps:
            cells, written = render_row(code, ts)
            writer.writerow(cells)
            written_rows[ts] = written
        handle.flush()
        os.fsync(handle.fileno())

    return path, written_rows


# ------------------------------------------------------------------ ledger

def ledger_path(ledger_root, code):
    return os.path.join(ledger_root, f"{code}.csv")


def ledger_append(ledger_root, code, rows):
    """Record what reached the disk, after it reached the disk."""
    if not rows:
        return
    os.makedirs(ledger_root, exist_ok=True)
    path = ledger_path(ledger_root, code)
    fresh = not os.path.exists(path)

    with open(path, "a", newline="", encoding="utf-8") as handle:
        writer = csv.writer(handle)
        if fresh:
            writer.writerow(HEADER)
        for ts in sorted(rows):
            written = rows[ts]
            writer.writerow(
                [ts.strftime(TIMESTAMP_FORMAT)]
                + [f"{written[c]:.{DECIMALS[c]}f}" for c in COLUMNS]
            )
        handle.flush()
        os.fsync(handle.fileno())


def ledger_read(ledger_root, code):
    """``{aware datetime: {column: float}}`` for everything written so far."""
    path = ledger_path(ledger_root, code)
    if not os.path.exists(path):
        return {}

    out = {}
    with open(path, newline="", encoding="utf-8") as handle:
        for row in csv.DictReader(handle):
            ts = datetime.strptime(row["timestamp"], TIMESTAMP_FORMAT).replace(tzinfo=UTC)
            out[ts] = {c: float(row[c]) for c in COLUMNS}
    return out
