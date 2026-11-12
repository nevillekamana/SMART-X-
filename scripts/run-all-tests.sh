#!/usr/bin/env bash
# Starts the API fresh and runs every suite against that one start, in the required order:
#   test-part2.sh  ->  ui-smoke (jsdom)  ->  test-part3.sh
# Usage: ./scripts/run-all-tests.sh [port]            (default 59185)
# Optional: SMARTX_CMD="dotnet path/to/SmartX.dll" to run a pre-built app instead of `dotnet run`.
set -u
PORT="${1:-59185}"; URL="http://localhost:$PORT"
cd "$(dirname "$0")/.."
CMD="${SMARTX_CMD:-dotnet run --no-launch-profile}"
$CMD --urls "$URL" > /tmp/smartx-test-server.log 2>&1 &
SERVER=$!
trap 'kill $SERVER 2>/dev/null' EXIT
for _ in $(seq 1 120); do curl -s -o /dev/null "$URL/api/stream/devices" && break; sleep 0.5; done
curl -s -o /dev/null "$URL/api/stream/devices" || { echo "API did not start — see /tmp/smartx-test-server.log"; exit 2; }

RC=0
echo "################ test-part2.sh";            bash ./scripts/test-part2.sh "$URL"            || RC=1
echo; echo "################ ui-smoke (jsdom)";  (cd scripts/ui-smoke && { [ -d node_modules ] || npm install --silent; } && node ui.js "$URL") || RC=1
echo; echo "################ test-part3.sh";       bash ./scripts/test-part3.sh "$URL"            || RC=1
exit $RC
