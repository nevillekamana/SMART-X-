#!/usr/bin/env bash
# Smart-X Part 2 — API smoke/behaviour tests.
# Usage:  ./scripts/test-part2.sh [base_url]      (default http://localhost:59185)
# Needs:  bash, curl, python3 (only for reading JSON). Stop the dashboard's device simulator first so it
#         does not add its own traffic while the tests run.
BASE="${1:-http://localhost:59185}/api/stream"
PASS=0; FAIL=0
J='Content-Type: application/json'

call() { # call METHOD PATH [BODY]  -> sets $CODE and $BODY
  local out; out=$(mktemp)
  if [ -n "$3" ]; then CODE=$(curl -s -o "$out" -w '%{http_code}' -X "$1" -H "$J" -d "$3" "$BASE$2")
  else CODE=$(curl -s -o "$out" -w '%{http_code}' -X "$1" "$BASE$2"); fi
  BODY=$(cat "$out"); rm -f "$out"
}
py() { python3 -c "import sys,json; d=json.load(sys.stdin); $1" <<<"$BODY"; }
ok()   { PASS=$((PASS+1)); echo "  PASS  $1"; }
bad()  { FAIL=$((FAIL+1)); echo "  FAIL  $1   (got: $2)"; }
expect() { if [ "$2" = "$3" ]; then ok "$1"; else bad "$1" "$2, wanted $3"; fi; }

MB="A4:CF:12:00:00:02"   # Moisture Sensor B
T2="A4:CF:12:00:00:04"   # Greenhouse Temp 2
T1="A4:CF:12:00:00:03"   # Greenhouse Temp 1
M2="A4:CF:12:00:00:06"   # Smart Meter 2
PA="A4:CF:12:00:00:08"   # Pump A

echo "== 1. Device registry (Dictionary)"
call GET /devices;                       expect "GET /devices -> 200" "$CODE" 200
expect "13 seeded devices" "$(py 'print(len(d))')" 13
call GET "/devices?q=pump";              expect "search 'pump' finds 2 devices" "$(py 'print(len(d))')" 2
call GET "/devices?category=Actuator";   expect "category filter -> 6 actuators" "$(py 'print(len(d))')" 6
call GET "/devices?q=$MB";               expect "exact MAC lookup -> 1 device" "$(py 'print(len(d))')" 1

echo "== 2. Ingest validation"
call POST /ingest "{\"deviceId\":\"$T1\",\"metric\":\"temperature\",\"value\":24.5,\"signalStrength\":-60}"; expect "valid packet -> 202" "$CODE" 202
call POST /ingest '{"deviceId":"nope","metric":"power","value":5}';                                   expect "unknown device -> 404" "$CODE" 404
call POST /ingest "{\"deviceId\":\"$MB\",\"metric\":\"power\",\"value\":5}";                          expect "wrong metric for device -> 400" "$CODE" 400
call POST /ingest "{\"deviceId\":\"$MB\",\"metric\":\"moisture\",\"value\":140}";                     expect "moisture 140 % -> 400" "$CODE" 400
call POST /ingest "{\"deviceId\":\"$PA\",\"metric\":\"state\"}";                                       expect "actuator without state -> 400" "$CODE" 400

echo "== 3. Priority queue overtakes the FIFO queue"
call POST /ingest "{\"deviceId\":\"$T2\",\"metric\":\"temperature\",\"value\":25}"   # make sure Temp 2 starts healthy
sleep 0.6
call POST /processor/pause
call POST /ingest/batch "[$(for i in $(seq 1 12); do printf '{"deviceId":"%s","metric":"temperature","value":24.%d},' "$T1" "$i"; done | sed 's/,$//')]"
expect "batch of 12 accepted" "$(py 'print(d["accepted"])')" 12
call POST /ingest "{\"deviceId\":\"$T2\",\"metric\":\"temperature\",\"value\":55,\"signalStrength\":-60}"
expect "critical packet routed to Priority lane" "$(py 'print(d["lane"])')" Priority
CRIT=$(py 'print(d["sequence"])')
call GET /queues
expect "12 waiting in standard lane" "$(py 'print(d["standardDepth"])')" 12
expect "1 waiting in priority lane" "$(py 'print(d["priorityDepth"])')" 1
call POST /processor/resume; sleep 1
call GET /queues
RES=$(py "
r={x['sequence']:x['order'] for x in d['recent']}
crit=r[$CRIT]
std=[o for s,o in r.items() if $CRIT-12<=s<$CRIT]
print('yes' if std and all(crit<o for o in std) else 'no')")
expect "critical (arrived last) processed before the 12 waiting standard packets" "$RES" yes

echo "== 4. HashSet suppresses duplicate alerts"
call GET /alerts; BEFORE=$(py 'print(d["duplicatesSuppressed"])')
for i in 1 2 3 4; do call POST /ingest "{\"deviceId\":\"$T2\",\"metric\":\"temperature\",\"value\":5$i,\"signalStrength\":-60}"; done
sleep 0.5
call GET /alerts
expect "still exactly one OverTemp alert for Temp 2" "$(py "print(sum(1 for a in d['active'] if a['type']=='OverTemp' and a['mac']=='$T2'))")" 1
AFTER=$(py 'print(d["duplicatesSuppressed"])')
expect "4 repeat readings suppressed" "$((AFTER-BEFORE))" 4
call POST /ingest "{\"deviceId\":\"$T2\",\"metric\":\"temperature\",\"value\":26}"; sleep 0.5
call GET /alerts
expect "alert resolves when reading returns to normal" "$(py "print(sum(1 for a in d['active'] if a['mac']=='$T2'))")" 0

echo "== 5. Disconnect reports (HashSet)"
call POST /disconnect-reports "{\"deviceId\":\"A4:CF:12:00:00:07\",\"reportedBy\":\"Node 1\"}"
expect "first report is new" "$(py 'print(d["isNew"])')" True
call POST /disconnect-reports "{\"deviceId\":\"A4:CF:12:00:00:07\",\"reportedBy\":\"Node 2\"}"
expect "second report is a duplicate" "$(py 'print(d["isNew"])')" False
call POST /ingest "{\"deviceId\":\"A4:CF:12:00:00:07\",\"metric\":\"power\",\"value\":1500}"; sleep 0.5
call GET "/devices/A4:CF:12:00:00:07"
expect "node reconnects when it sends a packet" "$(py 'print(d["state"])')" Healthy

echo "== 6. Commands + Stack undo"
call POST /commands/bulk '{"kind":"All","action":"Off"}'    # normalise (may legitimately 409)
while [ "$(curl -s -o /dev/null -w '%{http_code}' -X POST $BASE/commands/undo)" = 200 ]; do :; done   # empty the stack
call POST /commands "{\"deviceId\":\"$PA\",\"action\":\"On\"}";   expect "Pump A -> ON (200)" "$CODE" 200
call POST /commands "{\"deviceId\":\"$PA\",\"action\":\"On\"}";   expect "repeat ON is rejected (409)" "$CODE" 409
call POST /commands "{\"deviceId\":\"$T1\",\"action\":\"On\"}";   expect "cannot command a sensor (409)" "$CODE" 409
call POST /commands/bulk '{"kind":"Valve","action":"On"}';        expect "bulk: all valves ON (200)" "$CODE" 200
call GET /commands;                                                expect "undo stack depth 2" "$(py 'print(d["undoDepth"])')" 2
call POST /commands/undo;                                          expect "undo #1 reverts the bulk command" "$(py 'print(d["description"].startswith("Reverted: all valves"))')" True
call GET "/devices?category=Actuator"
expect "valves are OFF again" "$(py "print(all(not x['isOn'] for x in d if x['kind']=='Valve'))")" True
expect "Pump A still ON" "$(py "print([x['isOn'] for x in d if x['name']=='Pump A'][0])")" True
call POST /commands/undo;                                          expect "undo #2 reverts Pump A" "$CODE" 200
call POST /commands/undo;                                          expect "undo on empty stack -> 409" "$CODE" 409

echo "== 7. Predictive engine"
call POST /events/search '{"query":"Pump A"}';                     expect "exact-name search is learned" "$(py 'print(d["logged"])')" True
call POST /ingest "{\"deviceId\":\"$MB\",\"metric\":\"moisture\",\"value\":11,\"signalStrength\":-60}"; sleep 0.6
call GET /insights
expect "'Turn Pump A ON' suggested after Moisture B alert" "$(py "print(any(s['title']=='Turn Pump A ON' for s in d['suggestedActions']))")" True
expect "suggestion confidence >= 50 %" "$(py "print(max(s['confidence'] for s in d['suggestedActions'] if s['title']=='Turn Pump A ON')>=0.5)")" True
call POST /commands "{\"deviceId\":\"$PA\",\"action\":\"On\"}"; sleep 0.3
call GET /insights
expect "suggestion disappears once Pump A is already ON" "$(py "print(any(s['title']=='Turn Pump A ON' for s in d['suggestedActions']))")" False
call POST /commands/undo >/dev/null; call POST /ingest "{\"deviceId\":\"$MB\",\"metric\":\"moisture\",\"value\":58}" ; sleep 0.4

echo
echo "Result: $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
