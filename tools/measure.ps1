param([int]$Seconds = 60, [string]$Output = "$PSScriptRoot/../artifacts/performance.json", [string]$LogDirectory)
$ErrorActionPreference = 'Stop'
$samples = @()
for ($i = 0; $i -lt $Seconds; $i++) {
    $process = Get-Process -Name FontCrate -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $process) {
        if (-not $LogDirectory -and $process.Path) { $LogDirectory = Join-Path (Split-Path $process.Path -Parent) 'logs' }
        $samples += [pscustomobject]@{time = [DateTimeOffset]::Now; privateMiB = [math]::Round($process.PrivateMemorySize64 / 1MB, 2); workingMiB = [math]::Round($process.WorkingSet64 / 1MB, 2)}
    }
    Start-Sleep -Seconds 1
}
$logs = if ($LogDirectory -and (Test-Path -LiteralPath $LogDirectory -PathType Container)) {
    @(Get-ChildItem -LiteralPath $LogDirectory -File -Filter 'fontcrate-????-??-??.jsonl' | Sort-Object Name)
} else { @() }
$events = @($logs | ForEach-Object { Get-Content -LiteralPath $_.FullName -Encoding UTF8 } |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_ | ConvertFrom-Json })
$timings = @($events | Group-Object operation | ForEach-Object {
    $values = @($_.Group.milliseconds | Sort-Object)
    [pscustomobject]@{operation = $_.Name; count = $values.Count; p95Ms = $values[[math]::Max(0, [math]::Ceiling($values.Count * 0.95) - 1)]}
})
$parent = Split-Path $Output -Parent
New-Item -ItemType Directory -Path $parent -Force | Out-Null
[pscustomobject]@{os = [Environment]::OSVersion.VersionString; samples = $samples; timings = $timings} | ConvertTo-Json -Depth 8 | Set-Content -Path $Output -Encoding utf8
Write-Output $Output
