<#
 Smart-X device simulator (PowerShell) — behaves like the ESP32 fleet: one HTTP POST per reading.
 Usage:  .\scripts\device-simulator.ps1 [-BaseUrl http://localhost:59185] [-Seconds 30] [-Scenario normal|drought|spike|overheat|dropout]
#>
param(
    [string]$BaseUrl = "http://localhost:59185",
    [int]$Seconds = 30,
    [ValidateSet("normal", "drought", "spike", "overheat", "dropout")][string]$Scenario = "normal"
)

$api = "$BaseUrl/api/stream"
$sensors = @(
    @{ Mac = "A4:CF:12:00:00:01"; Metric = "moisture";    Base = 62;   Noise = 1.2 },
    @{ Mac = "A4:CF:12:00:00:02"; Metric = "moisture";    Base = 58;   Noise = 1.2 },
    @{ Mac = "A4:CF:12:00:00:03"; Metric = "temperature"; Base = 24;   Noise = 0.4 },
    @{ Mac = "A4:CF:12:00:00:04"; Metric = "temperature"; Base = 26;   Noise = 0.4 },
    @{ Mac = "A4:CF:12:00:00:05"; Metric = "power";       Base = 1800; Noise = 90 },
    @{ Mac = "A4:CF:12:00:00:06"; Metric = "power";       Base = 2300; Noise = 110 },
    @{ Mac = "A4:CF:12:00:00:07"; Metric = "power";       Base = 1400; Noise = 70 }
)

$stop = (Get-Date).AddSeconds($Seconds)
$tick = 0
$sent = 0
Write-Host "Simulating $($sensors.Count) sensors for ${Seconds}s - scenario: $Scenario"

while ((Get-Date) -lt $stop) {
    $tick++
    foreach ($s in $sensors) {
        $value = [math]::Round($s.Base + ((Get-Random -Minimum -1000 -Maximum 1000) / 1000.0) * $s.Noise, 2)

        if ($Scenario -eq "drought"  -and $s.Mac -eq "A4:CF:12:00:00:02") { $value = [math]::Max(9, 58 - 4 * $tick) }
        if ($Scenario -eq "spike"    -and $s.Mac -eq "A4:CF:12:00:00:06") { $value = 4600 }
        if ($Scenario -eq "overheat" -and $s.Mac -eq "A4:CF:12:00:00:04") { $value = [math]::Min(58, 26 + 3 * $tick) }
        if ($Scenario -eq "dropout"  -and $s.Mac -eq "A4:CF:12:00:00:07") { continue }

        $body = @{ deviceId = $s.Mac; metric = $s.Metric; value = $value; signalStrength = -(Get-Random -Minimum 55 -Maximum 70) } | ConvertTo-Json
        try {
            Invoke-RestMethod -Uri "$api/ingest" -Method Post -ContentType "application/json" -Body $body | Out-Null
            $sent++
        }
        catch { Write-Warning "$($s.Mac): $($_.Exception.Message)" }
    }
    Start-Sleep -Milliseconds 1500
}
Write-Host "Done - $sent packets accepted."
