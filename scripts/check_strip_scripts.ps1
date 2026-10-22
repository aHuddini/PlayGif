# Run after a Release build:  powershell -ExecutionPolicy Bypass -File scripts/check_strip_scripts.ps1
# Exercises DescriptionRendererService.StripScripts on hostile and ordinary description HTML.
$ErrorActionPreference = 'Stop'
$bin = 'C:\Projects\PlayGif\src\bin\Release\net4.6.2'
Add-Type -Path "$bin\Playnite.SDK.dll"
Add-Type -Path "$bin\Newtonsoft.Json.dll"
Add-Type -Path "$bin\HtmlAgilityPack.dll"
Add-Type -Path "$bin\Microsoft.Web.WebView2.Core.dll"
Add-Type -Path "$bin\Microsoft.Web.WebView2.Wpf.dll"
Add-Type -Path "$bin\PlayGif.dll"

$fails = 0
function Check($name, $cond) {
  if ($cond) { Write-Host "  PASS  $name" } else { Write-Host "  FAIL  $name"; $script:fails++ }
}
function Strip($html) { [PlayGif.Services.DescriptionRendererService]::StripScripts($html) }

$out = Strip '<p>hi</p><script>alert(1)</script>'
Check 'script element removed'          (-not $out.Contains('script') -and $out.Contains('<p>hi</p>'))

$out = Strip '<img src="x.gif" onerror="alert(1)" OnLoad="alert(2)">'
Check 'inline handlers removed'         (-not ($out -match 'on(error|load)') -and $out.Contains('src="x.gif"'))

$out = Strip '<a href=" java&#9;script:alert(1)">x</a><a href="JAVASCRIPT:alert(1)">y</a>'
Check 'javascript: hrefs removed'       (-not ($out -match 'href'))

$out = Strip "<a href=`"java`tscript:alert(1)`">x</a>"
Check 'tab-split scheme removed'        (-not ($out -match 'href'))

$out = Strip '<object data="x"></object><embed src="x"><base href="http://evil/"><meta http-equiv="refresh" content="0;url=http://evil">'
Check 'object/embed/base/meta removed'  (-not ($out -match '<(object|embed|base|meta)'))

$out = Strip '<iframe srcdoc="&lt;script nonce=&quot;x&quot;&gt;alert(1)&lt;/script&gt;" src="https://www.youtube.com/embed/x"></iframe>'
Check 'srcdoc removed, iframe src kept' (-not ($out -match 'srcdoc') -and $out.Contains('youtube.com/embed'))

$n1 = [PlayGif.Services.DescriptionRendererService]::WithFreshNonce('<script nonce="{{nonce}}">')
$n2 = [PlayGif.Services.DescriptionRendererService]::WithFreshNonce('<script nonce="{{nonce}}">')
Check 'nonce filled and fresh each load' (-not $n1.Contains('{{nonce}}') -and $n1 -ne $n2 -and $n1.Length -gt 30)

$steam ='<h2 class="bb_tag">About</h2><video class="bb_img" autoplay muted loop playsinline poster="https://a/p.avif"><source src="https://a/v.webm" type="video/webm"></video><a href="https://steamcommunity.com/linkfilter/?u=x" target="_blank" rel="noopener">link</a>'
$out = Strip $steam
Check 'ordinary Steam markup kept'      ($out.Contains('v.webm') -and $out.Contains('poster=') -and $out.Contains('target="_blank"') -and $out.Contains('linkfilter'))

Check 'empty passes through'            ((Strip '') -eq '')

if ($fails -gt 0) { Write-Host "`n$fails CHECK(S) FAILED"; exit 1 } else { Write-Host "`nAll checks passed"; exit 0 }
