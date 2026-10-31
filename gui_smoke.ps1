# GUI 冒烟测试：启动 WPF 主窗口 → 等它装配完 → 优雅关闭（等价于点右上角 X）
# 目的：验证 (1) XAML 能加载；(2) BootKernel/BootAgent 不抛异常；(3) 关闭时写出 window.json
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File _tmp_gui_smoke.ps1

$ErrorActionPreference = 'Stop'

$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
$dll = 'src\FatFish.App\bin\Debug\net10.0-windows\FatFish.dll'

if (-not (Test-Path $dotnet)) { Write-Output "NO_DOTNET"; exit 1 }
if (-not (Test-Path $dll))    { Write-Output "NO_DLL";    exit 1 }

$env:FATFISH_WORKSPACE = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

$p = Start-Process -FilePath $dotnet -ArgumentList $dll -PassThru
Write-Output ("STARTED pid=" + $p.Id)

Start-Sleep -Seconds 8

if ($p.HasExited) {
    Write-Output ("EXITED_BEFORE_CLOSE code=" + $p.ExitCode)
    exit 2
}

Write-Output "ALIVE_AFTER_8S"

# 优雅关闭：等价于用户点右上角 X，会走 OnClosed → SaveWindowPlacement
$closed = $p.CloseMainWindow()
Write-Output ("CloseMainWindow=" + $closed)

if (-not $p.WaitForExit(10000)) {
    Write-Output "NOT_CLOSED_IN_10S killing"
    Stop-Process -Id $p.Id -Force
    exit 3
}

Write-Output ("EXIT_CODE=" + $p.ExitCode)

$json = Join-Path $env:FATFISH_WORKSPACE 'logs\ui\window.json'
if (Test-Path $json) {
    Write-Output "WINDOW_JSON_OK"
    Get-Content $json | Write-Output
} else {
    Write-Output "WINDOW_JSON_MISSING"
}
