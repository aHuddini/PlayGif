# Builds the animated PlayGif logo: an APNG/GIF where the slash sweeps across
# the screen, turning muted text lines into live blue bars as it passes.
#
# Frames are generated as SVG (one per sweep position), rasterized with headless
# Edge, then assembled by ffmpeg. ffmpeg's palettegen/paletteuse is used because
# GIF is limited to 256 colours and a naive quantize bands the blue gradient.

$ErrorActionPreference = "Stop"
$root  = Split-Path -Parent $PSScriptRoot
$brand = Join-Path $root "branding"
$work  = Join-Path $env:TEMP "playgif_anim"

$edge = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "${env:ProgramFiles}\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw "Microsoft Edge not found - required to rasterize SVG." }

$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg) { throw "ffmpeg not found on PATH - required to assemble the GIF." }

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Path $work | Out-Null

# Sweep geometry. The cut travels from off-screen right to off-screen left;
# at each step the before/after clip boundary moves with it.
$FRAMES = 22
$SIZE   = 256

# Cut direction is fixed (down-left at the same angle); only its offset moves.
# x at the top edge of the screen travels from 250 (off right) to 8 (off left).
$xStart = 268
$xEnd   = 213

function New-FrameSvg {
    param([int]$Index, [double]$T)

    # Ease so the blade accelerates in and settles at the end
    $e = if ($T -lt 0.5) { 2*$T*$T } else { 1 - [Math]::Pow(-2*$T + 2, 2)/2 }
    $xTop = $xStart + ($xEnd - $xStart) * $e
    $xBot = $xTop - 156          # horizontal run of the cut over the screen height

    # Sparks fade in behind the blade and decay
    $sparkOps = @(0.85, 0.7, 0.6, 0.5, 0.4)
    $sparkXs  = @(207, 193, 164, 146, 115)
    $sparkYs  = @(68.4, 96.5, 104.1, 128.7, 137.6)
    $sparkRs  = @(5.0, 4.0, 3.4, 2.9, 2.4)

    $sparks = ""
    for ($i = 0; $i -lt 5; $i++) {
        # A spark lights only once the blade has passed it, then fades
        $passed = ($sparkXs[$i] -gt $xBot) -and ($sparkXs[$i] -lt $xTop + 40)
        $age = ($xTop - $sparkXs[$i]) / 90.0
        $op = 0.0
        if ($age -gt 0 -and $age -lt 1.4) {
            $op = $sparkOps[$i] * [Math]::Max(0.0, 1.0 - $age/1.4)
        }
        if ($op -gt 0.02) {
            $x = $sparkXs[$i]; $y = $sparkYs[$i]; $r = $sparkRs[$i]; $s = $r*0.34
            $sparks += ('<path d="M {0} {1} L {2} {3} L {4} {5} L {2} {6} L {0} {7} L {8} {6} L {9} {5} L {8} {3} Z" fill-opacity="{10}"/>' -f `
                $x, ($y-$r), ($x+$s), ($y-$s), ($x+$r), $y, ($y+$s), ($y+$r), ($x-$s), ($x-$r), [Math]::Round($op,3))
        }
    }

    @"
<svg xmlns="http://www.w3.org/2000/svg" width="$SIZE" height="$SIZE" viewBox="0 0 256 256">
<defs>
<linearGradient id="acc" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#3B82F6"/><stop offset="1" stop-color="#22D3EE"/></linearGradient>
<linearGradient id="glass" x1="0" y1="0" x2="0.6" y2="1"><stop offset="0" stop-color="#60A5FA" stop-opacity="0.20"/><stop offset="1" stop-color="#0EA5E9" stop-opacity="0.05"/></linearGradient>
<clipPath id="scr"><rect x="27" y="41" width="202" height="116" rx="12"/></clipPath>
<clipPath id="beforeCut"><path d="M 27 41 L $xTop 41 L $xBot 157 L 27 157 Z"/></clipPath>
<clipPath id="afterCut"><path d="M $xTop 41 L 229 41 L 229 157 L $xBot 157 Z"/></clipPath>
</defs>
<rect x="16" y="30" width="224" height="138" rx="20" fill="#0B1220" stroke="url(#acc)" stroke-width="9"/>
<rect x="27" y="41" width="202" height="116" rx="12" fill="url(#glass)"/>
<g clip-path="url(#scr)">
  <g clip-path="url(#beforeCut)" fill="#64748B" fill-opacity="0.7">
    <rect x="44" y="62" width="86" height="9" rx="4.5"/>
    <rect x="44" y="82" width="110" height="9" rx="4.5"/>
    <rect x="44" y="102" width="72" height="9" rx="4.5"/>
    <rect x="44" y="122" width="96" height="9" rx="4.5"/>
  </g>
  <g clip-path="url(#afterCut)" fill="url(#acc)">
    <rect x="44" y="62" width="150" height="9" rx="4.5"/>
    <rect x="44" y="82" width="150" height="9" rx="4.5"/>
    <rect x="44" y="102" width="150" height="9" rx="4.5"/>
    <rect x="44" y="122" width="150" height="9" rx="4.5"/>
  </g>
  <path d="M $xTop 38 L $xBot 160" stroke="#F0F9FF" stroke-width="7" stroke-linecap="round"/>
  <path d="M $xTop 38 L $xBot 160" stroke="url(#acc)" stroke-width="3" stroke-linecap="round"/>
</g>
<g clip-path="url(#afterCut)" fill="#F0F9FF">$sparks</g>
<rect x="118" y="168" width="20" height="20" fill="url(#acc)"/>
<rect x="74" y="188" width="108" height="12" rx="6" fill="url(#acc)"/>
</svg>
"@
}

Write-Host "`nGenerating $FRAMES frames..." -ForegroundColor Cyan
for ($i = 0; $i -lt $FRAMES; $i++) {
    $t = $i / [double]($FRAMES - 1)
    $svgPath = Join-Path $work ("f{0:d3}.svg" -f $i)
    $pngPath = Join-Path $work ("f{0:d3}.png" -f $i)
    New-FrameSvg -Index $i -T $t | Set-Content $svgPath -Encoding utf8

    $uri = ([System.Uri]$svgPath).AbsoluteUri
    $tmp = Join-Path $work ("u{0:d3}" -f $i)
    $a = "--headless --disable-gpu --hide-scrollbars --force-device-scale-factor=1 " +
         "--default-background-color=00000000 --screenshot=`"$pngPath`" " +
         "--window-size=$SIZE,$SIZE --user-data-dir=`"$tmp`" `"$uri`""
    cmd /c "`"$edge`" $a >nul 2>&1"
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path $pngPath)) { throw "Frame $i failed to render" }
}
Write-Host "  $FRAMES frames rendered"

# Hold the final composed frame so the loop reads as "slash, then rest"
$last = Join-Path $work ("f{0:d3}.png" -f ($FRAMES-1))
for ($h = 0; $h -lt 8; $h++) {
    Copy-Item $last (Join-Path $work ("f{0:d3}.png" -f ($FRAMES + $h)))
}

Write-Host "`nAssembling GIF..." -ForegroundColor Cyan
$pal = Join-Path $work "palette.png"
$pattern = Join-Path $work "f%03d.png"
$gif = Join-Path $brand "PlayGif-animated.gif"

# Two-pass palette keeps the blue gradient from banding
& $ffmpeg -y -loglevel error -framerate 24 -i $pattern `
    -vf "palettegen=stats_mode=diff:max_colors=160:reserve_transparent=1" $pal
& $ffmpeg -y -loglevel error -framerate 24 -i $pattern -i $pal `
    -lavfi "paletteuse=dither=bayer:bayer_scale=3:diff_mode=rectangle:alpha_threshold=128" `
    -loop 0 $gif

if (-not (Test-Path $gif)) { throw "GIF assembly failed" }
Write-Host ("  PlayGif-animated.gif ({0:N0} bytes)" -f (Get-Item $gif).Length)

# APNG keeps full alpha and 24-bit colour - better for the README
$apng = Join-Path $brand "PlayGif-animated.png"
& $ffmpeg -y -loglevel error -framerate 24 -i $pattern `
    -plays 0 -f apng $apng
if (Test-Path $apng) {
    Write-Host ("  PlayGif-animated.png ({0:N0} bytes, APNG - full alpha)" -f (Get-Item $apng).Length)
}

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "`nAnimation built." -ForegroundColor Green
