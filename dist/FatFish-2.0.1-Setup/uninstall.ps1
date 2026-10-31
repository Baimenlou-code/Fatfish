<#
  FatFish 启动器 —— 卸载程序
  默认保留你的 .env、会话记录与插件；加 -Purge 才连数据一起删。
#>
param(
    [switch]$Purge,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$Dir = $PSScriptRoot

function Line($c = '=') { Write-Host ($c * 62) }
function Ok($t)   { Write-Host "  [OK] $t" -ForegroundColor Green }
function Warn($t) { Write-Host "  [!] $t"  -ForegroundColor Yellow }

Line '='
Write-Host '  FatFish 启动器 · 卸载程序'
Line '='
Write-Host ''
Write-Host "  安装目录：$Dir"
if ($Purge) {
    Warn '已选 -Purge：配置、会话记录、插件将一并删除。'
} else {
    Write-Host '  默认保留：.env、会话记录、插件（以后重装可直接接着用）'
}
Write-Host ''

if (-not $Quiet) {
    $ans = Read-Host '  确认卸载？(y/N)'
    if ($ans -notmatch '^[Yy]') { Write-Host '  已取消，什么都没动。'; exit 0 }
}

# 1. 结束正在运行的进程
$procs = @(Get-Process -Name 'FatFish' -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -and $_.Path.StartsWith($Dir, [StringComparison]::OrdinalIgnoreCase) } catch { $false } })
if ($procs) {
    $procs | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600
    Ok '已结束正在运行的 FatFish'
}

# 2. 快捷方式
$lnks = @(
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\FatFish.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'FatFish.lnk')
)
foreach ($l in $lnks) { if (Test-Path $l) { Remove-Item $l -Force; Ok "已删除快捷方式：$(Split-Path $l -Leaf)" } }

# 3. 注册表卸载项
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\FatFish'
if (Test-Path $key) { Remove-Item $key -Recurse -Force; Ok '已清除「应用和功能」里的登记' }

# 4. 文件
$keep = @()
if (-not $Purge) { $keep = @('.env', 'logs', 'plugins', '_backup', 'workspace', '.fatfish_tmp') }

$removed = 0
foreach ($item in Get-ChildItem $Dir -Force) {
    if ($keep -contains $item.Name -or $item.Name -eq 'uninstall.ps1' -or $item.Name -eq 'uninstall.cmd') { continue }
    try { Remove-Item $item.FullName -Recurse -Force; $removed++ } catch { Warn "删不掉：$($item.Name)（可能被占用）" }
}
Ok "已删除 $removed 项程序文件"

if ($keep.Count -gt 0) {
    Write-Host ''
    Write-Host "  保留下来的数据（要彻底清掉就手动删除）：$Dir"
    Write-Host "    $($keep -join '、')"
}

Write-Host ''
if ($Purge) {
    try { Remove-Item $Dir -Recurse -Force; Ok '安装目录已删除' } catch { Warn "目录没删干净，请手动删除：$Dir" }
}
Line '='
Ok '卸载完成'
