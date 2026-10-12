param([string]$Output = "$PSScriptRoot/../artifacts")
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/..").Path
$outputPath = [IO.Path]::GetFullPath($Output)
$publish = Join-Path $outputPath 'FontCrate-win-x64'
dotnet test "$root/tests/FontCrate.Tests/FontCrate.Tests.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw 'Core tests failed' }
dotnet publish "$root/src/FontCrate.App/FontCrate.App.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
foreach ($required in @('FontCrate.exe', 'Wpf.Ui.dll', 'Wpf.Ui.Abstractions.dll')) {
    if (-not (Test-Path (Join-Path $publish $required))) { throw "Missing UI dependency: $required" }
}
Copy-Item "$root/README.zh-CN.md" $publish
Copy-Item "$root/README.md" $publish
Copy-Item "$root/LICENSE" $publish
Copy-Item "$root/THIRD-PARTY-NOTICES.md" $publish
Copy-Item "$root/licenses" $publish -Recurse -Force
foreach ($requiredLicense in @('WPF-UI-MIT.txt', 'Fluent-System-Icons-MIT.txt')) {
    if (-not (Test-Path (Join-Path $publish "licenses/$requiredLicense"))) { throw "Missing license: $requiredLicense" }
}
Copy-Item "$root/docs" $publish -Recurse -Force
$packageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget/packages' }
foreach ($package in @('microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64')) {
    $packagePath = Join-Path $packageRoot $package
    if (-not (Test-Path $packagePath)) { continue }
    Get-ChildItem $packagePath -Recurse -File | Where-Object { $_.Name -match '^(LICENSE.*|THIRD-PARTY-NOTICES.*)$' } | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $publish "licenses/$package-$($_.Name)") -Force
    }
}
Compress-Archive -Path "$publish/*" -DestinationPath (Join-Path $outputPath 'FontCrate-win-x64.zip') -Force
Get-FileHash (Join-Path $outputPath 'FontCrate-win-x64.zip') -Algorithm SHA256

# Include the corresponding source alongside each GPL release.
$sourceStaging = Join-Path ([IO.Path]::GetTempPath()) ("FontCrate-source-" + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $sourceStaging -Force | Out-Null
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
    # ZipFile includes hidden .github and Git configuration files on every OS.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $sourceZip = Join-Path $outputPath 'FontCrate-source.zip'
    if (Test-Path $sourceZip) { Remove-Item $sourceZip -Force }
    [IO.Compression.ZipFile]::CreateFromDirectory($sourceStaging, $sourceZip)
}
finally { Remove-Item $sourceStaging -Recurse -Force -ErrorAction SilentlyContinue }
