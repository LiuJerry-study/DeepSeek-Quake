<#
  右键粘贴（鼠标钩子）的自动验证

  思路：测试页把 textarea 内容实时写进 document.title，标题里还带 "DeepSeek" 字样，
  这样小工具会把它当成目标窗口。于是流程变成：
      临时把 hotkey.conf 指向测试页 → 启动小工具 → 设剪贴板 → 在窗口中心点一次右键 → 读标题
  标题里出现剪贴板内容 = 右键粘贴生效。

  用法：pwsh -File src\test-rightclick.ps1
  结束会自动还原 hotkey.conf 并重新拉起真实实例。
#>
[CmdletBinding()]
param()

$root = Split-Path $PSScriptRoot -Parent
$conf = Join-Path $root 'hotkey.conf'
$exe = Join-Path $root 'DeepSeekQuake.exe'
$page = Join-Path $PSScriptRoot 'test-paste.html'
$prof = Join-Path $root '.test-profile'
$sample = 'PASTED-BY-RIGHTCLICK-中文123'

Add-Type -TypeDefinition @'
using System; using System.Text; using System.Runtime.InteropServices;
public class MouseProbe2 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

  public static IntPtr FindByTitle(string prefix) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => {
      var cls = new StringBuilder(64); GetClassNameW(h, cls, 64);
      if (cls.ToString() != "Chrome_WidgetWin_1") return true;
      var t = new StringBuilder(512); GetWindowTextW(h, t, 512);
      if (t.ToString().StartsWith(prefix)) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
  public static string Title(IntPtr h) {
    var t = new StringBuilder(1024); GetWindowTextW(h, t, 1024); return t.ToString();
  }
  // 读小工具那个隐藏窗口的标题（里面写着钩子计数 DSQ d= u= p=）
  public static string TraceOf(string procName) {
    string found = "";
    var procs = System.Diagnostics.Process.GetProcessesByName(procName);
    var ids = new System.Collections.Generic.List<uint>();
    foreach (var p in procs) ids.Add((uint)p.Id);
    EnumWindows((h, l) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (!ids.Contains(pid)) return true;
      var t = new StringBuilder(512); GetWindowTextW(h, t, 512);
      if (t.ToString().StartsWith("DSQ ")) { found = t.ToString(); return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
  // 把窗口抢到前台：SetWindowPos 置顶 + AttachThreadInput 绕过前台锁，失败就重试
  public static bool Activate(IntPtr h) {
    for (int i = 0; i < 12; i++) {
      SetWindowPos(h, (IntPtr)(-1), 0, 0, 0, 0, 0x0001 | 0x0002);   // HWND_TOPMOST, NOSIZE|NOMOVE
      if (GetForegroundWindow() == h) return true;
      uint dummy;
      uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out dummy);
      uint myThread = GetCurrentThreadId();
      AttachThreadInput(myThread, fgThread, true);
      SetForegroundWindow(h);
      AttachThreadInput(myThread, fgThread, false);
      System.Threading.Thread.Sleep(300);
      if (GetForegroundWindow() == h) return true;
    }
    return GetForegroundWindow() == h;
  }

  public static void RightClickCenter(IntPtr h) {
    RECT r; GetWindowRect(h, out r);
    int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
    Activate(h);
    System.Threading.Thread.Sleep(400);
    SetCursorPos(cx, cy);
    System.Threading.Thread.Sleep(300);
    mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);   // LEFTDOWN：激活并把光标放进输入框
    System.Threading.Thread.Sleep(60);
    mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);   // LEFTUP
    System.Threading.Thread.Sleep(600);
    // 敲三个 a 验证输入框真的拿到了键盘焦点（标题会跟着出现 aaa）
    for (int i = 0; i < 3; i++) {
      keybd_event(0x41, 0, 0, IntPtr.Zero);
      System.Threading.Thread.Sleep(30);
      keybd_event(0x41, 0, 2, IntPtr.Zero);
      System.Threading.Thread.Sleep(60);
    }
    System.Threading.Thread.Sleep(800);
    // 再右键
    mouse_event(0x0008, 0, 0, 0, IntPtr.Zero);   // RIGHTDOWN
    System.Threading.Thread.Sleep(80);
    mouse_event(0x0010, 0, 0, 0, IntPtr.Zero);   // RIGHTUP
  }

  // 按住 Shift 的右键：应当**不**粘贴（留给原生菜单）
  public static void ShiftRightClickCenter(IntPtr h) {
    RECT r; GetWindowRect(h, out r);
    int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
    SetCursorPos(cx, cy);
    System.Threading.Thread.Sleep(200);
    keybd_event(0x10, 0, 0, IntPtr.Zero);        // SHIFT down
    System.Threading.Thread.Sleep(120);
    mouse_event(0x0008, 0, 0, 0, IntPtr.Zero);
    System.Threading.Thread.Sleep(80);
    mouse_event(0x0010, 0, 0, 0, IntPtr.Zero);
    System.Threading.Thread.Sleep(120);
    keybd_event(0x10, 0, 2, IntPtr.Zero);        // SHIFT up
    System.Threading.Thread.Sleep(300);
    keybd_event(0x1B, 0, 0, IntPtr.Zero);        // ESC：如果真弹了菜单就关掉
    System.Threading.Thread.Sleep(60);
    keybd_event(0x1B, 0, 2, IntPtr.Zero);
  }
}
'@

function Stop-Pieces {
  Get-Process DeepSeekQuake -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like "*$prof*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
  Start-Sleep -Milliseconds 900
}

$backup = $null
$result = $false
try {
  Stop-Pieces
  Remove-Item $prof -Recurse -Force -ErrorAction SilentlyContinue

  $url = 'file:///' + ($page -replace '\\', '/' -replace ' ', '%20')
  # 用命令行参数覆盖配置 —— 不去碰用户的 hotkey.conf
  Write-Host "启动小工具（命令行指向测试页）…" -ForegroundColor Cyan
  Start-Process -FilePath $exe -WorkingDirectory $root `
    -ArgumentList ('--url="' + $url + '" --profile="' + $prof + '"') | Out-Null

  $hwnd = [IntPtr]::Zero
  for ($i = 0; $i -lt 40 -and $hwnd -eq [IntPtr]::Zero; $i++) {
    Start-Sleep -Milliseconds 500
    $hwnd = [MouseProbe2]::FindByTitle('DeepSeek PASTE-TEST')
  }
  if ($hwnd -eq [IntPtr]::Zero) { Write-Host '没找到测试窗口 ❌' -ForegroundColor Red; return }

  Start-Sleep -Seconds 2
  Set-Clipboard -Value $sample
  $wasForeground = [MouseProbe2]::Activate($hwnd)
  Write-Host "剪贴板 = $sample" -ForegroundColor Cyan
  Write-Host ("测试窗口是否在前台：{0}" -f $wasForeground)
  if (-not $wasForeground) {
    Write-Host '抢不到前台（这台机器上 DSH 窗口会和测试抢焦点）——本轮结果不算数' -ForegroundColor Yellow
  }
  Write-Host "在输入框中心点右键…" -ForegroundColor Cyan
  [MouseProbe2]::RightClickCenter($hwnd)
  Start-Sleep -Seconds 3

  $title = [MouseProbe2]::Title($hwnd)
  $trace = [MouseProbe2]::TraceOf('DeepSeekQuake')
  $instances = (Get-Process DeepSeekQuake -ErrorAction SilentlyContinue | Measure-Object).Count
  $focusOk = $title -like '*aaa*'
  $result = $title -like "*$sample*"
  $count = ([regex]::Matches($title, [regex]::Escape($sample))).Count
  Write-Host ("右键后标题 = {0}" -f $title)
  Write-Host ("输入框焦点（标题里有 aaa）= {0}" -f $focusOk)
  Write-Host ("剪贴板内容出现次数 = {0}    DeepSeekQuake 进程数 = {1}" -f $count, $instances)
  Write-Host ("钩子计数: {0}" -f $trace)
  Write-Host ("→ {0}" -f $(if ($result) { '右键粘贴成功 ✅' } else { '右键粘贴没生效 ❌' })) `
    -ForegroundColor $(if ($result) { 'Green' } else { 'Red' })

  # 对照组：Shift+右键 不应粘贴
  [MouseProbe2]::ShiftRightClickCenter($hwnd)
  Start-Sleep -Seconds 2
  $title2 = [MouseProbe2]::Title($hwnd)
  $count2 = ([regex]::Matches($title2, [regex]::Escape($sample))).Count
  $shiftOk = $count2 -eq $count
  Write-Host ("Shift+右键 后次数 = {0}（应保持不变）→ {1}" -f $count2, $(if ($shiftOk) { '通过 ✅（留给原生菜单）' } else { '未通过 ❌' })) `
    -ForegroundColor $(if ($shiftOk) { 'Green' } else { 'Red' })
}
finally {
  Stop-Pieces
  Remove-Item $prof -Recurse -Force -ErrorAction SilentlyContinue
  Start-Process -FilePath $exe -WorkingDirectory $root | Out-Null
  Write-Host '已重启真实实例（hotkey.conf 全程未被改动）。' -ForegroundColor DarkGray
}
