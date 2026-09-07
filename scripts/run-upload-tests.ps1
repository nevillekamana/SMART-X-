param(
	[Parameter(Mandatory=$true)]
	[string]$SensorId
)

$base = "http://localhost:59185/api/sensors/$SensorId/attachments"
$files = @("test_files/valid.png","test_files/valid.jpg","test_files/valid.pdf","test_files/invalid_magic.png")

foreach($f in $files){
	if(-not (Test-Path $f)){
		Write-Host "SKIP: $f (missing)"
		continue
	}
	try{
		Write-Host "Uploading $f ..."
		$r = Invoke-WebRequest -Uri $base -Method Post -Form @{ file = Get-Item $f } -UseBasicParsing -ErrorAction Stop
		Write-Host " -> Status: $($r.StatusCode)"
		Write-Host " -> Body: $($r.Content)"
	} catch {
		if($_.Exception.Response){
			$code = $_.Exception.Response.StatusCode.Value__
			Write-Host " -> Error: HTTP $code - $($_.Exception.Message)"
		} else {
			Write-Host " -> Error: $($_.Exception.Message)"
		}
	}
}

Write-Host "Uploads folder contents:"
Get-ChildItem -Path "wwwroot\uploads\$SensorId" -Recurse -File -ErrorAction SilentlyContinue | Select-Object Name,Length | Format-Table -AutoSize
