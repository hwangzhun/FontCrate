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
    foreach ($file in @('FontCrate.exe', 'FontCrate.dll', 'Wpf.Ui.dll', 'Wpf.Ui.Abstractions.dll', 'coreclr.dll', 'e_sqlite3.dll', 'LICENSE')) {
        if (-not (Test-Path (Join-Path $installDir $file))) { throw "Installed file missing: $file" }
    }
    foreach ($excluded in @('docs', 'licenses', 'THIRD-PARTY-NOTICES.md')) {
        if (Test-Path (Join-Path $installDir $excluded)) { throw "Excluded content entered the installation: $excluded" }
    }
    foreach ($directory in @(Get-ChildItem $installDir -Directory)) {
        if ($directory.Name -notmatch '^(en|zh)(-|$)' -and
            (Get-ChildItem $directory.FullName -Filter '*.resources.dll' -File)) {
            throw "Unexpected resource language entered the installation: $($directory.Name)"
        }
    }
    foreach ($language in @('zh-Hans', 'zh-Hant')) {
        if (-not (Test-Path (Join-Path $installDir "$language/PresentationFramework.resources.dll"))) {
            throw "Chinese framework resources missing: $language"
        }
    }
    if (Get-ChildItem $installDir -Filter '*.pdb' -Recurse -File) { throw 'Debug symbols entered the installation.' }
    $runtime = Get-Content (Join-Path $installDir 'FontCrate.runtimeconfig.json') -Raw | ConvertFrom-Json
    if (-not $runtime.runtimeOptions.includedFrameworks -or $runtime.runtimeOptions.framework -or $runtime.runtimeOptions.frameworks) { throw 'Published application is not self-contained.' }
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

    # Reinstall over the same location; this exercises upgrade file replacement.
    Invoke-Setup $installerPath $arguments
    if (-not (Test-Path $marker)) { throw 'Upgrade removed user data.' }
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
