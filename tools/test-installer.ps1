param([Parameter(Mandatory)][string]$Installer)
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Installer smoke tests require Windows.' }
$installerPath = (Resolve-Path $Installer).Path
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\FontCrate_is1'
if (Test-Path $uninstallKey) { throw 'Use a disposable Windows environment without an existing FontCrate installation.' }
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("FontCrate 安装测试 " + [Guid]::NewGuid().ToString('N'))
$installDir = Join-Path $testRoot '程序 files'
$dataDir = Join-Path $env:LOCALAPPDATA 'FontCrate'
if (-not (Test-Path $dataDir) -and (Test-Path (Join-Path $env:LOCALAPPDATA 'FontLite'))) { $dataDir = Join-Path $env:LOCALAPPDATA 'FontLite' }
$marker = Join-Path $dataDir ("installer-test-" + [Guid]::NewGuid().ToString('N') + '.txt')
$guard = $null
Write-Host "Installer test directory: $testRoot"
function Invoke-Setup([string]$Executable, [string[]]$SetupArguments, [bool]$ExpectSuccess = $true) {
    $process = Start-Process -FilePath $Executable -ArgumentList $SetupArguments -PassThru
    if (-not $process.WaitForExit(60000)) {
        $process.Kill()
        throw 'Installer test timed out (unexpected interaction).'
    }
    $process.Refresh()
    if ($ExpectSuccess -and $process.ExitCode -ne 0) { throw "Installer command failed: $($process.ExitCode)" }
    if (-not $ExpectSuccess -and $process.ExitCode -eq 0) { throw 'Running application guard did not block the installer command.' }
}
try {
    New-Item -ItemType Directory -Path $testRoot, $dataDir -Force | Out-Null
    Set-Content $marker 'Preserve user data across install, upgrade and uninstall.' -Encoding utf8
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/DIR=`"$installDir`"", "/LOG=`"$(Join-Path $testRoot 'install.log')`"")
    $guard = [Threading.Mutex]::new($false, 'Local\FontCrate-Running')
    Invoke-Setup $installerPath $arguments $false
    $guard.Dispose(); $guard = $null
    if (Test-Path (Join-Path $installDir 'FontCrate.exe')) { throw 'Blocked installation wrote application files.' }

    Invoke-Setup $installerPath $arguments
    if (-not (Test-Path (Join-Path $installDir 'FontCrate.exe'))) { throw 'Application executable missing.' }
    foreach ($file in @(Get-ChildItem $installDir -File -Recurse)) {
        if ($file.Name -notmatch '^(FontCrate\.exe|unins[0-9]+\.(exe|dat|msg))$') {
            throw "Unexpected installed sidecar file: $($file.FullName)"
        }
    }
    if (-not (Test-Path $uninstallKey)) { throw 'Current-user uninstall registration missing.' }
    $registeredDir = (Get-ItemProperty $uninstallKey).InstallLocation
    if ($registeredDir.TrimEnd('\') -ne $installDir.TrimEnd('\')) { throw 'Unexpected registered installation directory.' }
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'FontCrate.lnk'
    if (-not (Test-Path $shortcut)) { throw 'Start menu shortcut missing.' }
    # Read the Unicode shell property: WScript.Shell can return a legacy path
    # representation for a shortcut whose target contains Chinese characters.
    $shell = New-Object -ComObject Shell.Application
    $shortcutItem = $shell.NameSpace((Split-Path $shortcut -Parent)).ParseName((Split-Path $shortcut -Leaf))
    $targetPath = $shortcutItem.ExtendedProperty('System.Link.TargetParsingPath')
    $expectedTarget = Join-Path $installDir 'FontCrate.exe'
    if ($targetPath -ne $expectedTarget) { throw "Shortcut target '$targetPath' does not match '$expectedTarget'." }

    # Emulate old app sidecars to verify migration to single-file deployment.
    foreach ($legacy in @('FontCrate.dll', 'coreclr.dll', 'FontCrate.runtimeconfig.json', 'README.md', 'createdump.exe')) {
        Set-Content (Join-Path $installDir $legacy) 'old application payload'
    }
    New-Item -ItemType Directory -Path (Join-Path $installDir 'zh-Hant') -Force | Out-Null
    Set-Content (Join-Path $installDir 'zh-Hant/PresentationFramework.resources.dll') 'old language resource'
    # Reinstall over the same location; this exercises upgrade file replacement.
    Invoke-Setup $installerPath $arguments
    if (-not (Test-Path $marker)) { throw 'Upgrade removed user data.' }
    foreach ($legacy in @('FontCrate.dll', 'coreclr.dll', 'FontCrate.runtimeconfig.json', 'README.md', 'createdump.exe')) {
        if (Test-Path (Join-Path $installDir $legacy)) { throw "Upgrade left a legacy sidecar: $legacy" }
    }
    if (Test-Path (Join-Path $installDir 'zh-Hant')) { throw 'Upgrade left the old language folder.' }
    $uninstaller = Join-Path $installDir 'unins000.exe'
    $guard = [Threading.Mutex]::new($false, 'Local\FontCrate-Running')
    Invoke-Setup $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') $false
    $guard.Dispose(); $guard = $null
    if (-not (Test-Path (Join-Path $installDir 'FontCrate.exe'))) { throw 'Blocked uninstall removed application files.' }
    Invoke-Setup $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    if (Test-Path (Join-Path $installDir 'FontCrate.exe')) { throw 'Uninstall left the application executable behind.' }
    if (Test-Path $uninstallKey) { throw 'Uninstall registration was not removed.' }
    if (Test-Path $shortcut) { throw 'Start menu shortcut was not removed.' }
    if (-not (Test-Path $marker)) { throw 'Uninstall removed user data.' }
    Write-Host 'Installer smoke tests passed: install, reinstall, mutex guards, user-data preservation and uninstall.'
}
finally {
    if ($guard) { $guard.Dispose() }
    # A failed test must not leave a registered installation on the disposable runner.
    $uninstaller = Join-Path $installDir 'unins000.exe'
    if (Test-Path $uninstaller) {
        try { Invoke-Setup $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') }
        catch { Write-Warning "Cleanup uninstall failed: $_" }
    }
    $logOutput = Join-Path $PSScriptRoot '../artifacts/installer-test-logs'
    New-Item -ItemType Directory -Path $logOutput -Force | Out-Null
    Get-ChildItem $testRoot -Filter '*.log' -File -ErrorAction SilentlyContinue | Copy-Item -Destination $logOutput -Force
    Remove-Item $marker -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path $uninstallKey)) {
        Remove-Item $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    } else {
        Write-Warning "Installation remains registered; keep $testRoot for diagnostics."
    }
}
