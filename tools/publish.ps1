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
    dotnet publish "$root/src/FontCrate.App/FontCrate.App.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -p:CopyOutputSymbolsToPublishDirectory=false -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    foreach ($required in @('FontCrate.exe', 'FontCrate.dll', 'FontCrate.Core.dll', 'FontCrate.Windows.dll', 'Wpf.Ui.dll', 'Wpf.Ui.Abstractions.dll', 'coreclr.dll', 'PresentationFramework.dll', 'e_sqlite3.dll')) {
        if (-not (Test-Path (Join-Path $publish $required))) { throw "Missing application dependency: $required" }
    }
    Get-ChildItem $publish -Filter '*.pdb' -Recurse -File | Remove-Item -Force
    # Keep Chinese (Simplified and Traditional) and English satellite resources.
    # English fallback resources are also embedded in the main framework DLLs.
    Get-ChildItem $publish -Directory | Where-Object {
        $_.Name -notmatch '^(en|zh)(-|$)' -and
        (Get-ChildItem $_.FullName -Filter '*.resources.dll' -File)
    } | Remove-Item -Recurse -Force

    # FontCrate.exe is the native apphost: read the managed assembly identity from
    # its companion DLL, then require the EXE's version resource to match.
    $assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $publish 'FontCrate.dll')).Version
    $version = $assemblyVersion.ToString(3)
    $exeVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publish 'FontCrate.exe'))
    if ($exeVersion.FileMajorPart -ne $assemblyVersion.Major -or $exeVersion.FileMinorPart -ne $assemblyVersion.Minor -or $exeVersion.FileBuildPart -ne $assemblyVersion.Build) {
        throw 'Published EXE and managed assembly versions do not match.'
    }
    foreach ($name in @('README.zh-CN.md', 'README.md', 'LICENSE')) {
        Copy-Item (Join-Path $root $name) $publish
    }
    foreach ($excluded in @('docs', 'licenses', 'THIRD-PARTY-NOTICES.md')) {
        $excludedPath = Join-Path $publish $excluded
        if (Test-Path $excludedPath) { Remove-Item $excludedPath -Recurse -Force }
    }

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
