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
$FRAMES = 18
$SIZE   = 220

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
    $sparkXs  = @(203, 189, 165, 143, 117)
    $sparkYs  = @(76.4, 97.4, 105.7, 122.8, 133.6)
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
<linearGradient id="media" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#7C3AED"/><stop offset="0.5" stop-color="#DB2777"/><stop offset="1" stop-color="#F59E0B"/></linearGradient>
<linearGradient id="media2" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#0EA5E9"/><stop offset="0.55" stop-color="#14B8A6"/><stop offset="1" stop-color="#A3E635"/></linearGradient>
<clipPath id="scr"><rect x="27" y="41" width="202" height="116" rx="12"/></clipPath>
<clipPath id="beforeCut"><path d="M 27 41 L $xTop 41 L $xBot 157 L 27 157 Z"/></clipPath>
<clipPath id="afterCut"><path d="M $xTop 41 L 229 41 L 229 157 L $xBot 157 Z"/></clipPath>
</defs>
<rect x="16" y="30" width="224" height="138" rx="20" fill="#0B1220" stroke="url(#acc)" stroke-width="9"/>
<rect x="27" y="41" width="202" height="116" rx="12" fill="url(#glass)"/>
<g clip-path="url(#scr)">
  <g clip-path="url(#beforeCut)">
    <rect x="42" y="50" width="152" height="26" rx="4" fill="#3A4658"/>
    <rect x="42" y="80" width="96" height="7" rx="3.5" fill="#64748B" fill-opacity="0.75"/>
    <rect x="42" y="91" width="120" height="7" rx="3.5" fill="#64748B" fill-opacity="0.75"/>
    <rect x="42" y="104" width="152" height="22" rx="4" fill="#3A4658"/>
    <rect x="42" y="130" width="82" height="7" rx="3.5" fill="#64748B" fill-opacity="0.75"/>
    <rect x="42" y="141" width="110" height="7" rx="3.5" fill="#64748B" fill-opacity="0.75"/>
  </g>
  <g clip-path="url(#afterCut)">
    <rect x="42" y="50" width="152" height="26" rx="4" fill="url(#media)"/>
    <rect x="42" y="80" width="96" height="7" rx="3.5" fill="url(#acc)"/>
    <rect x="42" y="91" width="120" height="7" rx="3.5" fill="url(#acc)"/>
    <rect x="42" y="104" width="152" height="22" rx="4" fill="url(#media2)"/>
    <rect x="42" y="130" width="82" height="7" rx="3.5" fill="url(#acc)"/>
    <rect x="42" y="141" width="110" height="7" rx="3.5" fill="url(#acc)"/>
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

# Ping-pong: sweep in, hold on the finished mark, sweep back out, hold on the
# blank state. Mirroring the frames makes the loop seamless in both directions,
# so there is no jump-cut back to the start.
$HOLD_END   = 8
$HOLD_START = 5
$seq = 0
$ordered = New-Object System.Collections.Generic.List[string]

# forward sweep
for ($i = 0; $i -lt $FRAMES; $i++) { $ordered.Add((Join-Path $work ("f{0:d3}.png" -f $i))) }
# hold on the composed mark
for ($h = 0; $h -lt $HOLD_END; $h++) { $ordered.Add((Join-Path $work ("f{0:d3}.png" -f ($FRAMES-1)))) }
# reverse sweep - skip the endpoints so neither hold frame is duplicated
for ($i = $FRAMES-2; $i -gt 0; $i--) { $ordered.Add((Join-Path $work ("f{0:d3}.png" -f $i))) }
# hold on the blank state before it starts again
for ($h = 0; $h -lt $HOLD_START; $h++) { $ordered.Add((Join-Path $work ("f{0:d3}.png" -f 0))) }

# Renumber into a contiguous sequence ffmpeg can glob
$seqDir = Join-Path $work "seq"
New-Item -ItemType Directory -Path $seqDir | Out-Null
foreach ($src in $ordered) {
    Copy-Item $src (Join-Path $seqDir ("s{0:d4}.png" -f $seq))
    $seq++
}
Write-Host "  $seq frames after ping-pong ($FRAMES forward + $HOLD_END hold + reverse + $HOLD_START hold)"

Write-Host "`nAssembling GIF..." -ForegroundColor Cyan
$pal = Join-Path $work "palette.png"
$pattern = Join-Path $seqDir "s%04d.png"
$gif = Join-Path $brand "PlayGif-animated.gif"

# Two-pass palette keeps the blue gradient from banding.
# stats_mode=full (not diff) is required for a transparent GIF - diff mode
# optimizes against the previous frame and drops the reserved alpha entry,
# which renders the logo on an opaque black box.
& $ffmpeg -y -loglevel error -framerate 24 -i $pattern `
    -vf "palettegen=stats_mode=full:max_colors=192:reserve_transparent=1" $pal
& $ffmpeg -y -loglevel error -framerate 24 -i $pattern -i $pal `
    -lavfi "paletteuse=dither=bayer:bayer_scale=3:diff_mode=rectangle:alpha_threshold=128" `
    -gifflags -offsetting -loop 0 $gif

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
