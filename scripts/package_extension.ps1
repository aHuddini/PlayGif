# PlayGif Extension Packaging Script
# Creates a .pext package for Playnite installation
#
# Usage: .\package_extension.ps1 [-Configuration Release|Debug]
#
# Note: This script packages an already-built project. Build first with:
#   dotnet build -c Release

param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  PlayGif Extension Packaging" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Configuration: $Configuration" -ForegroundColor Yellow
Write-Host ""

# Get project root (one level up from scripts/)
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptDir
Set-Location $projectRoot

# Read version from version.txt (single source of truth)
$versionFile = Join-Path $projectRoot "version.txt"
if (-not (Test-Path $versionFile)) {
    Write-Host "ERROR: version.txt not found." -ForegroundColor Red
    exit 1
}
$version = (Get-Content $versionFile -Raw).Trim()
Write-Host "Version: $version" -ForegroundColor Yellow

# Update extension.yaml with version from version.txt
$extensionYaml = Join-Path $projectRoot "extension.yaml"
if (Test-Path $extensionYaml) {
    $content = Get-Content $extensionYaml -Raw
    $content = $content -replace "Version:.*", "Version: $version"
    Set-Content $extensionYaml $content -NoNewline
    Write-Host "Updated extension.yaml version to $version" -ForegroundColor Gray
}

# Check for built DLL
$dllPath = Join-Path $projectRoot "src\bin\$Configuration\net4.6.2\PlayGif.dll"
if (-not (Test-Path $dllPath)) {
    Write-Host "ERROR: PlayGif.dll not found at $dllPath. Build first." -ForegroundColor Red
    exit 1
}

# Create clean package directory
$packageDir = Join-Path $projectRoot "package"
if (Test-Path $packageDir) {
    Remove-Item $packageDir -Recurse -Force
}
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null

Write-Host "Copying extension files..." -ForegroundColor Yellow

# Copy core files
$coreFiles = @(
    (Join-Path $projectRoot "extension.yaml"),
    (Join-Path $projectRoot "icon.png"),
    (Join-Path $projectRoot "LICENSE")
)
foreach ($file in $coreFiles) {
    if (Test-Path $file) {
        Copy-Item $file -Destination $packageDir -Force
        Write-Host "  Copied: $(Split-Path $file -Leaf)" -ForegroundColor Gray
    }
}

# Copy main DLL
Copy-Item $dllPath -Destination $packageDir -Force
Write-Host "  Copied: PlayGif.dll" -ForegroundColor Gray

# Copy dependencies from build output (exclude SDK and system DLLs)
$excludedDlls = @(
    "Playnite.SDK.dll",
    "PlayGif.dll"
)
$systemPrefixes = @("System.", "Microsoft.CSharp.", "Microsoft.VisualBasic.")
$buildOutput = Join-Path $projectRoot "src\bin\$Configuration\net4.6.2"
foreach ($dll in (Get-ChildItem "$buildOutput\*.dll")) {
    $excluded = $false
    foreach ($pattern in $excludedDlls) {
        if ($dll.Name -eq $pattern) {
            $excluded = $true
            break
        }
    }
    if (-not $excluded) {
        foreach ($prefix in $systemPrefixes) {
            if ($dll.Name.StartsWith($prefix)) {
                $excluded = $true
                break
            }
        }
    }
    if (-not $excluded) {
        Copy-Item $dll.FullName -Destination $packageDir -Force
        Write-Host "  Copied: $($dll.Name)" -ForegroundColor Gray
    }
}

# Copy runtimes directory (WebView2 native loaders)
$runtimesDir = Join-Path $buildOutput "runtimes"
if (Test-Path $runtimesDir) {
    Copy-Item $runtimesDir -Destination $packageDir -Recurse -Force
    Write-Host "  Copied: runtimes/ (WebView2 native DLLs)" -ForegroundColor Gray
}

# Create .pext file (ZIP with .pext extension)
$versionSafe = $version -replace '\.', '_'
$pextName = "PlayGif.2e196d25-24d1-4db3-b732-9766c994a496_$versionSafe.pext"
$pextDir = Join-Path $projectRoot "pext"
if (-not (Test-Path $pextDir)) {
    New-Item -ItemType Directory -Path $pextDir -Force | Out-Null
}
$pextPath = Join-Path $pextDir $pextName

if (Test-Path $pextPath) {
    Remove-Item $pextPath -Force
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($packageDir, $pextPath)

$pextSize = (Get-Item $pextPath).Length / 1KB
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Package created successfully!" -ForegroundColor Green
Write-Host "  $pextName ($([math]::Round($pextSize, 1)) KB)" -ForegroundColor Yellow
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Package contents:" -ForegroundColor Yellow
foreach ($item in (Get-ChildItem $packageDir -Recurse -File)) {
    $relPath = $item.FullName.Replace("$packageDir\", "")
    $size = $item.Length / 1KB
    Write-Host "  - $relPath ($([math]::Round($size, 2)) KB)" -ForegroundColor Gray
}
Write-Host ""
Write-Host "To install in Playnite:" -ForegroundColor Yellow
Write-Host "  1. Open Playnite" -ForegroundColor Gray
Write-Host "  2. Go to Add-ons -> Extensions" -ForegroundColor Gray
Write-Host "  3. Click 'Add extension' and select the .pext file" -ForegroundColor Gray
