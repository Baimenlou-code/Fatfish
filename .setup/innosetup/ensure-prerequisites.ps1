# ensure-prerequisites.ps1 -- probe FatFish's prerequisites, and repair what can be repaired.
#
# Two callers:
#   1. The wizard's "environment check" page (right after the user picks a folder):
#        -ReportFile <path>            -> probe only, no downloads
#        -ReportFile <path> -Repair    -> probe and download the missing runtime
#      The report is read back by fatfish.iss and shown inside the wizard.
#   2. PrepareToInstall, but ONLY in silent mode (there are no pages to click):
#        -ReportFile <path> -Repair
#
# Exit codes:
#   0 = every check passed (already satisfied, or repaired)
#   2 = something is missing that cannot be repaired
#
# Report format (deliberately ASCII, one record per line, '|'-separated):
#   OK|Windows|build 26200
#   FAIL|Disk space|only 120 MB free ...
#   SUMMARY|5|1|1            (ok|repaired|failed)
# ASCII on purpose: Inno's LoadStringsFromFile reads ANSI, so a UTF-8 report
# would turn into mojibake inside the wizard. The Chinese wording lives in the
# .iss, not here.
#
# Design rule learned the hard way: never `exit` from inside a try block, and
# never build a message with `(...) + 'text'` inside an argument list. Either
# mistake raises inside the try, the catch swallows it, and the installer sails
# on as if the checks had passed. So: every check only records a result, and a
# single gate at the bottom decides.
#
# What FatFish actually requires (verified against the repo, not guessed):
#   * Windows x64, build 14393 (Win10 1607) or newer -- the floor for .NET 10.
#   * .NET 10 DESKTOP runtime (Microsoft.WindowsDesktop.App 10.x). The app is
#     net10.0-windows (WPF) and published framework-dependent, so this is the
#     one heavy prerequisite. Downloaded when missing; ~75 MB in two packages,
#     no admin rights, no system PATH changes.
#   * Free disk space (~150 MB unpacked runtime + payload).
#   * A writable install directory.
#   There are no NuGet packages (the project uses only SDK built-ins) and no
#   WebView2 dependency -- checked, so no checks are invented for them.
#
# WHY TWO RUNTIME PACKAGES (measured 2026-11-20):
#   dotnet-runtime-*.zip          ~37 MB -> dotnet.exe, host\fxr, shared\Microsoft.NETCore.App
#   windowsdesktop-runtime-*.zip  ~39 MB -> only shared\Microsoft.WindowsDesktop.App
#   Expanding just the desktop zip leaves no dotnet.exe at all.
#
# WHY NOT dotnet-install.ps1 AS THE PRIMARY PATH: on this machine
# Invoke-WebRequest hangs forever against the CDN while curl fetches the same
# URL in seconds. curl.exe ships with Win10 1803+, so try it first, then
# WebClient, and only then give up.
#
# ASCII-only output on purpose: Windows PowerShell 5.1 misreads non-BOM UTF-8
# scripts, and this runs on machines in unknown locales.

param(
    [string]$TargetDir  = '',
    [string]$ReportFile = '',
    [switch]$Repair
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$MinBuild   = 14393      # Win10 1607
$NeedFreeMb = 400
$RuntimeDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'

# Test hook: lets the build prove the "cannot repair" path without a broken VM.
if ($env:FATFISH_SETUP_MIN_BUILD) { $MinBuild = [int]$env:FATFISH_SETUP_MIN_BUILD }

$Script:Results = New-Object System.Collections.ArrayList

function Add-Result {
    param([string]$Name, [string]$State, [string]$Detail)
    [void]$Script:Results.Add([pscustomobject]@{ Name = $Name; State = $State; Detail = $Detail })
    $tag = switch ($State) {
        'OK'    { '[OK]  ' }
        'FIXED' { '[FIX] ' }
        'SKIP'  { '[--]  ' }
        default { '[FAIL]' }
    }
    Write-Host ("  {0} {1} {2}" -f $tag, $Name.PadRight(26), $Detail)
}

function Write-Report {
    if (-not $ReportFile) { return }
    $lines = @()
    foreach ($r in $Script:Results) {
        $lines += ("{0}|{1}|{2}" -f $r.State, $r.Name, $r.Detail)
    }
    $ok    = @($Script:Results | Where-Object { $_.State -eq 'OK' }).Count
    $fixed = @($Script:Results | Where-Object { $_.State -eq 'FIXED' }).Count
    $fail  = @($Script:Results | Where-Object { $_.State -eq 'FAIL' }).Count
    $lines += ("SUMMARY|{0}|{1}|{2}" -f $ok, $fixed, $fail)
    try {
        Set-Content -Path $ReportFile -Value $lines -Encoding ASCII -ErrorAction Stop
    } catch {
        Write-Host '  [!] could not write the report file'
    }
}

Write-Host ''
Write-Host '  Checking prerequisites ...'

# ---------------------------------------------------------------------------
# 1. Windows build
# ---------------------------------------------------------------------------
$build = 0
try { $build = [Environment]::OSVersion.Version.Build } catch { $build = 0 }

if ($build -eq 0) {
    Add-Result 'Windows' 'FAIL' 'could not determine the OS version'
} elseif ($build -ge $MinBuild) {
    Add-Result 'Windows' 'OK' ("build {0}" -f $build)
} else {
    $detail = "build {0} is older than {1}; FatFish needs Windows 10 1607 or newer" -f $build, $MinBuild
    Add-Result 'Windows' 'FAIL' $detail
}

$osUsable = $build -ge $MinBuild

# ---------------------------------------------------------------------------
# 2. 64-bit
# ---------------------------------------------------------------------------
$is64 = $false
try { $is64 = [Environment]::Is64BitOperatingSystem } catch { $is64 = $false }

if ($is64) {
    Add-Result 'Architecture' 'OK' 'x64'
} else {
    Add-Result 'Architecture' 'FAIL' 'FatFish is x64 only; this is 32-bit Windows'
    $osUsable = $false
}

# ---------------------------------------------------------------------------
# 3. PowerShell
# ---------------------------------------------------------------------------
$psv = $PSVersionTable.PSVersion
if ($psv.Major -ge 5) {
    Add-Result 'PowerShell' 'OK' ("{0}" -f $psv)
} else {
    Add-Result 'PowerShell' 'FAIL' ("{0} is too old; 5.1 is required" -f $psv)
}

# ---------------------------------------------------------------------------
# 4. .NET 10 desktop runtime -- detect; download only when -Repair is given
# ---------------------------------------------------------------------------
function Find-Dotnet {
    $cands = @(
        (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'),
        'C:\Program Files\dotnet\dotnet.exe',
        'C:\Program Files (x86)\dotnet\dotnet.exe'
    )
    foreach ($c in $cands) { if (Test-Path $c) { return $c } }
    $cmd = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Test-DesktopRuntime($dotnetExe) {
    if (-not $dotnetExe) { return $false }
    if (-not (Test-Path $dotnetExe)) { return $false }
    try {
        $out = & $dotnetExe --list-runtimes 2>$null
        return [bool]($out -match 'Microsoft\.WindowsDesktop\.App\s+10\.')
    } catch { return $false }
}

function Get-FileWithCurl {
    param([string]$Url, [string]$Dest)
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if (-not $curl) { return $false }
    # curl writes progress to stderr; with ErrorActionPreference=Stop that is
    # treated as NativeCommandError and kills the script (this actually happened).
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $curl.Source -sS -L --fail --retry 2 --connect-timeout 20 -o $Dest $Url 2>$null
        if ($LASTEXITCODE -ne 0) { return $false }
    } catch { return $false }
    finally { $ErrorActionPreference = $old }
    return ((Test-Path $Dest) -and ((Get-Item $Dest).Length -gt 10MB))
}

function Get-FileWithWebClient {
    param([string]$Url, [string]$Dest)
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        (New-Object Net.WebClient).DownloadFile($Url, $Dest)
        return ((Test-Path $Dest) -and ((Get-Item $Dest).Length -gt 10MB))
    } catch { return $false }
}

if (-not $osUsable) {
    Add-Result '.NET 10 desktop runtime' 'SKIP' 'skipped: this system cannot run it anyway'
} else {
    $dotnet = Find-Dotnet
    if (Test-DesktopRuntime $dotnet) {
        Add-Result '.NET 10 desktop runtime' 'OK' $dotnet
    } elseif (-not $Repair) {
        Add-Result '.NET 10 desktop runtime' 'FAIL' 'missing (Repair will download ~75 MB)'
    } else {
        Write-Host '  [!] .NET 10 desktop runtime is missing; downloading now.'
        Write-Host '      ~75 MB from Microsoft. No admin rights, no system PATH change.'
        Write-Host ("      Target: {0}" -f $RuntimeDir)

        $packages = @(
            @{ Name = 'dotnet-runtime'; Url = 'https://aka.ms/dotnet/10.0/dotnet-runtime-win-x64.zip' },
            @{ Name = 'windowsdesktop'; Url = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.zip' }
        )

        foreach ($pkg in $packages) {
            Write-Host ("      downloading {0} ..." -f $pkg.Name)
            $tmpZip = Join-Path $env:TEMP ('fatfish-' + $pkg.Name + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8) + '.zip')

            $ok = Get-FileWithCurl $pkg.Url $tmpZip
            if (-not $ok) {
                Write-Host '      curl failed, retrying with .NET WebClient ...'
                $ok = Get-FileWithWebClient $pkg.Url $tmpZip
            }
            if (-not $ok) { break }

            Write-Host ("      expanding {0} ..." -f $pkg.Name)
            New-Item -ItemType Directory -Force -Path $RuntimeDir | Out-Null
            Expand-Archive -Path $tmpZip -DestinationPath $RuntimeDir -Force
            Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue
        }

        # Trust only the probe, never an exit code.
        $dotnet = Join-Path $RuntimeDir 'dotnet.exe'
        if (-not (Test-Path $dotnet)) { $dotnet = Find-Dotnet }
        if (Test-DesktopRuntime $dotnet) {
            Add-Result '.NET 10 desktop runtime' 'FIXED' ("installed to {0}" -f $RuntimeDir)
        } else {
            Add-Result '.NET 10 desktop runtime' 'FAIL' 'download/install did not succeed'
        }
    }
}

# ---------------------------------------------------------------------------
# 5. Disk space + writability of the install directory
# ---------------------------------------------------------------------------
$probe = $TargetDir
try {
    while ($probe -and -not (Test-Path $probe)) { $probe = Split-Path -Parent $probe }
    if (-not $probe) { $probe = $env:LOCALAPPDATA }
} catch { $probe = $env:LOCALAPPDATA }

try {
    $drive = (Get-Item $probe).PSDrive
    $freeMb = [math]::Round($drive.Free / 1MB, 0)
    if ($freeMb -ge $NeedFreeMb) {
        Add-Result 'Disk space' 'OK' ("{0} MB free on {1}:" -f $freeMb, $drive.Name)
    } else {
        $detail = "only {0} MB free on {1}:, need {2} MB" -f $freeMb, $drive.Name, $NeedFreeMb
        Add-Result 'Disk space' 'FAIL' $detail
    }
} catch {
    Add-Result 'Disk space' 'FAIL' 'could not read free space'
}

$writable = $false
try {
    $testFile = Join-Path $probe ('.fatfish-write-test-' + [Guid]::NewGuid().ToString('N').Substring(0, 6))
    Set-Content -Path $testFile -Value 'x' -ErrorAction Stop
    Remove-Item $testFile -Force -ErrorAction SilentlyContinue
    $writable = $true
} catch { $writable = $false }

if ($writable) {
    Add-Result 'Write access' 'OK' $probe
} else {
    Add-Result 'Write access' 'FAIL' ("cannot write to {0}" -f $probe)
}

# ---------------------------------------------------------------------------
# Single gate: any FAIL means "not ready". The caller decides what to do --
# the wizard blocks Next, PrepareToInstall (silent only) aborts the install.
# ---------------------------------------------------------------------------
Write-Report

$failed = @($Script:Results | Where-Object { $_.State -eq 'FAIL' })
Write-Host ''
if ($failed.Count -gt 0) {
    Write-Host ("  Not ready: {0} check(s) failed." -f $failed.Count)
    foreach ($f in $failed) { Write-Host ("    - {0}: {1}" -f $f.Name, $f.Detail) }
    Write-Host ''
    Write-Host '  .NET 10 desktop runtime (pick "Desktop Runtime", Windows x64):'
    Write-Host '    https://dotnet.microsoft.com/download/dotnet/10.0'
    exit 2
}

Write-Host '  Ready.'
exit 0
