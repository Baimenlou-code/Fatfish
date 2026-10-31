<#
  FatFish 依赖包 —— 安装 .NET 10 桌面运行时
  装在当前用户目录下（%LOCALAPPDATA%\Microsoft\dotnet），不需要管理员权限。
#>
$ErrorActionPreference = 'Stop'

function Line($c = '=') { Write-Host ($c * 62) }
function Ok($t)   { Write-Host "  [OK] $t" -ForegroundColor Green }
function Warn($t) { Write-Host "  [!] $t"  -ForegroundColor Yellow }

$InstallDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
$Script     = Join-Path $PSScriptRoot 'dotnet-install.ps1'

Line '='
Write-Host '  FatFish 依赖包 —— .NET 10 桌面运行时'
Line '='
Write-Host ''

if (-not (Test-Path $Script)) {
    Write-Host '  [失败] 依赖包不完整：缺少 dotnet-install.ps1' -ForegroundColor Red
    exit 1
}

$dotnetExe = Join-Path $InstallDir 'dotnet.exe'
if (Test-Path $dotnetExe) {
    $runtimes = & $dotnetExe --list-runtimes 2>$null
    if ($runtimes -match 'Microsoft\.WindowsDesktop\.App\s+10\.') {
        Ok '这台电脑已经装好了 .NET 10 桌面运行时，不用再装。'
        Line '='
        exit 0
    }
}

Write-Host '  将要下载并安装：Microsoft.WindowsDesktop.App 10.x（约 55 MB）'
Write-Host "  安装位置：$InstallDir"
Write-Host '  需要联网。不需要管理员权限。不写系统目录、不改 PATH。'
Write-Host ''
$ans = Read-Host '  开始安装？(Y/n)'
if ($ans -match '^[Nn]') { Write-Host '  已取消。'; exit 0 }
Write-Host ''

& $Script -Channel '10.0' -Runtime 'windowsdesktop' -InstallDir $InstallDir -NoPath
if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Warn '下载或安装没有成功。可以手动装：'
    Write-Host '  https://dotnet.microsoft.com/download/dotnet/10.0  →  .NET Desktop Runtime 10.x (Windows x64)'
    exit 1
}

Write-Host ''
if (Test-Path $dotnetExe) {
    $runtimes = & $dotnetExe --list-runtimes 2>$null
    $hit = $runtimes | Where-Object { $_ -match 'Microsoft\.WindowsDesktop\.App\s+10\.' }
    if ($hit) { Ok "安装成功：$hit" } else { Warn '装完了但没查到运行时，请重开一个窗口再试。' }
}

Line '='
Write-Host '  下一步：回到 FatFish-2.0.1-Setup，双击 install.cmd 安装程序本体。'
Line '='
