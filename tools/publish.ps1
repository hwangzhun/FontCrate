param(
    [string]$Output = "$PSScriptRoot/../artifacts",
    [string]$IsccPath,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/..").Path
$outputPath = [IO.Path]::GetFullPath($Output)
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Installer publishing requires Windows, .NET 10 SDK and Inno Setup 6.5 or newer.'
}
if (-not $IsccPath) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $IsccPath = $command.Source }
    else {
        foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
            if (-not $base) { continue }
            $candidate = Join-Path $base 'Inno Setup 6/ISCC.exe'
            if (Test-Path $candidate) { $IsccPath = $candidate; break }
        }
    }
}
if (-not $IsccPath -or -not (Test-Path $IsccPath)) {
    throw 'Inno Setup compiler not found. Install Inno Setup 6.5+ from https://jrsoftware.org/isdl.php or pass -IsccPath.'
}

# Isolate each build so old DLLs, installers and debugging files cannot enter the release.
$staging = Join-Path ([IO.Path]::GetTempPath()) ("FontCrate-release-" + [Guid]::NewGuid().ToString('N'))
$publish = Join-Path $staging 'publish'
$release = Join-Path $staging 'release'
$sourceStaging = Join-Path $staging 'source'
try {
    New-Item -ItemType Directory -Path $publish, $release, $sourceStaging -Force | Out-Null
    if (-not $SkipTests) {
        dotnet test "$root/tests/FontCrate.Tests/FontCrate.Tests.csproj" -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Core tests failed' }
    }
    $manifest = Join-Path $staging 'bundle-manifest.txt'
    dotnet publish "$root/src/FontCrate.App/FontCrate.App.csproj" -c Release -p:PublishProfile=WinX64 -p:DebugType=embedded "-p:BundleManifestPath=$manifest" -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    $payload = @(Get-Content $manifest | ForEach-Object { $_.Replace('\', '/') })
    foreach ($required in @('FontCrate.dll', 'FontCrate.Core.dll', 'FontCrate.Windows.dll', 'Wpf.Ui.dll', 'Wpf.Ui.Abstractions.dll', 'System.Private.CoreLib.dll', 'PresentationFramework.dll', 'e_sqlite3.dll', 'zh-Hans/PresentationFramework.resources.dll', 'FontCrate.runtimeconfig.json')) {
        if ($payload -notcontains $required) { throw "Dependency missing from single-file bundle: $required" }
    }
    foreach ($resource in @($payload | Where-Object { $_ -like '*.resources.dll' })) {
        if ($resource -notmatch '^(en|zh-Hans)/') { throw "Unexpected bundled language: $resource" }
    }
    if ($payload | Where-Object { $_ -like '*.md' }) { throw 'Markdown entered the bundle.' }
    # Fail if the publish profile ever starts leaking sidecar DLLs or resources.
    $publishedFiles = @(Get-ChildItem $publish -File -Recurse)
    if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].Name -ne 'FontCrate.exe') {
        throw 'Expected a single FontCrate.exe with no sidecar files.'
    }
    $exeVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publish 'FontCrate.exe'))
    $version = '{0}.{1}.{2}' -f $exeVersion.FileMajorPart, $exeVersion.FileMinorPart, $exeVersion.FileBuildPart
    # Used by the installer license page; the [Files] whitelist installs only the EXE.
    # The application reads its own license and third-party notices from embedded resources.
    Copy-Item (Join-Path $root 'LICENSE') $publish

    & $IsccPath "/DPublishDir=$publish" "/DOutputDir=$release" "/DAppVersion=$version" "$root/tools/installer/FontCrate.iss"
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
    $installerName = "FontCrate-Setup-$version-win-x64.exe"
    if (-not (Test-Path (Join-Path $release $installerName))) { throw 'Installer output missing.' }

    # Include corresponding source, including installer scripts and hidden GitHub files.
    $parts = @('src', 'tests', 'tools', 'docs', 'licenses', '.github', '.gitignore', '.gitattributes', 'Directory.Build.props', 'global.json', 'FontCrate.sln', 'README.md', 'README.zh-CN.md', 'THIRD-PARTY-NOTICES.md', 'LICENSE', 'fontcrate.png')
    foreach ($part in $parts) {
        $item = Get-Item (Join-Path $root $part) -Force
        $files = if ($item.PSIsContainer) { Get-ChildItem $item.FullName -File -Recurse -Force } else { @($item) }
        foreach ($file in $files) {
            if ($file.FullName -match '[\\/](bin|obj|TestResults|\.vs|\.idea)[\\/]' -or $file.Name -match '\.(trx|log|user|suo)$' -or $file.Name -match '\.db($|[.-])') { continue }
            $relative = $file.FullName.Substring($root.Length + 1)
            $target = Join-Path $sourceStaging $relative
            New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
            Copy-Item $file.FullName $target
        }
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($sourceStaging, (Join-Path $release 'FontCrate-source.zip'))
    $hashes = Get-ChildItem $release -File | Get-FileHash -Algorithm SHA256
    $hashes | ForEach-Object { "$($_.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_.Path))" } | Set-Content (Join-Path $release 'SHA256SUMS.txt') -Encoding ascii
    # Only completed release files are copied out; staging is never uploaded.
    New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
    Get-ChildItem $release -File | Copy-Item -Destination $outputPath -Force
    $hashes | Format-Table
    Write-Host "Installer: $(Join-Path $outputPath $installerName)"
}
finally { Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue }
