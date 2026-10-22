# Run after a Release build:  powershell -ExecutionPolicy Bypass -File scripts/check_source_pick.ps1
# Exercises MediaCacheService source selection for Steam's WebM + MP4 pairs (issue #5).
# MP4 is preferred; an MP4 that turns out to be HEVC loses to the WebM.
$ErrorActionPreference = 'Stop'
$bin = 'C:\Projects\PlayGif\src\bin\Release\net4.6.2'
Add-Type -Path "$bin\Playnite.SDK.dll"
Add-Type -Path "$bin\HtmlAgilityPack.dll"
Add-Type -Path "$bin\PlayGif.dll"

$root = Join-Path $env:TEMP ("pgcheck_" + [Guid]::NewGuid().ToString('N'))
$settings = New-Object PlayGif.PlayGifSettings
$settings.AutoCacheMedia = $false
$svc = New-Object PlayGif.Services.MediaCacheService($settings, $root)

$fails = 0
function Check($name, $cond) {
  if ($cond) { Write-Host "  PASS  $name" } else { Write-Host "  FAIL  $name"; $script:fails++ }
}

$webm = 'https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/1/extras/a.webm?t=1'
$mp4  = 'https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/1/extras/a.mp4?t=1'
$html = "<video autoplay muted loop><source src=`"$webm`" type=`"video/webm; codecs=vp9`"><source src=`"$mp4`" type=`"video/mp4`"></video>"
$mp4Name  = [PlayGif.Services.MediaCacheService]::GetCacheFilename($mp4)
$webmName = [PlayGif.Services.MediaCacheService]::GetCacheFilename($webm)

# Each scenario gets its own game so cached files and codec lookups do not leak
function NewGame { $id = [Guid]::NewGuid(); New-Item -ItemType Directory -Force (Join-Path $root "Games\$id") | Out-Null; $id }
function GameFile($id, $name) { Join-Path $root "Games\$id\$name" }
# Minimal stand-ins: only the sample-entry fourcc matters to the sniff
function FakeMp4($path, $fourcc) { Set-Content $path "....ftypisom....moov....stsd....$fourcc...." }

# Codec unknown: both kept, MP4 first, so the shell can fall back on a decode error
$g = NewGame
$out = $svc.RewriteDescriptionHtml($html, $g)
Check 'uncached pair keeps both sources'        ($out.Contains('a.mp4') -and $out.Contains('a.webm'))
Check 'uncached pair tries MP4 first'           ($out.IndexOf('a.mp4') -lt $out.IndexOf('a.webm'))

# Cached H.264 MP4 wins, served locally; the WebM is dropped
$g = NewGame
FakeMp4 (GameFile $g $mp4Name) 'avc1'
$out = $svc.RewriteDescriptionHtml($html, $g)
Check 'cached H.264 MP4 kept'                   ($out.Contains($mp4Name))
Check 'WebM dropped next to H.264'              (-not $out.Contains('a.webm'))

# A WebM cached alongside it is reclaimed
Set-Content (GameFile $g $webmName) 'x'
$null = $svc.RewriteDescriptionHtml($html, $g)
Check 'stale WebM reclaimed next to H.264'      (-not (Test-Path (GameFile $g $webmName)))

# Cached HEVC MP4 loses to the WebM
$g = NewGame
FakeMp4 (GameFile $g $mp4Name) 'hvc1'
$out = $svc.RewriteDescriptionHtml($html, $g)
Check 'HEVC MP4 loses to WebM'                  ($out.Contains('a.webm') -and -not $out.Contains('a.mp4') -and -not $out.Contains($mp4Name))
Check 'HEVC MP4 kept until WebM is cached'      (Test-Path (GameFile $g $mp4Name))

Set-Content (GameFile $g $webmName) 'x'
$out = $svc.RewriteDescriptionHtml($html, $g)
Check 'HEVC MP4 reclaimed once WebM cached'     (-not (Test-Path (GameFile $g $mp4Name)))
Check 'cached WebM served from playgif.local'   ($out.Contains("playgif.local/$g/$webmName"))
$out = $svc.RewriteDescriptionHtml($html, $g)
Check 'reclaimed HEVC MP4 is not retried'       (-not $out.Contains('a.mp4'))

# With FFmpeg the WebM is converted, and the H.264 conversion is what plays
Set-Content (GameFile $g ([IO.Path]::ChangeExtension($webmName, '.mp4'))) 'x'
$out = $svc.RewriteDescriptionHtml($html, $g)
Check 'converted H.264 MP4 preferred'           ($out.Contains([IO.Path]::ChangeExtension($webmName, '.mp4')))

# hev1 is HEVC too
$g = NewGame
FakeMp4 (GameFile $g $mp4Name) 'hev1'
Check 'hev1 detected as HEVC'                   ($svc.RewriteDescriptionHtml($html, $g).Contains('a.webm'))

# A lone MP4 (user-added or non-Steam) is left alone
$lone = "<video><source src=`"https://example.com/b.mp4`" type=`"video/mp4`"></video>"
Check 'lone MP4 untouched'                      ($svc.RewriteDescriptionHtml($lone, (NewGame)).Contains('b.mp4'))

Remove-Item $root -Recurse -Force
if ($fails -gt 0) { Write-Host "`n$fails CHECK(S) FAILED"; exit 1 } else { Write-Host "`nAll checks passed"; exit 0 }
