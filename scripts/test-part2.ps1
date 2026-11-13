<#
 Smart-X Part 2 - API tests (PowerShell). Mirrors scripts/test-part2.sh.
 Usage:  .\scripts\test-part2.ps1 [-BaseUrl http://localhost:59185]
 Stop the dashboard's device simulator first so it does not add its own traffic while the tests run.
#>
param([string]$BaseUrl = "http://localhost:59185")

$api = "$BaseUrl/api/stream"
$script:pass = 0
$script:fail = 0

function Call($method, $path, $body = $null) {
    $args = @{ Uri = "$api$path"; Method = $method; ContentType = "application/json"; SkipHttpErrorCheck = $true; StatusCodeVariable = "code" }
    if ($null -ne $body) { $args.Body = ($body | ConvertTo-Json -Depth 5 -Compress) }
    $res = Invoke-RestMethod @args
    [pscustomobject]@{ Code = $code; Data = $res }
}
function Expect($name, $actual, $wanted) {
    if ("$actual" -eq "$wanted") { $script:pass++; Write-Host "  PASS  $name" -ForegroundColor Green }
    else { $script:fail++; Write-Host "  FAIL  $name (got: $actual, wanted $wanted)" -ForegroundColor Red }
}

$MB = "A4:CF:12:00:00:02"; $T1 = "A4:CF:12:00:00:03"; $T2 = "A4:CF:12:00:00:04"; $PA = "A4:CF:12:00:00:08"

Write-Host "== 1. Device registry (Dictionary)"
$r = Call GET "/devices";                      Expect "13 seeded devices" @($r.Data).Count 13
$r = Call GET "/devices?q=pump";               Expect "search 'pump' finds 2 devices" @($r.Data).Count 2
$r = Call GET "/devices?category=Actuator";    Expect "category filter -> 6 actuators" @($r.Data).Count 6
$r = Call GET "/devices?q=$MB";                Expect "exact MAC lookup -> 1 device" @($r.Data).Count 1

Write-Host "== 2. Ingest validation"
$r = Call POST "/ingest" @{ deviceId = $T1; metric = "temperature"; value = 24.5; signalStrength = -60 }; Expect "valid packet -> 202" $r.Code 202
$r = Call POST "/ingest" @{ deviceId = "nope"; metric = "power"; value = 5 };                              Expect "unknown device -> 404" $r.Code 404
$r = Call POST "/ingest" @{ deviceId = $MB; metric = "power"; value = 5 };                                 Expect "wrong metric -> 400" $r.Code 400
$r = Call POST "/ingest" @{ deviceId = $MB; metric = "moisture"; value = 140 };                            Expect "moisture 140 % -> 400" $r.Code 400

Write-Host "== 3. Priority queue overtakes the FIFO queue"
Call POST "/ingest" @{ deviceId = $T2; metric = "temperature"; value = 25 } | Out-Null
Start-Sleep -Milliseconds 600
Call POST "/processor/pause" | Out-Null
$batch = 1..12 | ForEach-Object { @{ deviceId = $T1; metric = "temperature"; value = (24 + $_ / 100) } }
$r = Call POST "/ingest/batch" $batch;         Expect "batch of 12 accepted" $r.Data.accepted 12
$r = Call POST "/ingest" @{ deviceId = $T2; metric = "temperature"; value = 55; signalStrength = -60 }
Expect "critical packet routed to Priority lane" $r.Data.lane "Priority"
$crit = [int]$r.Data.sequence
$r = Call GET "/queues"
Expect "12 waiting in standard lane" $r.Data.standardDepth 12
Expect "1 waiting in priority lane" $r.Data.priorityDepth 1
Call POST "/processor/resume" | Out-Null
Start-Sleep -Seconds 1
$r = Call GET "/queues"
$critOrder = ($r.Data.recent | Where-Object { $_.sequence -eq $crit }).order
$earlier = $r.Data.recent | Where-Object { $_.sequence -ge ($crit - 12) -and $_.sequence -lt $crit }
$allAfter = @($earlier | Where-Object { $_.order -le $critOrder }).Count -eq 0
Expect "critical processed before the 12 waiting standard packets" $allAfter $true

Write-Host "== 4. HashSet suppresses duplicate alerts"
$before = (Call GET "/alerts").Data.duplicatesSuppressed
1..4 | ForEach-Object { Call POST "/ingest" @{ deviceId = $T2; metric = "temperature"; value = (50 + $_); signalStrength = -60 } | Out-Null }
Start-Sleep -Milliseconds 500
$r = Call GET "/alerts"
Expect "exactly one OverTemp alert for Temp 2" @($r.Data.active | Where-Object { $_.type -eq "OverTemp" -and $_.mac -eq $T2 }).Count 1
Expect "4 repeat readings suppressed" ($r.Data.duplicatesSuppressed - $before) 4

Write-Host "== 5. Commands + Stack undo"
Call POST "/commands/bulk" @{ kind = "All"; action = "Off" } | Out-Null
while ((Call POST "/commands/undo").Code -eq 200) { }
$r = Call POST "/commands" @{ deviceId = $PA; action = "On" };  Expect "Pump A -> ON" $r.Code 200
$r = Call POST "/commands" @{ deviceId = $PA; action = "On" };  Expect "repeat ON rejected" $r.Code 409
$r = Call POST "/commands/bulk" @{ kind = "Valve"; action = "On" }; Expect "bulk: all valves ON" $r.Code 200
$r = Call GET "/commands";                                      Expect "undo stack depth 2" $r.Data.undoDepth 2
$r = Call POST "/commands/undo";                                Expect "undo #1 reverts bulk command" $r.Data.description.StartsWith("Reverted: all valves") $true
$r = Call POST "/commands/undo";                                Expect "undo #2 reverts Pump A" $r.Code 200
$r = Call POST "/commands/undo";                                Expect "empty stack -> 409" $r.Code 409

Write-Host ""
Write-Host "Result: $script:pass passed, $script:fail failed"
if ($script:fail -gt 0) { exit 1 }
