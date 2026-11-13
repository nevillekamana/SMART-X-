#!/usr/bin/env bash
# Smart-X final PoE (Network Topology & Mesh Routing) — API behaviour tests.
# Usage:  ./scripts/test-part3.sh [base_url]      (default http://localhost:59185)
# Needs:  bash, curl, python3 (only for reading JSON). Same style as test-part2.sh.
# Run on a FRESH API start, in this order:  test-part2.sh  ->  scripts/ui-smoke (node ui.js)  ->  test-part3.sh
# (test-part3 registers a Part 1 sensor, which test-part2's "13 seeded devices" check would then count.)
# Do not run the dashboard's device simulator while this runs.
ROOT="${1:-http://localhost:59185}"
BASE="$ROOT/api/mesh"; S="$ROOT/api/stream"
PASS=0; FAIL=0
J='Content-Type: application/json'

call() { # call METHOD URL [BODY] -> sets $CODE and $BODY
  local out; out=$(mktemp)
  if [ -n "$3" ]; then CODE=$(curl -s -o "$out" -w '%{http_code}' -X "$1" -H "$J" -d "$3" "$2")
  else CODE=$(curl -s -o "$out" -w '%{http_code}' -X "$1" "$2"); fi
  BODY=$(cat "$out"); rm -f "$out"
}
m() { call "$1" "$BASE$2" "$3"; }      # mesh call
s() { call "$1" "$S$2" "$3"; }         # part-2 call
py() { python3 -c "import sys,json; d=json.load(sys.stdin); $1" <<<"$BODY"; }
ok()   { PASS=$((PASS+1)); echo "  PASS  $1"; }
bad()  { FAIL=$((FAIL+1)); echo "  FAIL  $1   (got: $2)"; }
expect() { if [ "$2" = "$3" ]; then ok "$1"; else bad "$1" "$2, wanted $3"; fi; }

V1="A4:CF:12:00:00:0B"   # Valve 1
FAN="A4:CF:12:00:00:0D"  # Fan 1
T1="A4:CF:12:00:00:03"   # Greenhouse Temp 1
M3="A4:CF:12:00:00:07"   # Smart Meter 3
M1="A4:CF:12:00:00:05"   # Smart Meter 1

# Precondition: a freshly started API (this suite registers a Part 1 sensor and fills the 200-event queue, so it can only run once per start).
call GET "$BASE/nodes"
N0=$(python3 -c "import sys,json; print(len(json.load(sys.stdin)))" <<<"$BODY" 2>/dev/null)
if [ "$N0" != "14" ]; then echo "Expected a freshly started API with 14 mesh nodes (got: ${N0:-no response}). Restart the API and run again."; exit 2; fi
m POST /outages/reset >/dev/null

echo "== 1. Topology, MST, Prim == Kruskal"
m GET /topology;                                   expect "GET /topology -> 200" "$CODE" 200
expect "14 nodes (13 devices + gateway)" "$(py 'print(len(d["nodes"]))')" 14
expect "22 links" "$(py 'print(len(d["links"]))')" 22
expect "every RSSI distinct" "$(py 'r=[l["rssi"] for l in d["links"]]; print(len(set(r))==len(r))')" True
expect "MST has n-1 = 13 edges" "$(py 'print(d["tree"]["edgeCount"])')" 13
expect "all nodes Up at start" "$(py 'print(d["kpis"]["online"])')" 13
expect "Prim cost == Kruskal cost" "$(py 'print(d["tree"]["primCost"]==d["tree"]["kruskalCost"])')" True
expect "Prim and Kruskal pick the very same edges" "$(py 'print(d["tree"]["agree"])')" True
PRIM_IDS=$(py 'print(",".join(d["tree"]["edgeIds"]))')
m GET "/topology?algorithm=kruskal"
expect "kruskal view returns the same edge set" "$(py 'print(",".join(d["tree"]["edgeIds"]))')" "$PRIM_IDS"
m GET "/topology?algorithm=bogus";                 expect "unknown algorithm -> 400" "$CODE" 400
m GET "/topology"
expect "tree is cheaper than using every link" "$(py 'print(d["kpis"]["treeCost"] < d["kpis"]["allLinksCost"])')" True
expect "Fan 1 flagged as a single point of failure" "$(py 'print([n["singlePointOfFailure"] for n in d["nodes"] if n["name"]=="Fan 1"][0])')" True
expect "only Fan 1 is a single point of failure" "$(py 'print(sum(1 for n in d["nodes"] if n["singlePointOfFailure"]))')" 1
m GET /nodes;                                      expect "GET /nodes -> 200 with 14 rows" "$(py 'print(len(d))')" 14

echo "== 2. ResolveNode: MAC, name, sensor id, gateway"
m GET "/routes/$V1";                               expect "route by MAC -> 200" "$CODE" 200
m GET "/routes/Valve%201";                         expect "route by device name -> 200" "$CODE" 200
m GET "/routes/valve1";                            expect "route by compact name -> 200" "$CODE" 200
m GET "/routes/gateway";                           expect "route for gateway -> 200" "$CODE" 200
m GET "/routes/no-such-node";                      expect "unknown node -> 404" "$CODE" 404
m GET "/nodes/Valve%201/history?lastMinutes=10";   expect "history accepts a device name" "$CODE" 200
m GET "/nodes/gateway/history";                    expect "gateway has no history -> 400" "$CODE" 400

echo "== 3. Failover: Valve 1 fails"
m POST "/nodes/$V1/fail";                          expect "fail Valve 1 -> 200" "$CODE" 200
m GET /topology
expect "Valve 1 is Down" "$(py 'print([n["status"] for n in d["nodes"] if n["name"]=="Valve 1"][0])')" Down
expect "Pump B now routes through Fan 1" "$(py 'print([n["nextHopName"] for n in d["nodes"] if n["name"]=="Pump B"][0])')" "Fan 1"
expect "nobody is cut off (the mesh healed)" "$(py 'print(d["kpis"]["cutOff"])')" 0
expect "tree still spans every live node (12 edges)" "$(py 'print(d["tree"]["edgeCount"])')" 12
expect "Prim == Kruskal after the failure" "$(py 'print(d["tree"]["agree"])')" True
m GET /connectivity
expect "connectivity lists Valve 1's affected nodes (old -> new hop)" "$(py 'a=d["downNodes"][0]["affected"]; print(sorted((x["name"],x["oldNextHop"],x["newNextHop"]) for x in a))')" "[('Moisture Sensor A', 'Valve 1', 'Pump B'), ('Pump B', 'Valve 1', 'Fan 1')]"
m GET "/routes/Pump%20B"
expect "route marked rerouted" "$(py 'print(d["rerouted"])')" True
expect "failover note carries the energy delta" "$(py 'import re; print(bool(re.search(r"Energy \+11\.53 power units", d["failoverNote"])))')" True
expect "pre-outage route goes via Valve 1" "$(py 'print(d["preOutage"]["names"])')" "['Pump B', 'Valve 1', 'Gateway']"
expect "MST, Dijkstra and BFS paths all present" "$(py 'print(all(d[k]["found"] for k in ("mst","dijkstra","bfs")))')" True
m GET "/routes/Moisture%20Sensor%20A"
expect "Moisture A rerouted over 3 hops (+1 hop)" "$(py 'print(d["mst"]["names"])')" "['Moisture Sensor A', 'Pump B', 'Fan 1', 'Gateway']"
m POST "/nodes/$V1/recover";                       expect "recover Valve 1 -> 200" "$CODE" 200
m GET /topology;                                   expect "healed: Valve 1 Up again" "$(py 'print([n["status"] for n in d["nodes"] if n["name"]=="Valve 1"][0])')" Up
m GET "/routes/Pump%20B";                          expect "route no longer rerouted" "$(py 'print(d["rerouted"])')" False

echo "== 4. Isolation: Fan 1 fails"
m POST "/nodes/Fan%201/fail";                      expect "fail Fan 1 -> 200" "$CODE" 200
m GET /topology
expect "both greenhouse temps are cut off" "$(py 'print(sorted(n["name"] for n in d["nodes"] if n["status"]=="Isolated"))')" "['Greenhouse Temp 1', 'Greenhouse Temp 2']"
expect "cut-off KPI = 2" "$(py 'print(d["kpis"]["cutOff"])')" 2
m GET /connectivity
expect "2 sub-networks (DFS)" "$(py 'print(len(d["subNetworks"]))')" 2
expect "relay suggested for each isolated node" "$(py 'print(all(i["suggestedRelay"] for i in d["isolated"]) and len(d["isolated"])==2)')" True
m GET "/routes/$T1"
expect "isolated node has no MST route" "$(py 'print(d["mst"]["found"])')" False
expect "note says CUT OFF and names a relay" "$(py 'print("CUT OFF" in d["failoverNote"] and "relay" in d["failoverNote"])')" True
m POST /outages/reset;                             expect "reset -> 200" "$CODE" 200
m GET /topology;                                   expect "everything Up again" "$(py 'print(d["kpis"]["online"])')" 13

echo "== 5. Part 2 'Disconnected' alert makes a node DOWN in the mesh"
s POST /disconnect-reports "{\"deviceId\":\"$M3\",\"reportedBy\":\"test\"}";   expect "Part 2 disconnect report accepted" "$CODE" 200
m GET /topology
expect "Smart Meter 3 shows Down" "$(py 'print([n["status"] for n in d["nodes"] if n["name"]=="Smart Meter 3"][0])')" Down
expect "reason names the Part 2 alert" "$(py 'print("Part 2" in [n["downReason"] for n in d["nodes"] if n["name"]=="Smart Meter 3"][0])')" True
m POST "/nodes/$M3/recover";                       expect "recover a node that is down only because of an alert -> 409" "$CODE" 409
s POST /ingest "{\"deviceId\":\"$M3\",\"metric\":\"power\",\"value\":1500}"; sleep 0.8
m GET /topology;                                   expect "node is Up again once it sends telemetry" "$(py 'print([n["status"] for n in d["nodes"] if n["name"]=="Smart Meter 3"][0])')" Up

echo "== 6. Guards and status codes"
m POST /nodes/gateway/fail;                        expect "fail the gateway -> 400" "$CODE" 400
m POST /nodes/ghost/fail;                          expect "fail unknown node -> 404" "$CODE" 404
m POST /nodes/ghost/recover;                       expect "recover unknown node -> 404" "$CODE" 404
m POST "/nodes/$M1/recover";                       expect "recover a node that is up -> 409" "$CODE" 409
m POST /links "{\"a\":\"gateway\",\"b\":\"$V1\",\"rssi\":-60}";   expect "duplicate link -> 409" "$CODE" 409
m POST /links "{\"a\":\"$V1\",\"b\":\"gateway\",\"rssi\":-60}";   expect "duplicate link (reversed) -> 409" "$CODE" 409
m POST /links "{\"a\":\"$V1\",\"b\":\"Valve 1\",\"rssi\":-60}";   expect "self link -> 409" "$CODE" 409
m POST /links "{\"a\":\"$V1\",\"b\":\"Pump A\",\"rssi\":-20}";    expect "RSSI above -30 -> 400" "$CODE" 400
m POST /links "{\"a\":\"$V1\",\"b\":\"Pump A\",\"rssi\":-101}";   expect "RSSI below -100 -> 400" "$CODE" 400
m POST /links "{\"a\":\"$V1\",\"b\":\"Pump A\"}";                 expect "missing RSSI -> 400" "$CODE" 400
m POST /links "{\"a\":\"nobody\",\"b\":\"Pump A\",\"rssi\":-60}"; expect "link to unknown node -> 404" "$CODE" 404
m GET "/nodes/$M1/history?from=not-a-date";        expect "bad 'from' date -> 400" "$CODE" 400
m GET "/nodes/$M1/history?to=also-bad";            expect "bad 'to' date -> 400" "$CODE" 400
m GET "/nodes/$M1/history?from=2026-11-02T10:00:00Z&to=2026-11-01T10:00:00Z"; expect "reversed dates -> 400" "$CODE" 400
m GET "/nodes/$M1/history?lastMinutes=0";          expect "lastMinutes=0 -> 400" "$CODE" 400
m POST /links "{\"a\":\"$V1\",\"b\":\"Pump A\",\"rssi\":-90}";    expect "valid new link -> 200" "$CODE" 200
m GET /topology;                                   expect "23 links now" "$(py 'print(len(d["links"]))')" 23
expect "tree unchanged by a weak spare link (Prim == Kruskal)" "$(py 'print(d["tree"]["agree"] and d["tree"]["edgeCount"]==13)')" True

echo "== 7. Registered Part 1 sensor is 'Unlinked' until linked"
RAND=$(printf '%02X:%02X' $((RANDOM%256)) $((RANDOM%256)))
NEWMAC="B8:27:EB:00:$RAND"
call POST "$ROOT/api/sensors/register" "{\"macAddress\":\"$NEWMAC\",\"location\":\"Test-Annex\",\"category\":\"Environmental\"}"; expect "Part 1 registration -> 200" "$CODE" 200
NEWID=$(py 'print(d["sensor"]["id"])')
m GET "/routes/$NEWID";                            expect "ResolveNode accepts the Part 1 sensor id" "$(py 'print(d["node"]["mac"])')" "$NEWMAC"
expect "unlinked node's route says it needs a link" "$(py 'print(d["status"], d["reachable"])')" "Unlinked False"
m GET /topology
expect "new sensor listed as Unlinked" "$(py "print([n['status'] for n in d['nodes'] if n['mac']=='$NEWMAC'])")" "['Unlinked']"
m POST "/nodes/$NEWMAC/fail";                      expect "cannot fail an unlinked node -> 409" "$CODE" 409
m POST /links "{\"a\":\"$NEWMAC\",\"b\":\"Smart Meter 2\",\"rssi\":-71}"; expect "link it into the mesh -> 200" "$CODE" 200
m GET /topology
expect "it is now Up and routed" "$(py "print([n['status'] for n in d['nodes'] if n['mac']=='$NEWMAC'])")" "['Up']"
expect "tree grew to 14 edges (15 nodes)" "$(py 'print(d["tree"]["edgeCount"])')" 14

echo "== 8. AVL range queries"
m GET "/nodes/$M1/history?lastMinutes=120&pageSize=10"
expect "history -> 200" "$CODE" 200
expect "matched ~ 2 h at 30 s intervals (>= 230)" "$(py 'print(d["matched"]>=230)')" True
expect "tree height equals the optimal height" "$(py 'print(d["tree"]["height"]==d["tree"]["optimalHeight"])')" True
expect "visited ≈ matches + O(log n)" "$(py 't=d["tree"]; print(t["nodesVisited"] <= d["matched"] + 2*t["height"] + 2)')" True
expect "visited far fewer than the tree size" "$(py 't=d["tree"]; print(t["nodesVisited"] < t["size"]/5)')" True
expect "page is newest-first" "$(py 'ts=[p["timestamp"] for p in d["items"]]; print(ts==sorted(ts,reverse=True))')" True
expect "page size honoured" "$(py 'print(len(d["items"]))')" 10
expect "chart series decimated to <= 240 points" "$(py 'print(len(d["series"])<=240)')" True
expect "chart series is chronological" "$(py 'ts=[p["timestamp"] for p in d["series"]]; print(ts==sorted(ts))')" True
expect "stats are min <= avg <= max" "$(py 's=d["stats"]; print(s["min"]<=s["avg"]<=s["max"])')" True
expect "critical thresholds included" "$(py 'print(d["thresholds"]["criticalHigh"])')" 4000
m GET "/nodes/$M1/history?lastMinutes=120&pageSize=10&page=1"; P1=$(py 'print(d["items"][0]["timestamp"])'); P1LAST=$(py 'print(d["items"][-1]["timestamp"])')
m GET "/nodes/$M1/history?lastMinutes=120&pageSize=10&page=2"; P2=$(py 'print(d["items"][0]["timestamp"])')
expect "page 2 starts older than page 1 ends" "$(python3 -c "print('$P2' < '$P1LAST')")" True
expect "page 2 is a different page" "$([ "$P1" != "$P2" ] && echo yes || echo no)" yes
m GET "/nodes/$M1/history?from=2020-01-01T00:00:00Z&to=2020-01-02T00:00:00Z"
expect "empty window -> 0 matches, 200" "$(py 'print(d["matched"], d["items"]==[])')" "0 True"
m GET "/nodes/Fan%201/history?lastMinutes=1440&pageSize=5"
expect "actuator history flagged as state device" "$(py 'print(d["thresholds"]["isStateDevice"])')" True
expect "actuator state reports every 5 min (~288/day)" "$(py 'print(270 <= d["matched"] <= 300)')" True
m GET /history/stats;                              expect "GET /history/stats -> 200" "$CODE" 200
expect "every per-device AVL tree is valid" "$(py 'print(d["allTreesValid"])')" True
expect "24 h of seed history present (> 20000 readings)" "$(py 'print(d["totalReadings"]>20000)')" True

echo "== 9. Benchmarks"
m GET "/benchmark/tree?points=50000&queries=100"
expect "tree benchmark -> 200" "$CODE" 200
expect "AVL and linear scan return identical results" "$(py 'print(d["identical"])')" True
expect "AVL examines far fewer readings than the scan" "$(py 'print(d["avl"]["avgNodesVisited"] < d["linearScan"]["readingsExaminedPerQuery"]/10)')" True
expect "AVL is faster than the linear scan" "$(py 'print(d["speedup"]>1)')" True
expect "AVL height == optimal height" "$(py 'print(d["avl"]["height"]==d["avl"]["optimalHeight"])')" True
expect "plain BST degenerates to height 3000" "$(py 'print(d["degeneration"]["plainBstHeight"])')" 3000
expect "AVL stays at height 12 for the same 3000 keys" "$(py 'print(d["degeneration"]["avlHeight"])')" 12
m GET "/benchmark/graph?nodes=1500"
expect "graph benchmark -> 200" "$CODE" 200
expect "Prim and Kruskal costs agree" "$(py 'print(d["costsAgree"])')" True
expect "both are spanning trees (n-1 edges)" "$(py 'print(d["spanningTreeEdges"])')" True
expect "BFS and DFS reach every node" "$(py 'print(d["bfs"]["reached"]==d["nodes"]==d["dfs"]["reached"])')" True

echo "== 10. Part 2 feedback fixes (Queue history + async insights)"
s GET /insights;                                   expect "GET /insights (async) -> 200" "$CODE" 200
expect "insights model exposes queryHistoryCapacity = 200" "$(py 'print(d["model"]["queryHistoryCapacity"])')" 200
DEPTH0=$(py 'print(d["model"]["queryHistoryDepth"])')
s POST /events/search '{"query":"Pump A"}'
s GET /insights;                                   expect "queryHistoryDepth grew by 1 after a search" "$(py "print(d['model']['queryHistoryDepth']-$DEPTH0)")" 1
s GET "/insights/history?take=3"
expect "GET /insights/history -> 200 with 3 events" "$(py 'print(len(d["events"]))')" 3
expect "history is newest-first" "$(py 'ts=[e["time"] for e in d["events"]]; print(ts==sorted(ts,reverse=True))')" True
expect "latest event is the search just made" "$(py 'print(d["events"][0]["kind"], d["events"][0]["subject"])')" "Search Pump A"
expect "capacity reported as 200" "$(py 'print(d["capacity"])')" 200
for i in $(seq 1 210); do curl -s -o /dev/null -X POST -H "$J" -d '{"query":"Valve 2"}' "$S/events/search"; done
s GET /insights
expect "queue is bounded: depth capped at 200 after 210 more events" "$(py 'print(d["model"]["queryHistoryDepth"])')" 200
s GET "/insights/history?take=500"
expect "take is clamped to the capacity" "$(py 'print(len(d["events"]))')" 200
s GET "/insights/history";                         expect "default take works" "$(py 'print(len(d["events"]))')" 20

m POST /outages/reset >/dev/null
echo
echo "Result: $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
