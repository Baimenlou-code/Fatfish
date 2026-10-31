<#
  FatFish 启动器 2.0.1 —— 安装程序
  不需要管理员权限：全部装在当前用户目录下。
#>
param(
    [string]$Dir = '',
    [switch]$Desktop,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$AppName   = 'FatFish'
$Version   = '2.0.1'
$Publisher = 'FatFish'
$Payload   = Join-Path $PSScriptRoot 'payload'
$ExeName   = 'FatFish.exe'

function Line($c = '=') { Write-Host ($c * 62) }
function Info($t) { Write-Host "  $t" }
function Ok($t)   { Write-Host "  [OK] $t" -ForegroundColor Green }
function Warn($t) { Write-Host "  [!] $t"  -ForegroundColor Yellow }
function Die($t)  { Write-Host ''; Write-Host "  [失败] $t" -ForegroundColor Red; exit 1 }

Line '='
Write-Host "  FatFish 启动器  v$Version  ·  安装程序"
Line '='
Write-Host ''

if (-not (Test-Path (Join-Path $Payload $ExeName))) { Die "安装包不完整：缺少 payload\$ExeName。请重新解压安装包后再运行。" }

if ([string]::IsNullOrWhiteSpace($Dir)) { $Dir = Join-Path $env:LOCALAPPDATA $AppName }
$Dir = [System.IO.Path]::GetFullPath($Dir)

# ---------- 1. 找一个 dotnet ----------
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
    try {
        $out = & $dotnetExe --list-runtimes 2>$null
        return [bool]($out -match 'Microsoft\.WindowsDesktop\.App\s+10\.')
    } catch { return $false }
}

Write-Host '  [1/6] 检查运行环境 …'
$dotnet = Find-Dotnet
$hasRuntime = Test-DesktopRuntime $dotnet

if (-not $hasRuntime) {
    Write-Host ''
    Warn '这台电脑上没有 .NET 10 桌面运行时（Microsoft.WindowsDesktop.App 10.x）。'
    Info '本程序是「框架依赖版」，必须先装运行时才能运行。'
    Info ''
    Info '请这样做：'
    Info '  1. 找到另一个包：FatFish-2.0.1-Deps-dotnet10'
    Info '  2. 解压后双击里面的 install-deps.cmd（需要联网，约 55 MB，不需要管理员权限）'
    Info '  3. 装完后重新双击本安装器的 install.cmd'
    Info ''
    Info '或者手动下载：https://dotnet.microsoft.com/download/dotnet/10.0'
    Info '  （选 “.NET Desktop Runtime 10.x” → Windows x64）'
    Write-Host ''
    $ans = Read-Host '  要现在打开下载页吗？(y/N)'
    if ($ans -match '^[Yy]') { Start-Process 'https://dotnet.microsoft.com/download/dotnet/10.0' }
    Write-Host ''
    Info '安装已中止，未改动你电脑上的任何文件。'
    exit 2
}
Ok "运行时已就绪（$dotnet）"

# ---------- 2. 复制程序文件 ----------
Write-Host ''
Write-Host '  [2/6] 复制程序文件 …'
if (Test-Path $Dir) {
    Warn "安装目录已存在，将更新其中的程序文件：$Dir"
} else {
    New-Item -ItemType Directory -Force -Path $Dir | Out-Null
    Ok "已创建安装目录：$Dir"
}

Copy-Item (Join-Path $Payload $ExeName) (Join-Path $Dir $ExeName) -Force
foreach ($extra in @('README.md')) {
    $src = Join-Path $Payload $extra
    if (Test-Path $src) { Copy-Item $src (Join-Path $Dir $extra) -Force }
}
Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') (Join-Path $Dir 'uninstall.ps1') -Force
Copy-Item (Join-Path $PSScriptRoot 'uninstall.cmd') (Join-Path $Dir 'uninstall.cmd') -Force
Ok '程序文件已就位'

# ---------- 3. 配置文件 ----------
Write-Host ''
Write-Host '  [3/6] 准备配置文件 …'
$envTarget = Join-Path $Dir '.env'
if (Test-Path $envTarget) {
    Ok '.env 已存在 —— 保留你原来的配置，不覆盖'
} else {
    Copy-Item (Join-Path $Payload '.env.example') $envTarget -Force
    Warn '已生成 .env 模板。装完后请打开它，把 FATFISH_API_KEY 换成你自己的密钥，否则程序启动会亮红灯。'
}

# ---------- 4. 快捷方式 ----------
Write-Host ''
Write-Host '  [4/6] 创建快捷方式 …'
$exePath = Join-Path $Dir $ExeName
function New-Shortcut([string]$lnk, [string]$target, [string]$workdir) {
    $shell = New-Object -ComObject WScript.Shell
    $s = $shell.CreateShortcut($lnk)
    $s.TargetPath = $target
    $s.WorkingDirectory = $workdir
    $s.Description = "FatFish 启动器 v$Version"
    $s.IconLocation = "$target,0"
    $s.Save()
}
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
New-Shortcut (Join-Path $startMenu 'FatFish.lnk') $exePath $Dir
Ok '开始菜单快捷方式：FatFish'
if ($Desktop) {
    New-Shortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) 'FatFish.lnk') $exePath $Dir
    Ok '桌面快捷方式：FatFish'
}

# ---------- 5. 注册卸载项 ----------
Write-Host ''
Write-Host '  [5/6] 登记到「应用和功能」…'
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\FatFish'
if (Test-Path $key) { Remove-Item $key -Recurse -Force }
New-Item -Path $key -Force | Out-Null
$sizeKb = [int]((Get-ChildItem $Dir -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum / 1KB)
$props = @{
    DisplayName     = "FatFish 启动器 $Version"
    DisplayVersion  = $Version
    Publisher       = $Publisher
    InstallLocation = $Dir
    DisplayIcon     = $exePath
    UninstallString = "powershell -NoProfile -ExecutionPolicy Bypass -File `"$Dir\uninstall.ps1`""
    EstimatedSize   = $sizeKb
    NoModify        = 1
    NoRepair        = 1
}
foreach ($k in $props.Keys) {
    New-ItemProperty -Path $key -Name $k -Value $props[$k] -Force | Out-Null
}
Ok '可以在「设置 → 应用」里卸载'

# ---------- 6. 完成 ----------
Write-Host ''
Write-Host '  [6/6] 完成'
Line '='
Ok "FatFish v$Version 已装到：$Dir"
Write-Host ''
Write-Host '  下一步：'
Write-Host "    1. 打开 $envTarget"
Write-Host '    2. 把 FATFISH_API_KEY=sk-your-key-here 换成你自己的密钥并保存'
Write-Host '    3. 从开始菜单启动 FatFish'
Write-Host ''
Write-Host '  配置文件、会话记录都留在安装目录里；卸载时默认不删。'
Line '='

if (-not $NoLaunch) {
    Write-Host ''
    $ans = Read-Host '  现在启动 FatFish 吗？(Y/n)'
    if ($ans -notmatch '^[Nn]') { Start-Process -FilePath $exePath -WorkingDirectory $Dir }
}
