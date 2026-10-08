# src\fix-integrity.ps1 —— 修掉「低完整性」标签，并把修复版换成正式版
#
# 为什么需要这个脚本：
#   本目录（D:\Desktop\DeepSeek Quake）上被打了 **Low 完整性标签，而且是 (OI)(CI) 强制继承**。
#   于是这个目录里**新建的任何文件都自动是 Low**，而 Windows 从带 Low 标签的程序启动时，
#   会把进程也按低完整性创建 —— 小工具会直接拒绝运行并弹「检测到低完整性」的警告。
#
#   更坑的是：`icacls <文件> /setintegritylevel Medium` 在这种目录里**看起来执行了但会被继承覆盖**，
#   所以必须在**目录**上一次性修掉，而不是逐个文件修。
#
# 用法（在正常桌面会话里跑，不要从沙箱/自动化工具里跑）：
#   pwsh -NoProfile -File "D:\Desktop\DeepSeek Quake\src\fix-integrity.ps1"
#   pwsh -NoProfile -File "D:\Desktop\DeepSeek Quake\src\fix-integrity.ps1" -Swap   # 顺便换上新版 exe

[CmdletBinding()]
param(
    # 把 DeepSeekQuake-fixed.exe 换成正式的 DeepSeekQuake.exe
    [switch]$Swap
)

$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

function Get-Integrity {
    param([string]$Path)
    $out = & icacls $Path 2>&1 | Out-String
    if ($out -match 'Mandatory Label\\(\w+)') { return $Matches[1] }
    return '(无)'
}

Write-Host "==== 1) 当前标签 ====" -ForegroundColor Cyan
Write-Host ("  目录 {0}  →  {1}" -f $root, (Get-Integrity $root))
foreach ($f in 'DeepSeekQuake.exe', 'DeepSeekQuake-fixed.exe') {
    $p = Join-Path $root $f
    if (Test-Path $p) { Write-Host ("  {0}  →  {1}" -f $f, (Get-Integrity $p)) }
}
Write-Host ""

Write-Host "==== 2) 修目录标签：Low → Medium（去掉强制继承）====" -ForegroundColor Cyan
# /setintegritylevel 会重写强制标签 ACE；带 (OI)(CI) 时子对象也会跟着改成 Medium。
& icacls $root /setintegritylevel Medium
$rc = $LASTEXITCODE
Write-Host "  icacls 退出码 = $rc"
if ($rc -ne 0) {
    Write-Host "  目录标签没改成功。请改用「以管理员身份运行」的 PowerShell 再跑一次本脚本。" -ForegroundColor Red
    Write-Host "  （一般不需要管理员：你是这个目录的所有者，通常有 WRITE_DAC 权限；" -ForegroundColor DarkGray
    Write-Host "    但如果目录被设成了别的所有者，就必须管理员。）" -ForegroundColor DarkGray
}
Write-Host ""

Write-Host "==== 3) 逐个文件兜底（有些文件可能带着不是继承来的旧标签）====" -ForegroundColor Cyan
$targets = @()
$targets += Get-ChildItem $root -File -Filter '*.exe' -ErrorAction SilentlyContinue
$targets += Get-ChildItem (Join-Path $root 'src') -File -ErrorAction SilentlyContinue
$targets += Get-ChildItem (Join-Path $root 'repro') -File -ErrorAction SilentlyContinue
$fixed = 0
foreach ($f in $targets) {
    if ((Get-Integrity $f.FullName) -eq 'Low') {
        & icacls $f.FullName /setintegritylevel Medium | Out-Null
        $now = Get-Integrity $f.FullName
        if ($now -ne 'Low') { $fixed++; Write-Host ("  已修: {0} → {1}" -f $f.Name, $now) -ForegroundColor Green }
        else { Write-Host ("  仍是 Low: {0}" -f $f.FullName) -ForegroundColor Yellow }
    }
}
Write-Host "  共修 $fixed 个文件"
Write-Host ""

Write-Host "==== 4) 结果复验 ====" -ForegroundColor Cyan
$dirLabel = Get-Integrity $root
Write-Host ("  目录  →  {0}" -f $dirLabel)
foreach ($f in 'DeepSeekQuake.exe', 'DeepSeekQuake-fixed.exe') {
    $p = Join-Path $root $f
    if (Test-Path $p) { Write-Host ("  {0}  →  {1}" -f $f, (Get-Integrity $p)) }
}
Write-Host ""

if ($dirLabel -eq 'Low') {
    Write-Host "目录仍是 Low：请用管理员 PowerShell 重跑本脚本，再继续。" -ForegroundColor Red
    exit 1
}

if ($Swap) {
    Write-Host "==== 5) 换上新版 exe ====" -ForegroundColor Cyan
    $fresh = Join-Path $root 'DeepSeekQuake-fixed.exe'
    $prod  = Join-Path $root 'DeepSeekQuake.exe'
    if (-not (Test-Path $fresh)) { Write-Host "找不到 $fresh" -ForegroundColor Red; exit 1 }

    $running = @(Get-Process DeepSeekQuake -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        Write-Host "  先退出正在运行的小工具（否则 exe 被占用）…" -ForegroundColor Yellow
        $running | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 1200
    }
    try {
        if (Test-Path $prod) { Remove-Item $prod -Force -ErrorAction Stop }
        Copy-Item $fresh $prod -Force -ErrorAction Stop
        & icacls $prod /setintegritylevel Medium | Out-Null
        Write-Host ("  已替换: {0}  标签={1}" -f $prod, (Get-Integrity $prod)) -ForegroundColor Green
    } catch {
        Write-Host "  替换失败: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "  可以手动做：删掉 DeepSeekQuake.exe，再把 DeepSeekQuake-fixed.exe 改名成 DeepSeekQuake.exe" -ForegroundColor Yellow
        exit 1
    }
    Write-Host ""
    Write-Host "  现在双击 DeepSeek Quake.lnk 正常启动即可。" -ForegroundColor Green
}

Write-Host ""
Write-Host "完成。" -ForegroundColor Green
exit 0
