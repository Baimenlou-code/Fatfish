# 发布版冒烟测试：验证「单文件自包含 exe」真能起来，而不是只有发布日志说成功。
#
# 与 gui_smoke.ps1 的区别（两者都要留）：
#   gui_smoke.ps1        → 跑 dotnet + Debug dll，验的是**源码**
#   publish_smoke.ps1    → 跑 Release 单文件 exe，验的是**发布产物**
#
# 为什么必须分开验：单文件发布有三处只在产物上才暴露的问题 ——
#   1. 首次运行要解包 native 库到临时目录（启动更慢，8 秒可能不够）；
#   2. 某些反射/资源路径在 ILMerged 单文件里会失效（Debug 下完全正常）；
#   3. 依赖文件是否真的被塞进 exe（漏了就是启动即崩，而 build 阶段毫无提示）。
#
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File publish_smoke.ps1

$ErrorActionPreference = 'Stop'

$exe = 'src\FatFish.App\bin\Release\net10.0-windows\win-x64\publish\FatFish.exe'

if (-not (Test-Path $exe)) { Write-Output "NO_EXE (先跑 build.cmd publish)"; exit 1 }

$size = (Get-Item $exe).Length
Write-Output ("EXE_SIZE_MB=" + [math]::Round($size / 1MB, 1))

$env:FATFISH_WORKSPACE = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

$p = Start-Process -FilePath $exe -PassThru
Write-Output ("STARTED pid=" + $p.Id)

# 12 秒：比 Debug 版多留 4 秒，给单文件解包用
Start-Sleep -Seconds 12

if ($p.HasExited) {
    Write-Output ("EXITED_BEFORE_CLOSE code=" + $p.ExitCode)
    exit 2
}

Write-Output "ALIVE_AFTER_12S"

$closed = $p.CloseMainWindow()
Write-Output ("CloseMainWindow=" + $closed)

if (-not $p.WaitForExit(15000)) {
    Write-Output "NOT_CLOSED_IN_15S killing"
    Stop-Process -Id $p.Id -Force
    exit 3
}

Write-Output ("EXIT_CODE=" + $p.ExitCode)

# 关闭时写出窗口位置 —— 这条同时证明 WPF 生命周期真的走完了，
# 而不是「窗口出现了但什么也没初始化」。
$json = Join-Path $env:FATFISH_WORKSPACE 'logs\ui\window.json'
if (Test-Path $json) {
    Write-Output "WINDOW_JSON_OK"
    Get-Content $json | Write-Output
} else {
    Write-Output "WINDOW_JSON_MISSING"
}
