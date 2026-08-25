# Agent collection test — setup checklist

Everything below is on **https://adl.climtech.africa** unless it says *tray*.
Do the steps in order; each one depends on the one above it.

Nothing here is destructive except step 4, which edits an existing station
link. Device **W11**'s pairing is not touched at any point.

---

## 0. Before you start — three things I need back

| # | What | Why |
|---|---|---|
| 1 | An **API key** (Wagtail admin → API Keys → add, copy the key once) | The checker gets a 403 without it and the whole verification leg is dead |
| 2 | The **Network** that the "Server Agent" connection belongs to | The 5 test stations have to be created inside it |
| 3 | Whether the units and parameters in steps 1–2 **already exist** | So we reuse them rather than creating near-duplicates |

If parameters already exist under different names (`air_temp`, `precipitation`,
whatever), send me the names — I'll point `config.json` at them instead of you
creating a second set.

---

## 1. Units

**Settings → Units.** Create only what's missing. The symbol must be a valid
pint symbol — I've checked all seven against the instance's pint registry and
they all resolve.

| Name | Symbol |
|---|---|
| Celsius | `degC` |
| Percent | `%` |
| Hectopascal | `hPa` |
| Pascal | `Pa` |
| Metres per second | `m/s` |
| Degree | `degree` |
| Millimetre | `mm` |

`Pa` is only ever a *source* unit — no parameter is held in it. It exists so
the pressure mapping has something to convert from.

## 2. Data parameters

**Settings → Data Parameters.** Category `Meteorological` for all six.

| Name | Unit | Aggregation method |
|---|---|---|
| `air_temperature` | degC | Standard |
| `relative_humidity` | % | Standard |
| `atmospheric_pressure` | **hPa** | Standard |
| `wind_speed` | m/s | Standard |
| `wind_direction` | degree | **Circular Mean** |
| `rainfall` | mm | Standard |

> Wind direction **must** be Circular Mean. On Standard it averages 350° and
> 10° to 180° — due south for a northerly — and the hourly aggregates come out
> quietly wrong while the raw records look fine.

## 3. Stations

**Stations → add**, in the network from step 0. STN01 already exists as the
station behind station link 4 — reuse it and rename if you like, or leave its
name alone and just note its id.

Create **STN02 … STN05**. Required fields:

| Field | Value |
|---|---|
| Station ID | `STN02` … `STN05` |
| Name | `Sim Station 02` … `Sim Station 05` |
| Network | (from step 0) |
| Station type | Automatic |
| WSI series | `0` |
| WSI issuer | `20000` |
| WSI issue number | `0` |
| WSI local | `SIM02` … `SIM05` |
| Location | anywhere plausible; spread them a degree apart so the map viewer isn't five pins on one spot |

## 4. The connection — "Server Agent"

**Open the existing AgentConnection and set:**

| Field | Value |
|---|---|
| Decoder | **Standard CSV** |
| CSV Configuration | the one created in step 5 (come back after creating it) |
| Stations Timezone | **UTC** |
| Processing Interval | `5` minutes (drain backstop; the agent plugin also drains within seconds of each cycle) |

## 5. CSV decoder configuration

**Settings → CSV Decoder Configurations → add.** Then attach it to the
connection in step 4.

| Field | Value |
|---|---|
| Configuration Name | `GBON minute sim` |
| File has header row | ✅ **checked** |
| Delimiter | Comma (`,`) |
| Skip rows | `0` |
| **No Data Value** | `-999` |
| Datetime Mode | Single datetime column |
| Datetime Column Name | `timestamp` |
| Datetime Format | `2025-01-15 14:30:45 (YYYY-MM-DD HH:MM:SS — ISO 8601)` |

Leave the separate date/time fields empty.

*Verified:* I ran the real `StandardCSVDecoder` against generated files with
exactly this config — 360 records out of a rolling file, 1 out of a per-minute
file, and every `-999` came back as `None`.

## 6. Variable mappings — 6 rows, on the connection

On the **AgentConnection**, connection-level variable mappings. All five
stations share these; there are no per-station overrides.

| File variable name | File variable unit | ADL parameter |
|---|---|---|
| `Temp_C` | degC | air_temperature |
| `RH_pct` | % | relative_humidity |
| `Press_Pa` | **Pa** | atmospheric_pressure |
| `WS_ms` | m/s | wind_speed |
| `WD_deg` | degree | wind_direction |
| `Rain_mm` | mm | rainfall |

Only the pressure row's units differ between source and parameter. If the
numbers in ADL come out around **101325 instead of 1013.25**, this row is the
cause.

## 7. Station links

**Station link 4 is repurposed as STN01** — its folder is already right, its
pattern is not. Create four more.

Set only these fields in the admin; the rest is the tray's in step 8.

| Station link | Station | Collection Start Date |
|---|---|---|
| link 4 (existing) | STN01 | **7 hours before you run the backfill**, UTC |
| new | STN02 | same |
| new | STN03 | same |
| new | STN04 | same |
| new | STN05 | same |

Seven hours, not six: the backfill covers six, and the extra hour is margin so
nothing is rejected for being a minute below the floor. The validator only
requires the date be in the past.

## 8. Folder binding — **in the tray, on the Windows VM**

This is where the app tier gets set, and doing it here is itself the test:
every value must then be visible in the Wagtail admin and `config_version` must
move. If it isn't in the admin, the write didn't happen.

The tray also validates patterns live — it will tell you how many files a
folder and glob currently match, so a wrong pattern is caught before a cycle
runs rather than after one.

| Station | Folder | Pattern | Strategy | Stability window |
|---|---|---|---|---|
| STN01 | `C:\adl-agent-dev\data` | `STN01_*.csv` | Enumerate | 60 |
| STN02 | `C:\adl-agent-dev\data` | `STN02_*.csv` | Enumerate | 60 |
| STN03 | `C:\adl-agent-dev\data\STN03` | *(unused)* | **Direct Fetch** | 60 |
| STN04 | `C:\adl-agent-dev\data` | `STN04_*.csv` | Enumerate | 60 |
| STN05 | `C:\adl-agent-dev\data` | `STN05_*.csv` | Enumerate | 60 |

**Fix link 4's pattern.** It currently reads `'Station1_*.dat` — with a leading
apostrophe, which matches nothing at all. It becomes `STN01_*.csv`.

**STN03, Direct Fetch fields:**

| Field | Value |
|---|---|
| File Prefix | `STN03_` |
| File Interval (minutes) | `1` |
| File Datetime Format | `yyyyMMddHHmm` |
| File Datetime Timezone | **UTC** |
| File Extension | `.csv` |

That format is .NET's, not Python's — `yyyyMMddHHmm` builds
`STN03_202608251230.csv`. One minute × 6 hours is 360 constructed names per
cycle, far under the 20 000 bound.

## 9. Device

**AgentDevice W11 → Check interval: `2` minutes.** Set it back to 5 when the
test is over.

---

## Then tell me

- the **station link ids** for STN01–STN05 (from each link's admin URL)
- the **API key**
- that the agent service is running on the VM (`run.cmd`)

and I'll run the backfill.

---

## Teardown, when you're done

1. Device W11 check interval back to **5**.
2. Delete the four new station links and set link 4's pattern back — or just
   disable the connection.
3. `rm -rf /Volumes/adl-agent-dev/data/*` to clear the generated files.
4. The observations stay in ADL under the five sim stations; delete the
   stations to take them with it.
