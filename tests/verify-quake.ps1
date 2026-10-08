<#
  DeepSeek Quake —— 一键验证脚本

  用法（小工具已经在运行时）：
    pwsh -File verify-quake.ps1
    pwsh -File verify-quake.ps1 -ToggleKey          # 跳过真实按键，只合成切换消息
    pwsh -File verify-quake.ps1 -SkipKeyTest        # 完全不碰键盘

  验证四件事：
    1. 小工具进程 + 目标浏览器窗口是否都锁定了
    2. 热键是否真的启用（组合键注册 / 双击 Ctrl 的键盘钩子）
    3. 切换链路：显示 ⇄ 收起 是否真的改变窗口可见性
    4. 真实按键：自动连按两下 Ctrl（或按你配置的组合键），看是否触发切换
    5. 窗口是否已从任务栏 / Alt+Tab 排除（WS_EX_TOOLWINDOW）

  全部通过退出码 0；有失败项退出码 1，并给出下一步该做什么。
#>
[CmdletBinding()]
param(
    [switch]$ToggleKey,
    [switch]$SkipKeyTest,
    [int]$WaitMs = 2000
)

$ErrorActionPreference = 'Stop'
$ok = 0; $bad = 0; $unknown = 0

function Pass($m) { $script:ok++; Write-Host ("  [通过] " + $m) -ForegroundColor Green }
function Fail($m) { $script:bad++; Write-Host ("  [失败] " + $m) -ForegroundColor Red }
function Warn($m) { $script:unknown++; Write-Host ("  [存疑] " + $m) -ForegroundColor Yellow }
function Head($m) { Write-Host ""; Write-Host $m -ForegroundColor Cyan }

Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public class QuakeCheck {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
  [DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll", EntryPoint="GetWindowLongW")] public static extern int GetWindowLong32(IntPtr h, int i);
  [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr64(IntPtr h, int i);
  [DllImport("user32.dll", SetLastError=true)] public static extern uint SendInput(uint n, INPUT[] inputs, int cb);

  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] public struct HARDWAREINPUT { public uint uMsg; public ushort l, h; }
  [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public HARDWAREINPUT hi; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion u; }

  public static string Text(IntPtr h) { var sb = new StringBuilder(512); GetWindowTextW(h, sb, sb.Capacity); return sb.ToString(); }
  public static string Cls(IntPtr h) { var sb = new StringBuilder(256); GetClassNameW(h, sb, sb.Capacity); return sb.ToString(); }
  public static long ExStyle(IntPtr h) {
    long v = IntPtr.Size == 8 ? GetWindowLongPtr64(h, -20).ToInt64() : (long)GetWindowLong32(h, -20);
    return v & 0xFFFFFFFFL;
  }
  public static bool HasOwner(IntPtr h) { return GetWindow(h, 4) != IntPtr.Zero; }

  public static IntPtr FindTraceWindow(string[] pids) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (Array.IndexOf(pids, pid.ToString()) < 0) return true;
      if (Text(h).StartsWith("DSQ v")) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }

  // 找属于指定进程名的 Chrome_WidgetWin_1 窗口（无 owner 的才算主窗口）
  public static IntPtr FindAppWindow(string[] pids) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (Array.IndexOf(pids, pid.ToString()) < 0) return true;
      if (Cls(h) != "Chrome_WidgetWin_1") return true;
      if (HasOwner(h)) return true;
      found = h; return false;
    }, IntPtr.Zero);
    return found;
  }

  public static void TapCtrl(int times, int gapMs) {
    for (int i = 0; i < times; i++) {
      INPUT[] down = new INPUT[1];
      down[0].type = 1; down[0].u.ki.wVk = 0x11;
      SendInput(1, down, Marshal.SizeOf(typeof(INPUT)));
      System.Threading.Thread.Sleep(40);
      INPUT[] up = new INPUT[1];
      up[0].type = 1; up[0].u.ki.wVk = 0x11; up[0].u.ki.dwFlags = 2;
      SendInput(1, up, Marshal.SizeOf(typeof(INPUT)));
      System.Threading.Thread.Sleep(gapMs);
    }
  }
}
'@

Head "1) 进程与窗口"
$tool = @(Get-Process DeepSeekQuake -ErrorAction SilentlyContinue)
if ($tool.Count -eq 0) { Fail "小工具没有运行。请双击 DeepSeek Quake.lnk 或 DeepSeekQuake.exe"; }
else { Pass "小工具在运行（PID $($tool[0].Id)，共 $($tool.Count) 个实例）" }
if ($tool.Count -gt 1) { Fail "有多个小工具实例，请只保留一个（任务管理器里结束多余的）" }

$toolPids = @($tool | ForEach-Object { $_.Id.ToString() })
$trace = [IntPtr]::Zero
if ($toolPids.Count -gt 0) { $trace = [QuakeCheck]::FindTraceWindow($toolPids) }
$traceText = if ($trace -ne [IntPtr]::Zero) { [QuakeCheck]::Text($trace) } else { "" }
if ($trace -eq [IntPtr]::Zero) { Warn "没找到小工具的诊断窗口，看不到内部状态" }
else {
    Write-Host "     内部状态: $traceText" -ForegroundColor DarkGray
    if ($traceText -match 'il=low') {
        Fail "当前实例是「低完整性」启动：Windows 会阻止它收到按键，热键必然没反应。请从桌面快捷方式正常启动。"
    }
}

# 目标窗口：优先只认「用本项目 profile 启动」的那个浏览器窗口。
# 否则本机 Edge 常年常驻，随便挑一个无 owner 的 Chrome_WidgetWin_1 会测到日常浏览器，
# 后面几项就全是假失败。
$browserPids = @()
foreach ($n in @('chrome','msedge')) {
    $browserPids += @(Get-Process $n -ErrorAction SilentlyContinue | ForEach-Object { $_.Id.ToString() })
}

$cfg = Join-Path $PSScriptRoot 'hotkey.conf'
$profileSetting = 'chrome-profile'
if (Test-Path $cfg) {
    $pl = Select-String -Path $cfg -Pattern '^\s*profile\s*=' | Select-Object -First 1
    if ($pl) { $profileSetting = ($pl.Line -split '=',2)[1].Trim() }
}
$profileFull = $profileSetting
if (-not [System.IO.Path]::IsPathRooted($profileFull)) { $profileFull = Join-Path $PSScriptRoot $profileFull }
try { $profileFull = [System.IO.Path]::GetFullPath($profileFull) } catch { }

$profilePids = @()
foreach ($bpId in $browserPids) {
    $cl = ''
    try { $cl = (Get-CimInstance Win32_Process -Filter ("ProcessId=" + $bpId) -ErrorAction SilentlyContinue).CommandLine } catch { }
    if ($cl -and $cl.IndexOf($profileFull, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $profilePids += $bpId
    }
}
$matchedByProfile = ($profilePids.Count -gt 0)
if (-not $matchedByProfile) { $profilePids = $browserPids }

$target = [IntPtr]::Zero
if ($profilePids.Count -gt 0) { $target = [QuakeCheck]::FindAppWindow($profilePids) }
if (-not $matchedByProfile -and $browserPids.Count -gt 0) {
    Warn "没有进程用本项目 profile（$profileFull）启动，退回到所有浏览器窗口；可能测到日常浏览器"
}

if ($target -eq [IntPtr]::Zero) {
    Fail "没找到浏览器的 app 窗口（chrome / msedge 里没有无 owner 的 Chrome_WidgetWin_1 窗口）"
    Write-Host "     下一步：右键托盘图标 →「自检与诊断…」→ 点「测试启动浏览器」，看它给出的具体原因。" -ForegroundColor Yellow
} else {
    Pass "找到目标窗口 hwnd=0x$($target.ToInt64().ToString('X'))  标题=[$([QuakeCheck]::Text($target))]  可见=$([QuakeCheck]::IsWindowVisible($target))"
}

Head "2) 热键是否启用"
if ($traceText -match 'hk=([^\s]+)') { Write-Host "     生效热键: $($Matches[1])" -ForegroundColor DarkGray }
if ($traceText -match 'khook=(\d)') {
    if ($Matches[1] -eq '1') { Pass "双击识别用的键盘钩子已安装" }
    else { Warn "键盘钩子没装（如果你用的是组合键热键，这属正常）" }
}
if ($traceText -match 'mhook=(\d)') {
    if ($Matches[1] -eq '1') { Pass "右键粘贴用的鼠标钩子已安装" }
    else { Warn "鼠标钩子没装（只在本窗口前台时装；先把窗口呼出到前台再跑一次）" }
}
if ($traceText -match 'mgd=(\d)') {
    if ($Matches[1] -eq '1') { Pass "鼠标手势守卫钩子已安装（Ctrl+鼠标手势不会误武装双击）" }
    else { Warn "鼠标手势守卫钩子没装：Ctrl+鼠标手势后单点 Ctrl 仍可能误触发" }
}

Head "3) 切换链路（合成切换消息）"
if ($trace -eq [IntPtr]::Zero) { Warn "拿不到诊断窗口，跳过" }
elseif ($target -eq [IntPtr]::Zero) { Warn "没有目标窗口，跳过" }
else {
    # 先直接收起，从确定状态开始测。现在「窗口可见就收起」，不再区分是否在前台。
    # （注意：测试期间若用户手动按热键，会并发改变可见性，可能让这一项误报。）
    #
    # wParam 带真实 TickCount：热键链路投递 WM_APP_TOGGLE 时就是这么填的。
    # 以前这里填 0，等于跳过了那段时间戳判断，于是「收不回去」这个缺陷在自检里
    # 完全没有覆盖（上一版就是这么漏掉的）。保持和真实投递同形，才不会白测。
    $stamp = [IntPtr][Environment]::TickCount
    [void][QuakeCheck]::ShowWindow($target, 0)
    Start-Sleep -Milliseconds 700
    $before = [QuakeCheck]::IsWindowVisible($target)
    [void][QuakeCheck]::PostMessage($trace, 0x8000 + 0x50, $stamp, [IntPtr]::Zero)  # WM_APP_TOGGLE
    Start-Sleep -Milliseconds $WaitMs
    $after1 = [QuakeCheck]::IsWindowVisible($target)
    if ($before -ne $after1) { Pass "可见性翻转：$before → $after1" }
    else { Fail "可见性没变（$before → $after1），说明小工具没有真正操作用这个窗口" }

    [void][QuakeCheck]::PostMessage($trace, 0x8000 + 0x50, [IntPtr][Environment]::TickCount, [IntPtr]::Zero)
    Start-Sleep -Milliseconds $WaitMs
    $after2 = [QuakeCheck]::IsWindowVisible($target)
    if ($after2 -eq $before) { Pass "再次切换回到原状态：$after1 → $after2" }
    else { Fail "第二次切换没有回到原状态（$after1 → $after2）" }
}

Head "4) 真实按键"
if ($SkipKeyTest) { Warn "按 -SkipKeyTest 跳过" }
elseif ($target -eq [IntPtr]::Zero) { Warn "没有目标窗口，跳过" }
else {
    $cfg = Join-Path $PSScriptRoot 'hotkey.conf'
    $hotkey = ''
    if (Test-Path $cfg) {
        $line = Select-String -Path $cfg -Pattern '^\s*hotkey\s*=' | Select-Object -First 1
        if ($line) { $hotkey = ($line.Line -split '=',2)[1].Trim() }
    }
    Write-Host "     配置的热键: $hotkey" -ForegroundColor DarkGray

    $visBefore = [QuakeCheck]::IsWindowVisible($target)
    $dtBefore = -1
    if ($trace -ne [IntPtr]::Zero) {
        $tt0 = [QuakeCheck]::Text($trace)
        if ($tt0 -match 'dt=(\d+)') { $dtBefore = [int]$Matches[1] }
    }
    if ($hotkey -match 'DOUBLE\s*:?\s*(CTRL|SHIFT|ALT|WIN)' -and -not $ToggleKey) {
        $mod = $Matches[1]
        if ($mod -ne 'CTRL') {
            Warn "配置的是双击 $mod，本脚本只会合成 Ctrl，请手动连按两下 $mod"
        } else {
            Write-Host "     正在合成「连按两下 Ctrl」…" -ForegroundColor DarkGray
            [QuakeCheck]::TapCtrl(2, 90)
            Start-Sleep -Milliseconds ($WaitMs + 400)
            $visAfter = [QuakeCheck]::IsWindowVisible($target)
            $dtAfter = -1
            if ($trace -ne [IntPtr]::Zero) {
                $tt1 = [QuakeCheck]::Text($trace)
                if ($tt1 -match 'dt=(\d+)') { $dtAfter = [int]$Matches[1] }
            }
            if ($visAfter -ne $visBefore -and $dtAfter -gt $dtBefore) {
                Pass "双击 Ctrl 真的触发了切换（dt $dtBefore → $dtAfter，可见 $visBefore → $visAfter）"
            }
            elseif ($dtAfter -le $dtBefore -and $dtBefore -ge 0) {
                Fail "钩子没有收到双击 Ctrl（dt $dtBefore → $dtAfter），热键在这台机器上已失效"
                Write-Host "     这是「呼不出 / 收不起」的典型症状：钩子句柄还在，但已收不到按键。" -ForegroundColor Yellow
            }
            else {
                Fail "双击 Ctrl 没有触发切换（可见 $visBefore → $visAfter）"
                Write-Host "     注意：合成按键会被某些程序/安全软件区分对待，最好手动连按两下 Ctrl 复测一次。" -ForegroundColor Yellow
            }
        }
    } else {
        Warn "组合键无法安全地自动合成（可能被当前前台窗口抢走），请手动按一次 $hotkey 观察窗口是否收起/呼出"
    }
}

Head "5) 任务栏 / Alt+Tab 排除"
if ($target -eq [IntPtr]::Zero) { Warn "没有目标窗口，跳过" }
else {
    $ex = [QuakeCheck]::ExStyle($target)
    if (($ex -band 0x80) -ne 0) { Pass "窗口带 WS_EX_TOOLWINDOW，不会出现在任务栏 / Alt+Tab" }
    else { Fail "窗口没有 WS_EX_TOOLWINDOW，会占用任务栏 / Alt+Tab" }
    if (-not [QuakeCheck]::HasOwner($target)) { Pass "窗口无 owner（符合 Alt+Tab 排除条件）" }
    else { Warn "窗口有 owner，Alt+Tab 行为可能不符合预期" }
}

Head "结果"
Write-Host ("  通过 $ok 项，失败 $bad 项，存疑 $unknown 项") -ForegroundColor $(if ($bad -eq 0) { 'Green' } else { 'Red' })
if ($bad -gt 0) {
    Write-Host "  建议：托盘右键 →「自检与诊断…」→「复制全部」，把内容发出来定位。" -ForegroundColor Yellow
    exit 1
}
exit 0
