<#
  FatFish 启动器 2.1.1 —— 安装程序（一键版）

  ★ 2026-11-20 起：不再需要先装第二个「依赖包」。
    本脚本自己检查 .NET 10 桌面运行时；缺了就当场下载安装
    （约 39 MB 压缩包 / 装完约 150 MB，装到 %LOCALAPPDATA%\Microsoft\dotnet，
      不需要管理员权限、不改系统 PATH、不影响其它 .NET 程序）。

  ★ 为什么下载不用 dotnet-install.ps1 的默认路径：
    实测在部分网络下，PowerShell 5.1 的 Invoke-WebRequest 抓这个 CDN 会卡在 0 字节
    （HEAD 请求 1.8 秒就回 200，GET 却几十分钟不动；同样的 URL 用 curl 十几秒能拿到）。
    所以这里优先用 curl.exe，其次 .NET WebClient，两条都不行才退回官方脚本。
    dotnet-install.ps1 仍然随包提供，既用来取下载地址，也作为最后的兜底。

  整个安装过程不需要管理员权限：程序本身也装在当前用户目录下。
#>
param(
    [string]$Dir = '',
    [switch]$Desktop,
    [switch]$NoLaunch,
    # 下面三个只用于自动化验证，普通用户不会用到
    [switch]$Yes,
    [switch]$AssumeNoRuntime,
    [string]$RuntimeDir = ''
)

$ErrorActionPreference = 'Stop'
# 进度条在输出被重定向时会拖慢下载，甚至看起来像卡死。整个脚本关掉。
$ProgressPreference = 'SilentlyContinue'

$AppName   = 'FatFish'
$Version   = '2.1.1'
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

if ([string]::IsNullOrWhiteSpace($RuntimeDir)) { $RuntimeDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet' }
$RuntimeDir = [System.IO.Path]::GetFullPath($RuntimeDir)

# ---------- 运行时探测 ----------
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

# 拿「该下哪个 zip」。
#
# ★ 为什么不解析 dotnet-install.ps1 -DryRun 的输出：
#   它用 Write-Host 打印，而 Write-Host 在 PS 5.1 里绕过所有流（2>&1 也抓不到），
#   想抓只能重定向整个进程，代价远大于收益。
# ★ 为什么用 aka.ms：
#   实测它 302 到当前 10.0 的最新补丁（今天解析到 10.0.15），curl -L 与 WebClient
#   都会自动跟随重定向；比「先去读 productVersion.txt」稳（那条路径实测是 BlobNotFound）。
function Get-RuntimeZipUrl {
    return 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.zip'
}

# 1) curl.exe：绝大多数 Win10 1803+ 自带，速度最好
function Get-FileWithCurl {
    param([string]$Url, [string]$Dest)

    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if (-not $curl) { return $false }

    # ★ curl 会把进度表写到 stderr。本脚本 $ErrorActionPreference='Stop'，
    #   原生程序写 stderr 会被 PowerShell 当成 NativeCommandError 直接终止脚本
    #   （第一版就这么挂的：临时 zip 根本没创建）。所以这里：
    #     · -sS：关掉进度表，只在真出错时输出
    #     · 局部把 ErrorActionPreference 降为 Continue
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $curl.Source -sS -L --fail --retry 2 --connect-timeout 20 -o $Dest $Url 2>$null
        if ($LASTEXITCODE -ne 0) { return $false }
    } catch { return $false }
    finally { $ErrorActionPreference = $old }

    return ((Test-Path $Dest) -and ((Get-Item $Dest).Length -gt 10MB))
}

# 2) .NET WebClient：没有 curl 时用，比 Invoke-WebRequest 稳定
function Get-FileWithWebClient {
    param([string]$Url, [string]$Dest)
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        (New-Object Net.WebClient).DownloadFile($Url, $Dest)
        return ((Test-Path $Dest) -and ((Get-Item $Dest).Length -gt 10MB))
    } catch { return $false }
}

# ---------- 1/6 检查运行环境（缺就当场装） ----------
Write-Host '  [1/6] 检查运行环境 …'
$dotnet = Find-Dotnet
$hasRuntime = Test-DesktopRuntime $dotnet
if ($AssumeNoRuntime) { $hasRuntime = $false }   # 仅用于验证「缺运行时装得对不对」

if ($hasRuntime) {
    Ok "运行时已就绪（$dotnet）"
}
else {
    Write-Host ''
    Warn '这台电脑上没有 .NET 10 桌面运行时（Microsoft.WindowsDesktop.App 10.x）。'
    Info '本程序是「框架依赖版」：体积小，但要靠这个运行时运行。'
    Info ''
    Info '现在会自动下载并安装它：'
    Info '  · 来源：微软官方 CDN（builds.dotnet.microsoft.com）'
    Info '  · 大小：约 39 MB 压缩包'
    Info "  · 位置：$RuntimeDir"
    Info '  · 不需要管理员权限，不改系统 PATH，不影响其它 .NET 程序'
    Info '  · 需要联网'
    Write-Host ''

    $depsScript = Join-Path $PSScriptRoot 'deps\dotnet-install.ps1'

    if (-not (Test-Path $depsScript)) {
        Write-Host ''
        Warn '安装包不完整：缺少 deps\dotnet-install.ps1，无法自动下载运行时。'
        Info '你可以手动装：https://dotnet.microsoft.com/download/dotnet/10.0'
        Info '  （选 “.NET Desktop Runtime 10.x” → Windows x64）'
        Write-Host ''
        Info '安装已中止，未改动你电脑上的任何文件。'
        exit 2
    }

    if (-not $Yes) {
        $ans = Read-Host '  开始下载并安装？(Y/n)'
        if ($ans -match '^[Nn]') {
            Write-Host ''
            Info '已取消。运行时没装的话程序启动会失败。'
            Info '稍后可以重新双击 install.cmd，或手动装：https://dotnet.microsoft.com/download/dotnet/10.0'
            exit 2
        }
    }

    Write-Host ''
    Info '正在下载运行时 …（约 39 MB，网速慢时要几分钟，别关窗口）'

    $zipUrl = Get-RuntimeZipUrl $depsScript
    $tmpZip = Join-Path $env:TEMP ('fatfish-runtime-' + [Guid]::NewGuid().ToString('N').Substring(0, 8) + '.zip')
    $downloaded = $false

    Info "  地址：$zipUrl"
    $downloaded = Get-FileWithCurl $zipUrl $tmpZip
    if (-not $downloaded) {
        Info '  curl 没成功，换 .NET 下载器再试 …'
        $downloaded = Get-FileWithWebClient $zipUrl $tmpZip
    }

    if ($downloaded) {
        Info '下载完成，正在解压 …'
        New-Item -ItemType Directory -Force -Path $RuntimeDir | Out-Null
        Expand-Archive -Path $tmpZip -DestinationPath $RuntimeDir -Force
        Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue
    }
    else {
        Warn '直接下载没成功，改用微软官方脚本再试一次 …'
        Write-Host ''
        & $depsScript -Channel '10.0' -Runtime 'windowsdesktop' -InstallDir $RuntimeDir -NoPath
        Write-Host ''
    }

    # 重新探测：以「能不能查到运行时」为准，不看任何退出码
    $dotnet = Join-Path $RuntimeDir 'dotnet.exe'
    if (-not (Test-Path $dotnet)) { $dotnet = Find-Dotnet }
    $hasRuntime = Test-DesktopRuntime $dotnet

    if (-not $hasRuntime) {
        Write-Host ''
        Warn '运行时没有装成功。'
        Info '常见原因：网络被墙 / 代理拦截 / 公司电脑限制访问 builds.dotnet.microsoft.com'
        Info ''
        Info '手动装（任选一种），然后再双击 install.cmd：'
        Info '  · https://dotnet.microsoft.com/download/dotnet/10.0'
        Info '    选 “.NET Desktop Runtime 10.x” → Windows x64'
        if ($zipUrl) { Info "  · 或直接下这个 zip 解压到 $RuntimeDir" ; Info "    $zipUrl" }
        Info ''
        Info '安装已中止，程序本体尚未安装（运行时是它运行的前提）。'
        exit 2
    }

    Ok "运行时已装好：$dotnet"
}

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
foreach ($extra in @('README.md','LICENSE','NOTICE')) {
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
    $ans = 'Y'
    if (-not $Yes) { $ans = Read-Host '  现在启动 FatFish 吗？(Y/n)' }
    if ($ans -notmatch '^[Nn]') { Start-Process -FilePath $exePath -WorkingDirectory $Dir }
}
