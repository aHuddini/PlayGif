# Run after a Release build:  powershell -ExecutionPolicy Bypass -File scripts/check_description_baseline.ps1
# Exercises the shipped SteamDescriptionService baseline logic (issue #3).
$ErrorActionPreference = 'Stop'
$bin = 'C:\Projects\PlayGif\src\bin\Release\net4.6.2'
Add-Type -Path "$bin\Playnite.SDK.dll"
Add-Type -Path "$bin\Newtonsoft.Json.dll"
Add-Type -Path "$bin\PlayGif.dll"

$root = Join-Path $env:TEMP ("pgcheck_" + [Guid]::NewGuid().ToString('N'))
$svc  = New-Object PlayGif.Services.SteamDescriptionService($root, $null)

$game = New-Object Playnite.SDK.Models.Game
$game.Id = [Guid]::NewGuid()
$game.Name = 'Test Game'
$game.Description = '<p>Steam-ish original</p>'

$fails = 0
function Check($name, $cond) {
  if ($cond) { Write-Host "  PASS  $name" } else { Write-Host "  FAIL  $name"; $script:fails++ }
}

Check 'no cache -> not current' (-not $svc.IsCachedDescriptionCurrent($game))

$svc.SaveCachedDescription($game, '<p>RICH steam html</p>')
Check 'cache exists after save'              ($svc.HasCachedDescription($game.Id))
Check 'baseline written'                     (Test-Path (Join-Path $root "Games\$($game.Id)\_baseline.html"))
Check 'fresh cache -> current'               ($svc.IsCachedDescriptionCurrent($game))

# Another extension (or the user) rewrites the stored description
$game.Description = '<p>MY CUSTOM DESCRIPTION 12345</p>'
Check 'stored description changed -> superseded' (-not $svc.IsCachedDescriptionCurrent($game))
Check 'cache file still on disk (blocks auto-refetch)' ($svc.HasCachedDescription($game.Id))

# Explicit re-stamp (what an explicit fetch / edit / PlayGif-own write does)
$svc.SaveBaseline($game)
Check 're-stamped baseline -> current again'  ($svc.IsCachedDescriptionCurrent($game))

# Emptying the description overrules nothing
$game.Description = ''
Check 'empty stored description -> cache still applies' ($svc.IsCachedDescriptionCurrent($game))

# Legacy cache written before baselines existed
Remove-Item (Join-Path $root "Games\$($game.Id)\_baseline.html")
$game.Description = '<p>pre-existing text</p>'
Check 'legacy cache adopted'                  ($svc.IsCachedDescriptionCurrent($game))
Check 'legacy adoption stamps a baseline'     (Test-Path (Join-Path $root "Games\$($game.Id)\_baseline.html"))
$game.Description = '<p>changed after adoption</p>'
Check 'adopted baseline then detects change'  (-not $svc.IsCachedDescriptionCurrent($game))

# Reset clears both
$svc.ClearAllCachedDescriptions($game.Id)
Check 'reset removes cache'                   (-not $svc.HasCachedDescription($game.Id))
Check 'reset removes baseline'                (-not (Test-Path (Join-Path $root "Games\$($game.Id)\_baseline.html")))

Remove-Item $root -Recurse -Force
if ($fails -gt 0) { Write-Host "`n$fails CHECK(S) FAILED"; exit 1 } else { Write-Host "`nAll checks passed"; exit 0 }
