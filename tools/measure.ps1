param([int]$Seconds = 60, [string]$Output = "$PSScriptRoot/../artifacts/performance.json")
$ErrorActionPreference = 'Stop'
$samples = @()
for ($i = 0; $i -lt $Seconds; $i++) {
    $process = Get-Process -Name FontCrate -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $process) { $samples += [pscustomobject]@{time = [DateTimeOffset]::Now; privateMiB = [math]::Round($process.PrivateMemorySize64 / 1MB, 2); workingMiB = [math]::Round($process.WorkingSet64 / 1MB, 2)} }
    Start-Sleep -Seconds 1
}
$dataDirectory = Join-Path $env:LOCALAPPDATA 'FontCrate'
$legacyDirectory = Join-Path $env:LOCALAPPDATA 'FontLite'
if (-not (Test-Path $dataDirectory) -and (Test-Path $legacyDirectory)) { $dataDirectory = $legacyDirectory }
$log = Join-Path $dataDirectory 'performance.jsonl'
$events = if (Test-Path $log) { @(Get-Content $log | ForEach-Object { $_ | ConvertFrom-Json }) } else { @() }
$timings = @($events | Group-Object operation | ForEach-Object {
    $values = @($_.Group.milliseconds | Sort-Object)
    [pscustomobject]@{operation = $_.Name; count = $values.Count; p95Ms = $values[[math]::Max(0, [math]::Ceiling($values.Count * 0.95) - 1)]}
})
$parent = Split-Path $Output -Parent
New-Item -ItemType Directory -Path $parent -Force | Out-Null
[pscustomobject]@{os = [Environment]::OSVersion.VersionString; samples = $samples; timings = $timings} | ConvertTo-Json -Depth 8 | Set-Content -Path $Output -Encoding utf8
Write-Output $Output
