#requires -Version 5.1
<#
  pack-setup.ps1 -- build the FatFish Windows installer

  Produces:
    dist\FatFish-<ver>-Setup.exe            Inno Setup installer (primary)
    dist\FatFish-<ver>-Setup-win-x64.zip    same folder zipped (portable fallback)

  Toolchain
  ---------
  Needs Inno Setup 6 (ISCC.exe). Install per-user without admin:
      winget install --id JRSoftware.InnoSetup -e --source winget --silent
  If ISCC is missing this script tells you exactly that instead of half-working.

  Why an installer at all (history)
  ---------------------------------
  v2.0.1 shipped a folder zipped up, and users were expected to unzip it and
  double-click install.cmd. People double-clicked it *inside* the archive,
  where payload\ does not exist, and got a cryptic failure. v2.1.1 adds a real
  installer: native Add/Remove entry, start menu entry, uninstaller, version
  info, and the app icon on the setup itself.

  Layout of the payload
  ---------------------
  The installer's content comes from dist\FatFish-<ver>-Setup\ (installer
  scripts + payload\). That folder is produced by the app publish step; this
  script does not rebuild FatFish.exe.

  Keep this file pure ASCII (see README "Why ASCII-only .cmd").
#>
param(
    [string]$Version  = '2.1.1',
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$SkipZip
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

$setupDirName = "FatFish-$Version-Setup"
$distDir      = Join-Path $RepoRoot 'dist'
$setupDir     = Join-Path $distDir $setupDirName
$payloadDir   = Join-Path $setupDir 'payload'
$issDir       = Join-Path $PSScriptRoot 'innosetup'
$issFile      = Join-Path $issDir 'fatfish.iss'
$outExe       = Join-Path $distDir "FatFish-$Version-Setup.exe"
$outZip       = Join-Path $distDir "FatFish-$Version-Setup-win-x64.zip"

function Step($t) { Write-Host ''; Write-Host "== $t" -ForegroundColor Cyan }
function Ok($t)   { Write-Host "   [OK] $t" -ForegroundColor Green }
function Die($t)  { Write-Host "   [FAIL] $t" -ForegroundColor Red; exit 1 }

Step "Check inputs"
if (-not (Test-Path $payloadDir)) { Die "missing payload folder: $payloadDir" }
if (-not (Test-Path (Join-Path $payloadDir 'FatFish.exe'))) { Die "missing payload\FatFish.exe" }
if (-not (Test-Path $issFile)) { Die "missing Inno script: $issFile" }
Ok "payload + script present"

$candidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
)
$iscc = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    Die @"
ISCC.exe (Inno Setup 6) not found. Install it per-user, no admin needed:
  winget install --id JRSoftware.InnoSetup -e --source winget --silent
"@
}
Ok "ISCC: $iscc"

# ---------------------------------------------------------------------------
# 1. zip (portable fallback asset, same content as the installer payload)
# ---------------------------------------------------------------------------
if (-not $SkipZip) {
    Step "Zip the installer folder"
    if (Test-Path $outZip) { Remove-Item $outZip -Force }
    Compress-Archive -Path (Join-Path $setupDir '*') -DestinationPath $outZip -CompressionLevel Optimal
    Ok ("{0}  {1:N1} KB" -f [IO.Path]::GetFileName($outZip), ((Get-Item $outZip).Length / 1KB))
}

# ---------------------------------------------------------------------------
# 2. compile the installer
# ---------------------------------------------------------------------------
Step "Compile installer (Inno Setup)"
# The .iss holds Chinese literals. Inno's own .isl files ship WITHOUT a BOM and
# ISCC copes, so a BOM is not strictly required -- but adding it removes any
# dependence on how the file was last saved, which is exactly the class of bug
# where a Chinese string silently becomes mojibake. Cheap insurance.
$bytes = [IO.File]::ReadAllBytes($issFile)
if (-not ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)) {
    $text = [Text.Encoding]::UTF8.GetString($bytes)
    [IO.File]::WriteAllText($issFile, $text, (New-Object Text.UTF8Encoding($true)))
    Ok "added UTF-8 BOM to fatfish.iss"
}

if (Test-Path $outExe) { Remove-Item $outExe -Force }
# Pass the FULL path: ISCC resolves a bare filename against the current working
# directory (repo root), not against $issDir, and fails with "file not found".
& $iscc "/DAppVersion=$Version" $issFile
if ($LASTEXITCODE -ne 0) { Die "ISCC failed with exit code $LASTEXITCODE" }
if (-not (Test-Path $outExe)) { Die "compiler reported success but no exe at $outExe" }
Ok ("{0}  {1:N1} KB" -f [IO.Path]::GetFileName($outExe), ((Get-Item $outExe).Length / 1KB))

Step "Done"
Write-Host "   exe: $outExe"
if (-not $SkipZip) { Write-Host "   zip: $outZip" }
exit 0
