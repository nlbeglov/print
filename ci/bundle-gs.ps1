# Скачивает Ghostscript (закреплённая версия + SHA256), кладёт gswin64c.exe с DLL в dist\pkg\gs\bin
# и проверяет, что он рисует tests\sample.eps правильного размера. Ошибки выводятся как аннотации GitHub.
$ErrorActionPreference = 'Stop'
$GS_URL = 'https://github.com/ArtifexSoftware/ghostpdl-downloads/releases/download/gs10080/gs10080w64.exe'
$GS_SHA256 = '52a91b8bf09298788d7a57b9206127026c23eacd75405f0a131e26dc381dce50'

function Fail($title, $text) {
  $msg = ($text | Out-String) -replace '%', '%25' -replace "`r?`n", '%0A'
  Write-Output "::error title=$title::$msg"
  exit 1
}

Invoke-WebRequest $GS_URL -OutFile gs-installer.exe
$h = (Get-FileHash gs-installer.exe -Algorithm SHA256).Hash.ToLower()
if ($h -ne $GS_SHA256) { Fail 'Ghostscript checksum' "got $h" }
7z x -y -ogs_ext gs-installer.exe | Out-Null

$root = 'dist\pkg\gs'
$bin = "$root\bin"
New-Item -ItemType Directory -Force $bin | Out-Null
Copy-Item gs_ext\bin\gswin64c.exe, gs_ext\bin\gsdll64.dll $bin
Copy-Item gs_ext\lib "$root\lib" -Recurse
Copy-Item gs_ext\Resource "$root\Resource" -Recurse
Copy-Item gs_ext\iccprofiles "$root\iccprofiles" -Recurse
Copy-Item gs_ext\doc\COPYING "$root\LICENSE-Ghostscript-AGPL.txt"
foreach ($d in 'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll') {
  if (Test-Path "$env:WINDIR\System32\$d") { Copy-Item "$env:WINDIR\System32\$d" $bin }
  else { Fail 'VC runtime' "$d not found in System32" }
}

# Smoke test
$png = Join-Path $PWD 'smoke.png'
$out = & "$bin\gswin64c.exe" -dSAFER -dBATCH -dNOPAUSE -dEPSCrop -sDEVICE=png16m -r600 "-sOutputFile=$png" tests\sample.eps 2>&1
$code = $LASTEXITCODE
if ($code -ne 0 -or -not (Test-Path $png)) {
  Fail 'Ghostscript smoke test' ("exit code $code`n" + ($out | Out-String) + "`nbin: " + ((Get-ChildItem $bin).Name -join ', '))
}
Add-Type -AssemblyName System.Drawing
$img = [System.Drawing.Image]::FromFile($png)
$w = $img.Width; $hgt = $img.Height
$img.Dispose()
Write-Output "smoke.png: $w x $hgt"
if ([math]::Abs($w - 1370) -gt 2 -or [math]::Abs($hgt - 945) -gt 2) { Fail 'Ghostscript smoke test' "unexpected size $w x $hgt (expected 1370 x 945)" }
Write-Output "Ghostscript OK"
