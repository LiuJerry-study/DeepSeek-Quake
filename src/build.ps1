# DeepSeek Quake —— 一键编译
#
# 用法（在项目根目录或任意位置）：
#   pwsh -File src\build.ps1
#
# 为什么要先退出小工具：exe 正在运行时被占用，csc 写不进去。

[CmdletBinding()]
param(
    [string]$Out = '',
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrEmpty($Out)) { $Out = Join-Path $root 'DeepSeekQuake.exe' }

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "找不到 csc.exe: $csc" }

$src = @(
    'DeepSeekQuake.cs', 'Native.cs', 'HotkeySpec.cs', 'HotkeyManager.cs',
    'BrowserHost.cs', 'SettingsForm.cs', 'DiagnosticsForm.cs', 'StatusForm.cs', 'SelfTest.cs'
) | ForEach-Object { Join-Path $PSScriptRoot $_ }

foreach ($f in $src) { if (-not (Test-Path $f)) { throw "缺少源文件: $f" } }

$running = @(Get-Process DeepSeekQuake -ErrorAction SilentlyContinue)
if ($running.Count -gt 0 -and -not $KeepRunning) {
    Write-Host "先退出正在运行的小工具（否则 exe 被占用写不进去）…" -ForegroundColor Yellow
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 900
}

$outDir = Split-Path $Out -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

$args = @(
    '/nologo', '/target:winexe', '/optimize+', '/platform:anycpu',
    "/out:$Out",
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Management.dll'
) + $src

Write-Host "编译 → $Out" -ForegroundColor Cyan
& $csc @args
if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }

$fi = Get-Item $Out
Write-Host ("编译成功: {0}  ({1:N0} 字节, {2})" -f $fi.Name, $fi.Length, $fi.LastWriteTime) -ForegroundColor Green

# 本目录可能带着 DSH/沙箱设置的 Low 完整性标签（(OI)(CI) 继承）。
# 从带 Low 标签的 exe 启动，Windows 会把进程也按低完整性创建，
# 小工具会因此拒绝运行（低完整性下热键不可能生效）。编译后显式放宽到 Medium。
#
# 注意：如果**目录**上带着 (OI)(CI) 的 Low 标签，只给单个文件设 Medium 往往不生效——
# 新文件会继续继承 Low。所以这里设完必须**复验**，不通过就明确报错，绝不静默放过
# （上一版就是静默失效，结果用户双击后只看到「检测到低完整性」的警告）。
try { & icacls $Out /setintegritylevel Medium | Out-Null }
catch { Write-Host "设置产物完整性标签失败（不影响编译）: $_" -ForegroundColor Yellow }

$label = (& icacls $Out 2>&1 | Out-String)
if ($label -match 'Mandatory Label\\Low') {
    Write-Host ""
    Write-Host "！！产物被打上了 Low 完整性标签，双击会弹「检测到低完整性」并拒绝运行。" -ForegroundColor Red
    Write-Host "   根因通常是**本目录**带着 Low 标签的强制继承，逐个文件改没用。" -ForegroundColor Red
    Write-Host "   修法（一条命令，在正常桌面会话里跑）：" -ForegroundColor Yellow
    Write-Host "     pwsh -NoProfile -File `"$PSScriptRoot\fix-integrity.ps1`"" -ForegroundColor Yellow
    Write-Host "   它会同时把标签修好并（加 -Swap）把新版换成正式 exe。" -ForegroundColor Yellow
    Write-Host ""
    exit 2
}
Write-Host "产物完整性标签: " -NoNewline
Write-Host (($label -split "`n" | Select-String 'Mandatory' | ForEach-Object { $_.ToString().Trim() }) -join ' / ') -ForegroundColor Green


Write-Host ""
Write-Host "跑内置自测…" -ForegroundColor Cyan
& $Out --selftest
$code = $LASTEXITCODE
if ($code -ne 0) { Write-Host "自测未全部通过（退出码 $code）" -ForegroundColor Red }
else { Write-Host "自测全部通过" -ForegroundColor Green }

exit $code
