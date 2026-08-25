# GBON minute simulator

A fake vendor for testing whether the ADL Agent actually collects data. Five
stations, six GBON parameters, one row a minute, decoded on the ADL side by
`adl_ftp_plugin`'s Standard CSV decoder.

Values are a pure function of `(station, minute, column)` — a blake2b digest
stands in for the random number generator — so a run is reproducible and any
row can be recomputed later. What the checker actually diffs against is the
**ledger**, because the ledger records what was *written*: a generator killed
halfway leaves a ledger that stops where the files stop.

## Files

| File | What it is |
|---|---|
| `simdata.py` | The vendor: station table, value model, file + ledger writing. Import-only. |
| `simulate.py` | `backfill` / `live` / `latedrop` |
| `check.py` | Pulls ADL's timeseries API and diffs it against the ledger |
| `poll.sh` | One line a minute: observation counts per link + the agent's sync state |
| `config.json` | Base URL, station link ids, column → ADL parameter name |
| `CHECKLIST.md` | The ADL admin + tray setup, if you ever rebuild it |

## One-time

```bash
export ADL_API_KEY='...'          # Wagtail admin -> API Keys
```

`config.json` holds the station link ids and the column→parameter mapping.
It is already pointed at climtech links 4–8. If you rebuild the ADL side,
the link ids come from each link's admin URL and the parameter names from
`GET /api/data-parameters/`.

## The loop

```bash
# 1. Seed history. Anchored to now, so run it when you mean it.
./simulate.py backfill --hours 6

# 2. Watch it arrive.
./poll.sh                          # ctrl-c when you have seen enough

# 3. Feed it live.
caffeinate -i ./simulate.py live --minutes 60

# 4. Mid-run, from another shell: the late-arriving file.
./simulate.py latedrop

# 5. Verdict.
./check.py
```

`backfill` withholds one STN01 minute from the middle of its window and
records it in `ledger/latedrop.json`. `latedrop` writes that file later, with
an old observation time and a fresh mtime — the case the agent's candidate
window, `max(last-write, creation)`, exists to catch. Withholding it first is
what makes its arrival provable rather than merely plausible.

`check.py` exits non-zero if anything is wrong, so it can be the last line of
a script. It separates four faults because they mean different things:

- **missing** — written but ADL has no record. Not collected, or not decoded.
- **mismatch** — ADL holds a different number. Decode, or unit conversion; a
  pressure out by ×100 is the Pa → hPa mapping.
- **ghost** — a `-999` sentinel ADL stored anyway. Core drops a `None` rather
  than storing a null, so the right outcome is no record at all.
- **extra** — ADL holds a minute nothing wrote. Usually residue from a previous
  run against the same stations.

## Resetting between runs

The ledger is cumulative and `check.py` reads all of it, so a second backfill
on top of a first will have the checker demand both.

```bash
rm -rf ledger                              # forget what was written
rm -rf /Volumes/adl-agent-dev/data/*       # clear the vendor folder
```

ADL keeps the observations either way. To make ADL forget too, delete the
observations for those stations in the admin — otherwise old minutes outside
the new ledger show up as `extra`.

You do **not** need to reset the agent. It keeps no record of what it
delivered; the vendor's folder is its only state.

## Knobs

| Flag | Default | Notes |
|---|---|---|
| `--hours` | 6 | Backfill depth. 6h ≈ 1081 files ≈ three manifest pages. |
| `--minutes` | 60 | Live run length. |
| `--data-root` | `/Volumes/adl-agent-dev/data` | Point at a local dir to dry-run with no VM. |
| `--ledger-root` | `./ledger` | |

A dry run costs nothing and touches no ADL:

```bash
./simulate.py --data-root /tmp/sim --ledger-root /tmp/simledger backfill --hours 1
```

In `simdata.py`:

- `STATIONS` — the station table: code, write style, subdirectory, glob.
  Adding one means adding a station link and a folder binding on the ADL side.
- `SENTINEL_RATE` — 0.01. How often a reading is written as `-999`.
- `ROLLING_FLUSH_MINUTES` — 5. See the gotcha below before lowering it.
- `true_values()` — the value model. Change it freely; the ledger is written
  from whatever it returns, so the checker follows automatically.

## Gotchas worth remembering

**A rolling file appended every 60 s never clears a 60 s stability window.**
It is never older than sixty seconds, so it is never once eligible and the
station collects nothing, silently. That is why rolling stations flush five
minutes at a time. If you lower `ROLLING_FLUSH_MINUTES`, lower those stations'
stability window in the tray to match.

**SMB is the slow part.** ~360 ms per file, so a 6-hour backfill takes about
six and a half minutes to write. Not a hang.

**Collection Start Date must be below the backfill window.** It is admin tier
and not visible in the API or in the agent's config cache, so nothing here can
check it for you. If it sits above the window every station reads as collecting
nothing, which looks exactly like a broken agent.

**The timeseries API 500s on a naive `start_date`.** `?start_date=2026-08-25T04:17:00`
blows up; `...+00:00` and `...Z` are fine. `check.py` always sends aware
timestamps, so it never hits this — but you will, by hand, in curl.

**`poll.sh` has the API key inline.** Fine for a scratch harness on your own
machine; move it to `$ADL_API_KEY` before this goes anywhere shared.

## Reading the agent's own state

The share carries the agent's view of ADL, which is the fastest way to tell a
configuration problem from a collection problem:

```bash
python3 -c "
import json; d=json.load(open('/Volumes/adl-agent-dev/state/config-cache.json'))
print(d['fetched_at'], 'v'+str(d['config']['config_version']))
for c in d['config']['connections']:
    for sl in c['station_links']:
        print(sl['id'], sl['config']['listing_strategy'], sl['config'].get('file_pattern'), sl['config']['local_folder_path'])
"
```

If a pattern is wrong *there*, the tray write never reached ADL — no number of
cycles will fix it.
