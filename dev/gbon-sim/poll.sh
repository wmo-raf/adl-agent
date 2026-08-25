#!/usr/bin/env bash
# Poll ADL for observation counts on the five links until told to stop.
KEY="${ADL_API_KEY:?set ADL_API_KEY first}"
BASE='https://adl.climtech.africa'
while true; do
  line="$(date -u '+%H:%M:%S')"
  for id in 4 5 6 7 8; do
    n=$(curl -s -m 45 -H "Authorization: Api-Key $KEY" \
        "$BASE/api/data/timeseries/$id/?start_date=2026-08-25T05:00:00%2B00:00&end_date=2026-08-25T23:59:00%2B00:00" \
        | python3 -c "import json,sys
try:
    d=json.load(sys.stdin); print(len(d.get('results',[])))
except Exception: print('-')" 2>/dev/null)
    line="$line  $id:${n:-x}"
  done
  cache=$(python3 -c "import json;d=json.load(open('/Volumes/adl-agent-dev/state/config-cache.json'));print(d['fetched_at'][11:19], 'v'+str(d['config']['config_version']), str(d['config']['device']['check_interval_minutes'])+'m')" 2>/dev/null)
  echo "$line   | agent sync $cache"
  sleep 60
done
