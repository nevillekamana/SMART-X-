#!/usr/bin/env bash
# Smart-X device simulator — behaves like the ESP32 fleet: one HTTP POST per reading.
# Usage: ./scripts/device-simulator.sh [base_url] [seconds] [scenario]
#   scenario: normal (default) | drought | spike | overheat | dropout
BASE="${1:-http://localhost:59185}/api/stream"
SECS="${2:-30}"
SCENARIO="${3:-normal}"
J='Content-Type: application/json'

# mac|metric|base value|noise
SENSORS=(
  "A4:CF:12:00:00:01|moisture|62|1.2"
  "A4:CF:12:00:00:02|moisture|58|1.2"
  "A4:CF:12:00:00:03|temperature|24|0.4"
  "A4:CF:12:00:00:04|temperature|26|0.4"
  "A4:CF:12:00:00:05|power|1800|90"
  "A4:CF:12:00:00:06|power|2300|110"
  "A4:CF:12:00:00:07|power|1400|70"
)

end=$((SECONDS + SECS)); tick=0; sent=0
echo "Simulating ${#SENSORS[@]} sensors for ${SECS}s — scenario: $SCENARIO"
while [ $SECONDS -lt $end ]; do
  tick=$((tick+1))
  for s in "${SENSORS[@]}"; do
    IFS='|' read -r mac metric base noise <<<"$s"
    val=$(awk -v b="$base" -v n="$noise" -v t="$tick" 'BEGIN{srand(); printf "%.2f", b + (rand()-0.5)*2*n}')
    case "$SCENARIO:$mac" in
      drought:A4:CF:12:00:00:02)   val=$(awk -v t="$tick" 'BEGIN{v=58-4*t; if(v<9)v=9; printf "%.2f", v}');;
      spike:A4:CF:12:00:00:06)     val=4600;;
      overheat:A4:CF:12:00:00:04)  val=$(awk -v t="$tick" 'BEGIN{v=26+3*t; if(v>58)v=58; printf "%.2f", v}');;
      dropout:A4:CF:12:00:00:07)   continue;;
    esac
    code=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H "$J" \
      -d "{\"deviceId\":\"$mac\",\"metric\":\"$metric\",\"value\":$val,\"signalStrength\":-$((55 + RANDOM % 15))}" "$BASE/ingest")
    [ "$code" = 202 ] && sent=$((sent+1)) || echo "  ! $mac -> HTTP $code"
  done
  sleep 1.5
done
echo "Done — $sent packets accepted."
