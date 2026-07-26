# Builds the PlayGif branding kit: per-layer SVGs plus PNG rasters.
# Rasterizes with headless Edge (WebView2 runtime is already a project dependency),
# so no ImageMagick/Inkscape install is required.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$brand = Join-Path $root "branding"

$edge = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "${env:ProgramFiles}\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $edge) { throw "Microsoft Edge not found - required to rasterize SVG." }

function Export-Png {
    param([string]$SvgPath, [string]$PngPath, [int]$Width, [int]$Height)

    $uri = ([System.Uri]$SvgPath).AbsoluteUri
    $tmp = Join-Path $env:TEMP ("pgshot_" + [System.Guid]::NewGuid().ToString("N"))

    # Edge reports success on stderr; PS 5.1 turns that into a NativeCommandError,
    # so run it through cmd and discard both streams.
    $args = "--headless --disable-gpu --hide-scrollbars --force-device-scale-factor=1 " +
            "--default-background-color=00000000 " +
            "--screenshot=`"$PngPath`" --window-size=$Width,$Height " +
            "--user-data-dir=`"$tmp`" `"$uri`""
    cmd /c "`"$edge`" $args >nul 2>&1"

    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue

    if (-not (Test-Path $PngPath)) { throw "Failed to rasterize $SvgPath" }
    Write-Host ("  {0} ({1}x{2}, {3:N0} bytes)" -f (Split-Path $PngPath -Leaf), $Width, $Height, (Get-Item $PngPath).Length)
}

# Splits a composite SVG into one file per <g id="...">, preserving defs.
function Split-Layers {
    param([string]$SvgPath, [string]$Prefix)

    $svg = Get-Content $SvgPath -Raw
    $header = [regex]::Match($svg, '(?s)^.*?<defs>.*?</defs>').Value
    if (-not $header) { $header = [regex]::Match($svg, '(?s)^.*?>').Value }

    foreach ($m in [regex]::Matches($svg, '(?s)<g id="(\w+)".*?</g>')) {
        $name = $m.Groups[1].Value
        $out = Join-Path $brand "layer-$Prefix-$name.svg"
        ($header + "`n  " + $m.Value + "`n</svg>`n") | Set-Content $out -Encoding utf8
        Write-Host "  layer-$Prefix-$name.svg"
    }
}

Write-Host "`nSplitting layers..." -ForegroundColor Cyan
Split-Layers (Join-Path $brand "PlayGif-icon.svg") "icon"
Split-Layers (Join-Path $brand "PlayGif.svg") "banner"

Write-Host "`nRasterizing..." -ForegroundColor Cyan
Export-Png (Join-Path $brand "PlayGif-icon.svg") (Join-Path $brand "PlayGif-icon-preview.png") 256 256
Export-Png (Join-Path $brand "PlayGif.svg") (Join-Path $brand "PlayGif-preview.png") 640 256

foreach ($f in Get-ChildItem $brand -Filter "layer-*.svg") {
    $png = Join-Path $brand ($f.BaseName + ".png")
    if ($f.BaseName -like "*icon*") { Export-Png $f.FullName $png 256 256 }
    else { Export-Png $f.FullName $png 640 256 }
}

# The extension icon Playnite displays - lives at the project root
Write-Host "`nExtension icon..." -ForegroundColor Cyan
Export-Png (Join-Path $brand "PlayGif-icon.svg") (Join-Path $root "icon.png") 256 256

Write-Host "`nBranding kit built." -ForegroundColor Green
