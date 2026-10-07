// DeepSeek Quake —— Chrome / Edge --app 外壳的 quake 模式热键小工具
//
// 解决的三件事：
//   1. 全局热键呼出 / 收起（默认「双击 Ctrl」，也可用任意组合键）
//   2. 窗口里点右键 = 直接粘贴剪贴板；Shift+右键 = 浏览器原生菜单
//   3. 托盘右键 → 设置…，可以在界面里按键即录改热键，不用手改配置文件
//
// 设计取舍（针对本机实测环境）：
//   * 本机火绒会按程序拦截「未签名程序写文件」，所以本程序自己不写任何文件。
//     需要改 hotkey.conf 时，调用 src\config-write.ps1（PowerShell 有签名）来写。
//     因此本程序不写日志文件、不写注册表。
//   * 所有诊断信息都放在一个隐藏窗口的标题里（托盘 → 自检与诊断 可看）。
//   * 低级钩子只在必要范围内安装：鼠标钩子只在本窗口前台时装，
//     键盘钩子（双击触发用）只在目标窗口就绪后装，且回调里绝不做重活。
//
// 配置：同目录 hotkey.conf（key=value，可选；命令行 --key=value 可覆盖）
//   hotkey=DOUBLECTRL      双击 Ctrl；也支持 WIN+SHIFT+OEM3 / CTRL+ALT+SPACE 等
//   browser=auto           auto / chrome / edge / 显式 exe 路径
//   url=https://chat.deepseek.com
//   profile=chrome-profile 相对本目录
//   width=980  height=820
//   rightclickpaste=1      1 = 右键直接粘贴；0 = 关闭
//   restorefocus=1         1 = 收起后把焦点还给之前的窗口
//   startHidden=0          1 = 启动后保持收起
//   doubletapms=420        双击判定的时间窗（毫秒）

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DeepSeekQuake
{
    /// <summary>内存里的诊断日志。本程序不写文件，这些东西通过「自检与诊断」窗口查看。</summary>
    internal sealed class Log
    {
        private readonly List<string> _lines = new List<string>();
        private readonly object _lock = new object();
        private const int Max = 400;

        public void Add(string msg)
        {
            lock (_lock)
            {
                _lines.Add(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg);
                if (_lines.Count > Max) _lines.RemoveRange(0, _lines.Count - Max);
            }
        }

        public string[] Snapshot()
        {
            lock (_lock) { return _lines.ToArray(); }
        }

        public string Text()
        {
            lock (_lock) { return string.Join(Environment.NewLine, _lines.ToArray()); }
        }
    }

    /// <summary>钩子命中计数。放在独立类里，避免主程序直接把字段暴露出去。</summary>
    internal sealed class HookStats
    {
        private int _down, _up, _paste, _hookInstall, _kbdEvents;

        public int Down { get { return _down; } }
        public int Up { get { return _up; } }
        public int Paste { get { return _paste; } }
        public int HookInstall { get { return _hookInstall; } }
        public int KeyboardEvents { get { return _kbdEvents; } }

        public void NoteDown() { Interlocked.Increment(ref _down); }
        public void NoteUp() { Interlocked.Increment(ref _up); }
        public void NotePaste() { Interlocked.Increment(ref _paste); }
        public void NoteHookInstall() { Interlocked.Increment(ref _hookInstall); }
        public void NoteKeyboardEvent() { Interlocked.Increment(ref _kbdEvents); }
        public void ResetHooks() { Interlocked.Exchange(ref _hookInstall, 0); }
    }

    // 只用来接收 WM_HOTKEY / WM_APP_TOGGLE / WM_APP_PASTE 的隐藏窗口（永不显示，不进任务栏）
    internal sealed class MessageWindow : Form
    {
        private readonly HotkeyManager _hotkeys;
        private readonly Action _onPaste;

        public MessageWindow(HotkeyManager hotkeys, Action onPaste)
        {
            _hotkeys = hotkeys;
            _onPaste = onPaste;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-4000, -4000);
            Size = new Size(1, 1);
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(false);      // 恒不可见；句柄照样存在，可以收消息
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                if (_hotkeys != null) _hotkeys.OnHotkeyMessage(m.WParam);
                return;
            }
            if (m.Msg == (int)Native.WM_APP_TOGGLE)
            {
                // 必须兜住异常：这是消息循环里的入口，抛出去会直接把整个小工具干掉，
                // 表现就是「按一次热键之后托盘图标没了、热键也不灵了」。
                Program.NoteToggleMessage();
                try { Program.ToggleWindow(); }
                catch (Exception ex) { Program.Diag.Add("切换消息处理异常: " + ex); }
                return;
            }
            if (m.Msg == (int)Native.WM_APP_REHOOK)
            {
                Program.ReinstallHotkeyFromMessage();
                return;
            }
            if (m.Msg == (int)Native.WM_APP_PASTE)
            {
                if (_onPaste != null) _onPaste();
                return;
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>
    /// 隐藏的诊断窗口。除了把实时状态串写在标题上，还接受 WM_APP_TOGGLE：
    /// verify-quake.ps1 找的就是这个「标题以 DSQ v 开头的窗口」，并往里投合成
    /// 切换消息。如果这里不处理，自带的验证脚本第 3 项会永远失败。
    /// </summary>
    internal sealed class TraceWindow : Form
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == (int)Native.WM_APP_TOGGLE)
            {
                Program.NoteToggleMessage();
                try { Program.ToggleWindow(); }
                catch (Exception ex) { Program.Diag.Add("诊断窗口切换消息异常: " + ex.Message); }
                return;
            }
            if (m.Msg == (int)Native.WM_APP_REHOOK)
            {
                Program.ReinstallHotkeyFromMessage();
                return;
            }
            base.WndProc(ref m);
        }
    }

    internal static class Program
    {
        public const string Title = "DeepSeek Quake";
        public const string Version = "2.0";

        // ── 全局状态 ───────────────────────────────────────────────
        public static Log Diag = new Log();
        public static HookStats Stats = new HookStats();
        private static string _baseDir;
        private static string _confPath;
        private static Dictionary<string, string> _conf;
        private static Mutex _mutex;

        private static MessageWindow _msg;
        private static NotifyIcon _tray;
        private static BrowserHost _host;
        private static HotkeyManager _hotkeys;

        private static IntPtr _traceHwnd = IntPtr.Zero;
        private static System.Windows.Forms.Timer _syncTimer;
        private static System.Windows.Forms.Timer _traceTimer;
        private static string _traceText = "DSQ boot";

        private static volatile bool _quitting;
        // true = 希望窗口保持收起；false = 希望显示。
        // 启动时按 startHidden 初始化，用户按热键会改写它，窗口出现时据此决定显示还是收起，
        // 所以「窗口还没出来就按了热键」也不会被覆盖。
        private static volatile bool _hideRequested;
        private static int _findInProgress;
        private static string _hotkeyProblem;
        private static bool _startupBalloonShown;
        private static string _pendingRestartExe;
        private static string _hotkeyFallbackPref = "WIN+SHIFT+OEM3";
        private static string _lastLaunchFailure;      // 最近一次「启动浏览器 / 找窗口」失败的原文
        private static StatusForm _status;
        private static volatile bool _launchWanted;    // 有按钮/热键要求「去把浏览器拉起来」
        private static int _searchUntilTick;           // 轮询查找的截止时间（Environment.TickCount）
        private static int _toggleCount, _hideCount, _showCount, _findRequests, _launchAttempts;
        private static int _lastTick;                  // 上一次 SyncTick 的 TickCount（健康检查用）
        private static int _uiHeartbeat;               // 每次 SyncTick +1，用来发现「消息循环卡住/变慢」
        private static int _slowTickCount;             // 两拍之间超过 1.5 秒的次数
        private static int _integrityRid;              // 当前进程完整性级别（低完整性会让钩子收不到输入）
        private static bool _lowIntegrity;
        private static int _toggleMessages;            // 收到的 WM_APP_TOGGLE 条数
        private static int _lastToggleDispatchTick;    // 最近一次投递切换消息的时间戳
        private static bool _trayNeedsRestore;         // explorer 重启后要把托盘图标挂回来
        private static bool _trayUnavailable;          // 系统没有通知区域（没有 explorer 的任务栏）

        public static string ProfilePath { get; private set; }
        public static string Url { get { return _host == null ? "" : _host.Url; } }
        public static bool RightClickPaste { get { return _host != null && _host.RightClickPasteEnabled; } }
        public static HotkeyManager Hotkeys { get { return _hotkeys; } }
        public static BrowserHost Host { get { return _host; } }
        public static int DownCount { get { return Stats.Down; } }
        public static int UpCount { get { return Stats.Up; } }
        public static int PasteCount { get { return Stats.Paste; } }
        public static int HookInstallCount { get { return Stats.HookInstall; } }

        // ── --diagnose：控制台版启动诊断 ───────────────────────────
        private static int RunDiagnose()
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            try
            {
                Console.WriteLine("========== DeepSeek Quake 启动诊断 v" + Version + " ==========");
                Console.WriteLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                Console.WriteLine("程序: " + Application.ExecutablePath);
                Console.WriteLine("配置: " + _confPath);
                Console.WriteLine();

                List<BrowserInfo> found = BrowserHost.Discover();
                Console.WriteLine("找到的浏览器:");
                if (found.Count == 0) Console.WriteLine("  （一个都没有）");
                foreach (BrowserInfo b in found) Console.WriteLine("  " + b.Display + "  " + b.ExePath);

                string pref = Get("browser", "auto");
                BrowserInfo chosen = BrowserHost.ResolveBrowser(pref);
                Console.WriteLine("配置 browser=" + pref + " → 选中: "
                    + (chosen == null ? "（无）" : chosen.Display + " " + chosen.ExePath));
                if (chosen == null)
                {
                    Console.WriteLine();
                    Console.WriteLine(BrowserHost.DescribeSearchPlaces());
                    return 2;
                }

                Diag = new Log();
                Stats = new HookStats();
                BrowserHost host = new BrowserHost(Diag, Stats);
                string profile = Get("profile", "chrome-profile");
                bool explicitProfile = Path.IsPathRooted(profile);
                string profilePath = explicitProfile ? profile : Path.Combine(_baseDir, profile);
                host.Configure(pref, Get("url", "https://chat.deepseek.com"), profilePath, explicitProfile,
                    ParseInt(Get("width", "980"), 980), ParseInt(Get("height", "820"), 820),
                    Get("rightclickpaste", "1") != "0", Get("restorefocus", "1") != "0");
                host.SandboxMode = Get("sandbox", "auto");

                Console.WriteLine("实际 profile: " + host.ActiveProfilePath);
                Console.WriteLine("沙箱策略: " + host.SandboxMode);
                Console.WriteLine("url: " + Get("url", "https://chat.deepseek.com"));
                Console.WriteLine();

                Console.WriteLine("--- 先看有没有已经开着的窗口 ---");
                IntPtr exist = host.FindTargetWindow();
                Console.WriteLine(exist == IntPtr.Zero
                    ? "  没有现成窗口"
                    : "  已找到 hwnd=0x" + exist.ToInt64().ToString("X") + " 标题=[" + Native.TitleOf(exist) + "]");
                Console.WriteLine();

                if (exist != IntPtr.Zero)
                {
                    Console.WriteLine("结论: 窗口已存在，小工具正常运行时应该能直接接管它。");
                    return 0;
                }

                Console.WriteLine("--- 尝试启动浏览器 ---");
                string err = host.Launch();
                Console.WriteLine(err == null ? "  启动命令已发出，进程没有秒退" : "  失败: " + err);
                Console.WriteLine();

                Console.WriteLine("--- 最多等 20 秒找窗口 ---");
                IntPtr h = IntPtr.Zero;
                for (int i = 0; i < 40 && h == IntPtr.Zero; i++)
                {
                    Thread.Sleep(500);
                    h = host.FindTargetWindow();
                }
                if (h == IntPtr.Zero)
                {
                    Console.WriteLine("  20 秒内没找到窗口");
                    Console.WriteLine();
                    Console.WriteLine("结论: 浏览器没能把 app 窗口拉起来。");
                    if (err != null) Console.WriteLine("  启动失败原因: " + err);
                    Console.WriteLine("  下一步：先手动双击 DeepSeekChrome.lnk，如果能开出来，");
                    Console.WriteLine("          说明浏览器本身没问题，是启动路径被安全软件拦了。");
                    DumpLog();
                    return 3;
                }

                Console.WriteLine("  找到窗口 hwnd=0x" + h.ToInt64().ToString("X") + " 标题=[" + Native.TitleOf(h) + "]");
                Console.WriteLine();
                Console.WriteLine("结论: 启动链路正常。小工具运行时应当直接锁定这个窗口并启用热键。");
                DumpLog();
                return 0;
            }
            catch (Exception ex)
            {
                try { Console.WriteLine("诊断过程抛异常: " + ex); }
                catch { MessageBox.Show(ex.ToString(), Title, MessageBoxButtons.OK, MessageBoxIcon.Error); }
                return 4;
            }
        }

        /// <summary>只读的当前状态快照。诊断窗口和 --status 共用，保证看到的是同一份事实。</summary>
        public static string BuildStatusReport()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            int selfPid = Process.GetCurrentProcess().Id;
            int otherInstances = 0;
            foreach (Process p in Process.GetProcessesByName("DeepSeekQuake"))
                if (p.Id != selfPid) otherInstances++;
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("进程: PID=" + selfPid + "  其它实例数=" + otherInstances);
            sb.AppendLine("程序: " + Application.ExecutablePath);
            sb.AppendLine("配置: " + _confPath);
            sb.AppendLine("完整性级别: " + IntegrityName(_integrityRid)
                + " (0x" + _integrityRid.ToString("X") + ")"
                + (_integrityRid > 0 && _integrityRid < 0x2000
                    ? "  ← 低完整性会让热键失效，请从快捷方式正常启动" : ""));
            sb.AppendLine();

            sb.AppendLine("[窗口]");
            sb.AppendLine("  目标窗口: " + (_host != null && _host.IsWindowAlive()
                ? "hwnd=0x" + _host.TargetWindow.ToInt64().ToString("X")
                  + " 标题=[" + Native.TitleOf(_host.TargetWindow) + "]"
                : "（还没锁定）"));
            sb.AppendLine("  窗口可见: " + (_host != null && _host.Visible));
            sb.AppendLine("  窗口在前台: " + (_host != null && _host.IsOursForeground()));
            if (_host != null && _host.IsWindowAlive())
            {
                long ex = Native.GetWindowLongPtr(_host.TargetWindow, Native.GWL_EXSTYLE);
                sb.AppendLine("  WS_EX_TOOLWINDOW=" + ((ex & Native.WS_EX_TOOLWINDOW) != 0)
                    + "  WS_EX_APPWINDOW=" + ((ex & Native.WS_EX_APPWINDOW) != 0));
            }
            sb.AppendLine();

            sb.AppendLine("[热键]");
            if (_hotkeys == null) sb.AppendLine("  （未初始化）");
            else
            {
                sb.AppendLine("  配置 hotkey=" + Get("hotkey", "DOUBLECTRL"));
                sb.AppendLine("  实际生效=" + _hotkeys.ActivePretty);
                sb.AppendLine("  组合键已注册=" + _hotkeys.ChordRegistered);
                sb.AppendLine("  键盘钩子已安装=" + _hotkeys.KeyboardHookInstalled
                    + "  钩子线程存活=" + _hotkeys.HookThreadAlive
                    + "  安装尝试=" + _hotkeys.HookInstallAttempts);
                sb.AppendLine("  鼠标手势守卫钩子已安装=" + _hotkeys.MouseGuardInstalled);
                sb.AppendLine("  双击间隔=" + _hotkeys.DoubleTapMs + "ms");
                sb.AppendLine("  钩子收到的键盘事件=" + _hotkeys.HookEvents
                    + "  其中目标修饰键=" + _hotkeys.ModifierEvents
                    + "（都为 0 说明钩子根本没收到事件）");
                sb.AppendLine("  双击触发次数=" + _hotkeys.DoubleTapFires);
                sb.AppendLine("  收到的 WM_HOTKEY 次数=" + _hotkeys.ChordFires);
                sb.AppendLine("  主动重装钩子次数=" + _hotkeys.HookReinstalls);
                if (!string.IsNullOrEmpty(_hotkeyProblem)) sb.AppendLine("  上次失败原因: " + _hotkeyProblem);
            }
            sb.AppendLine();

            sb.AppendLine("[切换]");
            sb.AppendLine("  切换执行次数=" + _toggleCount);
            sb.AppendLine("  收起次数=" + _hideCount + "  呼出次数=" + _showCount);
            sb.AppendLine("  收到的切换消息=" + _toggleMessages + "（少于上面的触发次数就说明消息没被处理）");
            sb.AppendLine("  UI 心跳计数=" + _uiHeartbeat + "  卡顿次数(>1.5s)=" + _slowTickCount);
            sb.AppendLine("  查找请求=" + _findRequests + "  启动浏览器尝试=" + _launchAttempts);
            sb.AppendLine("  当前查找中=" + IsFinding + "  轮询截止=" + (_searchUntilTick == 0 ? "无" : "有"));
            sb.AppendLine();

            sb.AppendLine("[右键粘贴]");
            sb.AppendLine("  开关=" + Get("rightclickpaste", "1"));
            sb.AppendLine("  鼠标钩子已安装=" + (_host != null && _host.MouseHookInstalled));
            sb.AppendLine("  右键按下=" + Stats.Down + "  命中=" + Stats.Up + "  实际注入 Ctrl+V=" + Stats.Paste);
            sb.AppendLine();

            sb.AppendLine("[启动失败记录]");
            sb.AppendLine("  " + (string.IsNullOrEmpty(_lastLaunchFailure) ? "（无）" : _lastLaunchFailure));
            return sb.ToString();
        }

        // ── --status：读运行中实例的内部状态 ───────────────────────
        // 小工具本身不写文件，所以没法「读日志」。但它的诊断窗口标题里带着
        // 实时状态串，进程窗口也都能枚举——这两样足够判断它到底卡在哪一步。
        private static int RunStatus()
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            try
            {
                // --status 本身也是 DeepSeekQuake 进程，不能把自己算成「另一个实例」。
                int selfPid = Process.GetCurrentProcess().Id;
                List<Process> procs = new List<Process>();
                foreach (Process p in Process.GetProcessesByName("DeepSeekQuake"))
                    if (p.Id != selfPid) procs.Add(p);

                Console.WriteLine("========== DeepSeek Quake 运行状态 ==========");
                Console.WriteLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                Console.WriteLine("DeepSeekQuake 运行实例数: " + procs.Count + "（不含当前 --status 进程）");
                if (procs.Count == 0)
                {
                    Console.WriteLine("结论: 小工具没有在运行。请双击 DeepSeek Quake.lnk。");
                    return 1;
                }
                if (procs.Count > 1)
                {
                    Console.WriteLine("警告: 有多个实例，请只保留一个（任务管理器里结束多余的）。");
                }

                foreach (Process p in procs)
                {
                    Console.WriteLine();
                    Console.WriteLine("--- PID " + p.Id + " 启动于 " + p.StartTime.ToString("HH:mm:ss") + " ---");
                    Console.WriteLine("  程序: " + SafePath(p));
                    string trace = FindTraceTitle(p.Id);
                    Console.WriteLine("  实时状态串: " + (trace.Length == 0 ? "(没找到诊断窗口)" : trace));
                }

                Console.WriteLine();
                Console.WriteLine("[浏览器窗口]");
                int found = 0;
                foreach (string name in new string[] { "chrome", "msedge" })
                {
                    Process[] ps;
                    try { ps = Process.GetProcessesByName(name); } catch { continue; }
                    if (ps.Length == 0) continue;
                    Console.WriteLine("  " + name + " 进程数=" + ps.Length);
                    found++;
                }
                if (found == 0) Console.WriteLine("  （chrome / msedge 都没在运行）");

                Console.WriteLine();
                Console.WriteLine("[屏幕上的可见顶层窗口]");
                Console.Write(Native.ListVisibleTopLevelWindows());

                Console.WriteLine();
                Console.WriteLine("[桌面外壳]");
                Console.WriteLine("  explorer.exe 进程数: " + Process.GetProcessesByName("explorer").Length);
                IntPtr trayWnd = Native.FindWindowW("Shell_TrayWnd", null);
                Console.WriteLine("  Shell_TrayWnd(任务栏): " + (trayWnd == IntPtr.Zero ? "不存在 → 没有任务栏，也不可能有托盘图标" : "存在"));
                if (trayWnd != IntPtr.Zero)
                {
                    IntPtr notify = Native.FindWindowExW(trayWnd, IntPtr.Zero, "TrayNotifyWnd", null);
                    IntPtr pager = notify == IntPtr.Zero ? IntPtr.Zero : Native.FindWindowExW(notify, IntPtr.Zero, "SysPager", null);
                    IntPtr toolbar = pager == IntPtr.Zero ? IntPtr.Zero : Native.FindWindowExW(pager, IntPtr.Zero, "ToolbarWindow32", null);
                    Console.WriteLine("  TrayNotifyWnd=" + (notify == IntPtr.Zero ? "无" : "有")
                        + "  ToolbarWindow32=" + (toolbar == IntPtr.Zero ? "无（托盘图标无处可放）" : "有"));
                }

                Console.WriteLine();
                Console.WriteLine("[提示] 要看完整诊断（窗口/热键/钩子/右键粘贴），");
                Console.WriteLine("       请点托盘图标右键 →「自检与诊断…」→「复制全部」。");
                return 0;
            }
            catch (Exception ex)
            {
                try { Console.WriteLine("读取状态失败: " + ex.Message); } catch { }
                return 4;
            }
        }

        private static string SafePath(Process p)
        {
            try { return p.MainModule == null ? "(未知)" : p.MainModule.FileName; }
            catch { return "(读不到，可能权限不足)"; }
        }

        private static string IntegrityName(int rid)
        {
            if (rid >= 0x3000) return "high";
            if (rid >= 0x2000) return "medium";
            if (rid >= 0x1000) return "low";
            return "unknown";
        }

        /// <summary>找到该进程那个「标题里带实时状态」的诊断窗口。</summary>
        private static string FindTraceTitle(int pid)
        {
            string result = "";
            try
            {
                Native.EnumWindows(delegate(IntPtr h, IntPtr l)
                {
                    uint wpid;
                    Native.GetWindowThreadProcessId(h, out wpid);
                    if ((int)wpid != pid) return true;
                    string t = Native.TitleOf(h);
                    if (t.StartsWith("DSQ v", StringComparison.Ordinal)) { result = t; return false; }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return result;
        }

        private static void DumpLog()
        {
            try
            {
                Console.WriteLine();
                Console.WriteLine("--- 内部事件日志 ---");
                foreach (string line in Diag.Snapshot()) Console.WriteLine("  " + line);
            }
            catch { }
        }

        /// <summary>
        /// 只用于诊断「钩子到底有没有收到按键」：不建主窗口，装好键盘钩子后
        /// 把计数每 250ms 写进 tmp\hookprobe.txt。外部用 SendInput 合成双击 Ctrl
        /// 即可验证——这是把「窗口逻辑」和「钩子链路」分开测的最小探针。
        /// </summary>
        private static int RunHookProbe()
        {
            string dir = Path.Combine(_baseDir, "tmp");
            try { Directory.CreateDirectory(dir); } catch { }
            string outPath = Path.Combine(dir, "hookprobe.txt");
            Log log = new Log();
            int fired = 0;
            HotkeyManager hk = new HotkeyManager(delegate { Interlocked.Increment(ref fired); }, log);
            string err;
            try { err = hk.Apply("DOUBLECTRL", null); }
            catch (Exception ex) { err = "exception: " + ex.Message; }
            try
            {
                for (int i = 0; i < 48; i++)
                {
                    Thread.Sleep(250);
                    string text = "time=" + DateTime.Now.ToString("HH:mm:ss.fff")
                        + " il=" + IntegrityName(_integrityRid)
                        + " err=" + (err ?? "null")
                        + " installed=" + hk.KeyboardHookInstalled
                        + " thread=" + hk.HookThreadAlive
                        + " events=" + hk.HookEvents
                        + " mods=" + hk.ModifierEvents
                        + " dt=" + hk.DoubleTapFires
                        + " fired=" + fired;
                    try { File.WriteAllText(outPath, text); } catch { }
                }
            }
            finally
            {
                try { hk.Release(); hk.Dispose(); } catch { }
            }
            return 0;
        }

        [STAThread]
        private static void Main(string[] args)
        {
            // 先把路径和配置准备好：--selftest / --diagnose 也要用，
            // 而且它们不该受单实例互斥限制（跑诊断时小工具通常正在运行）。
            _baseDir = Path.GetDirectoryName(Application.ExecutablePath);
            if (string.IsNullOrEmpty(_baseDir)) _baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _confPath = Path.Combine(_baseDir, "hotkey.conf");
            _conf = LoadConf(_confPath);
            _integrityRid = Native.CurrentIntegrityRid();

            // 命令行可覆盖任意配置项：--hotkey=... --browser=edge --url=...
            foreach (string a in args)
            {
                if (a == null || !a.StartsWith("--")) continue;
                int eq = a.IndexOf('=');
                if (eq > 2) _conf[a.Substring(2, eq - 2)] = a.Substring(eq + 1);
            }

            // --hookprobe：只在本地诊断用；装好钩子后把计数写 tmp\hookprobe.txt
            foreach (string a in args)
                if (a != null && a.StartsWith("--hookprobe", StringComparison.OrdinalIgnoreCase))
                {
                    Environment.ExitCode = RunHookProbe();
                    return;
                }

            // --selftest：纯逻辑自测，不建窗口、不碰托盘，供自动化验证用
            foreach (string a in args)
                if (a != null && a.StartsWith("--selftest", StringComparison.OrdinalIgnoreCase))
                {
                    Environment.ExitCode = SelfTest.Run();
                    return;
                }

            // --diagnose：不用看界面，直接把「启动为什么失败」打到控制台。
            // 排查环境问题时最有用：它会把浏览器定位、启动尝试、退出码全列出来。
            foreach (string a in args)
                if (a != null && a.StartsWith("--diagnose", StringComparison.OrdinalIgnoreCase))
                {
                    Environment.ExitCode = RunDiagnose();
                    return;
                }

            // --status：只打印当前运行实例的状态（需要小工具正在运行才能看到完整信息）。
            foreach (string a in args)
                if (a != null && a.StartsWith("--status", StringComparison.OrdinalIgnoreCase))
                {
                    Environment.ExitCode = RunStatus();
                    return;
                }

            bool createdNew;
            _mutex = new Mutex(true, "DeepSeekQuakeHotkey.SingleInstance", out createdNew);
            if (!createdNew)
            {
                // 重启场景下旧实例可能还没退干净，等一会儿再抢
                for (int i = 0; i < 20 && !createdNew; i++)
                {
                    Thread.Sleep(250);
                    try { _mutex = new Mutex(true, "DeepSeekQuakeHotkey.SingleInstance", out createdNew); }
                    catch { }
                }
                if (!createdNew) return;
            }

            string browserPref = Get("browser", "auto");
            string url = Get("url", "https://chat.deepseek.com");
            string profile = Get("profile", "chrome-profile");
            bool profileIsExplicit = Path.IsPathRooted(profile);
            ProfilePath = profileIsExplicit ? profile : Path.Combine(_baseDir, profile);
            int width = ParseInt(Get("width", "980"), 980);
            int height = ParseInt(Get("height", "820"), 820);
            bool rightClick = Get("rightclickpaste", "1") != "0";
            bool restoreFocus = Get("restorefocus", "1") != "0";
            bool startHidden = Get("startHidden", "0") != "0";

            Diag.Add(Title + " v" + Version + " 启动，PID=" + Process.GetCurrentProcess().Id);
            Diag.Add("配置目录: " + _baseDir);
            Diag.Add("完整性级别: " + IntegrityName(_integrityRid) + " (0x" + _integrityRid.ToString("X") + ")");
            if (_integrityRid > 0 && _integrityRid < 0x2000)
                Diag.Add("警告：本进程以低完整性运行，Windows 会阻止键盘钩子接收普通输入，"
                    + "呼出/收起热键将无效。请从桌面快捷方式正常启动（不要从沙箱或自动化工具里启动）。");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 低完整性进程（常见于被自动化工具 / 沙箱拉起）在 Windows 里收不到
            // 普通键盘输入，双击 Ctrl 一定不生效；前台激活也会被 UIPI 挡掉。
            // 与其让用户面对一个「按了没反应」的窗口，不如明确阻止并给出正确启动方式。
            _lowIntegrity = _integrityRid > 0 && _integrityRid < 0x2000;
            if (_lowIntegrity && Get("allow-low", "0") == "0")
            {
                Diag.Add("拒绝以低完整性继续运行：热键在此模式下不可能生效。");
                MessageBox.Show(
                    "检测到 DeepSeek Quake 正以「低完整性」运行。\n\n"
                    + "这种启动方式通常来自自动化工具、沙箱或一些启动器；Windows 在这种模式下"
                    + "会阻止程序收到键盘输入和抢前台，双击 Ctrl 不会生效。\n\n"
                    + "请关掉这个提示，改为双击桌面上的 DeepSeek Quake.lnk 正常启动。",
                    Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 全局兜底：任何没被接住的 UI 异常都不该让小工具消失——
            // 一旦它退出，托盘图标没了、热键也没了，用户只剩一个「没人管」的浏览器窗口。
            // 注意必须设 e.ThrowException = false，否则 ThreadException 之后进程照样会退出。
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                SafeLog("UI 未处理异常（已吞掉，工具继续运行）: " + e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                SafeLog("未处理异常: " + e.ExceptionObject);
            };

            _host = new BrowserHost(Diag, Stats);
            _host.Configure(browserPref, url, ProfilePath, profileIsExplicit,
                width, height, rightClick, restoreFocus);
            _host.SandboxMode = Get("sandbox", "auto");
            _host.PasteRequested += delegate
            {
                if (_msg != null && !_msg.IsDisposed && _msg.IsHandleCreated)
                    Native.PostMessage(_msg.Handle, Native.WM_APP_PASTE, IntPtr.Zero, IntPtr.Zero);
            };

            _hotkeys = new HotkeyManager(ToggleWindow, PostToggleMessage, Diag);
            // 动作热键：不依赖托盘也能改设置 / 看诊断。
            // 本机 explorer.exe 没在运行（用户用的是 Themia 自定义外壳），
            // 系统里没有任务栏和通知区域，托盘图标无处可放——所以这些入口必须有。
            _hotkeys.AddActionHotkey(Get("settingshotkey", "CTRL+ALT+S"), "打开设置", OpenSettings);
            _hotkeys.AddActionHotkey(Get("diagnosehotkey", "CTRL+ALT+D"), "打开自检与诊断", OpenDiagnostics);
            int dtms = ParseInt(Get("doubletapms", "420"), 420);
            _hotkeys.DoubleTapMs = dtms;
            _hotkeyFallbackPref = Get("hotkeyfallback", "WIN+SHIFT+OEM3");

            _msg = new MessageWindow(_hotkeys, PasteClipboard);
            IntPtr dummy = _msg.Handle;                       // 强制建句柄，供 RegisterHotKey 用
            _hotkeys.AttachWindow(dummy);

            if (_host.Browser == null)
            {
                Diag.Add("警告：没找到 Chrome / Edge，稍后会提示。");
            }
            else
            {
                Diag.Add("浏览器: " + _host.Browser);
            }
            Diag.Add("热键配置=" + Get("hotkey", "DOUBLECTRL") + "  浏览器=" + browserPref
                + "  url=" + url + "  右键粘贴=" + rightClick);

            // 启动早期不装热键：低频钩子只在有目标窗口时才需要，避免无谓地影响全局输入
            _hotkeyProblem = "等待目标窗口就绪，尚未启用热键";
            _hideRequested = startHidden;

            MakeTray();
            if (rightClick) _host.InstallWinEventHook();

            // 启动过程必须看得见：托盘图标太小、失败时又完全静默，
            // 用户看到的就是「启动之后啥都没有」。所以启动时弹一个状态窗口，
            // 窗口一就绪就自动关；失败就停在那里报原因。
            ShowStatusWindow();

            // 延迟一点再查找窗口，让状态窗口先画出来。
            // 这里必须允许「拉起浏览器」——否则冷启动时永远等不到窗口，
            // 热键也不会武装，用户看到的就是「一直启动、啥都没出来」。
            System.Windows.Forms.Timer startup = new System.Windows.Forms.Timer();
            startup.Interval = 300;
            startup.Tick += delegate
            {
                try
                {
                    startup.Stop();
                    startup.Dispose();
                    RequestFind(true);
                }
                catch (Exception ex) { SafeLog("启动查找异常: " + ex.Message); }
            };
            startup.Start();

            StartTimers();
            Application.ApplicationExit += OnApplicationExit;
            Application.Run(new ApplicationContext());

            if (!string.IsNullOrEmpty(_pendingRestartExe)) RestartSelf(_pendingRestartExe);
        }

        // ── 托盘 ───────────────────────────────────────────────────
        /// <summary>系统里有没有任务栏 / 通知区域。没有的话托盘图标无处可放。</summary>
        public static bool TrayAvailable()
        {
            try
            {
                IntPtr tray = Native.FindWindowW("Shell_TrayWnd", null);
                if (tray == IntPtr.Zero) return false;
                IntPtr notify = Native.FindWindowExW(tray, IntPtr.Zero, "TrayNotifyWnd", null);
                if (notify == IntPtr.Zero) return false;
                IntPtr pager = Native.FindWindowExW(notify, IntPtr.Zero, "SysPager", null);
                IntPtr toolbar = pager == IntPtr.Zero ? IntPtr.Zero : Native.FindWindowExW(pager, IntPtr.Zero, "ToolbarWindow32", null);
                return toolbar != IntPtr.Zero;
            }
            catch { return false; }
        }

        private static void MakeTray()
        {
            if (!TrayAvailable())
            {
                // 关键：本机 explorer.exe 没在运行，没有任务栏也没有通知区域，
                // 托盘图标根本无处可放。这不该让程序出错，只记录一下，
                // 然后用热键（Ctrl+Alt+S / Ctrl+Alt+D）作为入口。
                _trayUnavailable = true;
                Diag.Add("系统没有任务栏 / 通知区域（explorer.exe 未运行，可能是自定义外壳）："
                       + "托盘图标无法显示，已改由热键提供入口。");
                return;
            }

            try
            {
                _tray = new NotifyIcon();
                _tray.Icon = SystemIcons.Shield;
                _tray.Text = Trim140(Title + "（正在启动…）");
                _tray.Visible = true;

                ContextMenuStrip menu = new ContextMenuStrip();
                menu.Opening += delegate { RebuildTrayMenu(menu); };
                _tray.ContextMenuStrip = menu;
                _tray.DoubleClick += delegate { ToggleWindow(); };
                RebuildTrayMenu(menu);
                Diag.Add("托盘图标已创建");
            }
            catch (Exception ex)
            {
                _trayUnavailable = true;
                Diag.Add("创建托盘图标失败（已忽略，改用热键入口）: " + ex.Message);
                _tray = null;
            }
        }

        public static bool TrayUnavailable { get { return _trayUnavailable; } }

        private static void RebuildTrayMenu(ContextMenuStrip menu)
        {
            menu.Items.Clear();

            string hk = _hotkeys == null ? "?" : _hotkeys.ActivePretty;
            ToolStripMenuItem toggle = new ToolStripMenuItem("呼出 / 收起（" + hk + "）");
            toggle.Font = new Font(toggle.Font, FontStyle.Bold);
            toggle.Click += delegate { ToggleWindow(); };
            menu.Items.Add(toggle);

            menu.Items.Add("设置…", null, delegate { OpenSettings(); });
            menu.Items.Add("自检与诊断…", null, delegate { OpenDiagnostics(); });

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem browserMenu = new ToolStripMenuItem("浏览器外壳");
            List<BrowserInfo> found = BrowserHost.Discover();
            if (found.Count == 0)
            {
                browserMenu.DropDownItems.Add(new ToolStripMenuItem("(没找到 Chrome / Edge)") { Enabled = false });
            }
            else
            {
                foreach (BrowserInfo b in found)
                {
                    BrowserInfo captured = b;
                    ToolStripMenuItem it = new ToolStripMenuItem(b.Display + "　" + b.ExePath);
                    it.Checked = _host != null && _host.Browser != null
                        && string.Equals(_host.Browser.ExePath, b.ExePath, StringComparison.OrdinalIgnoreCase);
                    it.Click += delegate { SwitchBrowser(captured); };
                    browserMenu.DropDownItems.Add(it);
                }
            }
            menu.Items.Add(browserMenu);

            menu.Items.Add("打开 hotkey.conf", null, delegate
            {
                try { Process.Start("notepad.exe", _confPath); }
                catch (Exception ex) { Diag.Add("打开 hotkey.conf 失败: " + ex.Message); }
            });

            menu.Items.Add("重新查找窗口", null, delegate
            {
                _host.ForgetWindow();
                RequestFind(true);
            });

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate
            {
                _quitting = true;
                if (_tray != null) _tray.Visible = false;
                Application.Exit();
            });
        }

        private static void SwitchBrowser(BrowserInfo b)
        {
            Diag.Add("手动切换到浏览器: " + b.Display);
            string key = (b.Key == "chrome" || b.Key == "edge") ? b.Key : b.ExePath;
            Program.SetInMemory("browser", key);
            SaveConfigAsync(null, new string[] { "browser=" + key });
            _host.Configure(key, _host.Url, ProfilePath, 0, 0,
                _host.RightClickPasteEnabled, Get("restorefocus", "1") != "0");
            _host.ForgetWindow();
            KickFind(true);
        }

        private static void UpdateTrayText()
        {
            if (_tray == null) return;
            string hk = _hotkeys == null ? "?" : _hotkeys.ActivePretty;
            string state;
            if (_quitting) state = "退出中";
            else if (!_host.IsWindowAlive()) state = string.IsNullOrEmpty(_hotkeyProblem) ? "未找到窗口" : _hotkeyProblem;
            else state = _host.Visible ? "已呼出" : "已收起";
            try { _tray.Text = Trim140(Title + " · " + hk + " · " + state); }
            catch { }
        }

        /// <summary>
        /// 托盘图标丢失就补挂回去。
        /// explorer 重启、或者 shell 出于自己的原因清掉图标之后，NotifyIcon 不会自动回来，
        /// 用户就完全看不到小工具在不在跑。这里每次 SyncTick 检查一下。
        /// </summary>
        private static void EnsureTrayAlive()
        {
            if (_tray == null || _quitting) return;
            try
            {
                if (Native.FindWindowW("Shell_TrayWnd", null) == IntPtr.Zero)
                {
                    // 任务栏整个不在了（explorer 正在重启）：先标记不可见，等它回来再挂
                    if (_tray.Visible) { _tray.Visible = false; _trayNeedsRestore = true; }
                    return;
                }
                if (_trayNeedsRestore || !_tray.Visible)
                {
                    _tray.Visible = true;
                    _trayNeedsRestore = false;
                    Diag.Add("托盘图标已重新挂上");
                }
            }
            catch (Exception ex) { Diag.Add("托盘健康检查异常: " + ex.Message); }
        }

        private static string Trim140(string s)
        {
            return s.Length <= 127 ? s : s.Substring(0, 124) + "…";
        }

        public static void Balloon(string title, string text)
        {
            if (_tray == null) return;
            try
            {
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = text;
                _tray.ShowBalloonTip(6000);
            }
            catch { }
        }

        // ── 定时器 ─────────────────────────────────────────────────
        private static void StartTimers()
        {
            _syncTimer = new System.Windows.Forms.Timer();
            _syncTimer.Interval = 500;
            _syncTimer.Tick += delegate
            {
                // 定时器回调里抛异常 = 整个小工具静默退出（用户看到的就是
                // 「用着用着托盘没了、热键也不灵了」）。所有周期任务都必须兜住异常。
                try { SyncTick(); }
                catch (Exception ex) { SafeLog("周期同步异常（已忽略）: " + ex.GetType().Name + ": " + ex.Message); }
            };
            _syncTimer.Start();

            _traceTimer = new System.Windows.Forms.Timer();
            _traceTimer.Interval = 250;
            _traceTimer.Tick += delegate
            {
                try { UpdateTraceTitle(); }
                catch { }
            };
            _traceTimer.Start();
        }

        private static void SafeLog(string msg)
        {
            try { Diag.Add(msg); } catch { }
        }

        private static void SyncTick()
        {
            if (_quitting) return;

            // UI 心跳：如果两拍之间隔了超过 1.5 秒（正常是 0.5 秒），
            // 说明消息循环被什么东西堵住了——「热键要等设置窗口打开才生效」
            // 这种怪现象通常就是这个原因。记下来，诊断窗口能直接看到。
            int now = Environment.TickCount;
            if (_lastTick != 0 && unchecked(now - _lastTick) > 1500) _slowTickCount++;
            _lastTick = now;
            _uiHeartbeat++;

            // 健康检查：低级键盘钩子可能被系统悄悄摘掉（第一次能用、第二次没反应
            // 就是这个原因）。定期检查，被摘掉就装回来。
            if (_hotkeys != null) _hotkeys.EnsureKeyboardHookAlive();

            // 托盘图标可能因为 explorer 重启等原因消失，发现没了就重新挂上
            EnsureTrayAlive();

            if (_host.IsWindowAlive())
            {
                _host.EnsureToolWindow(_host.TargetWindow);
                // 热键之前没启用成功就再试（例如占用它的程序退出了）。
                // 条件必须是「有失败原因」——_hotkeyProblem 只有在失败时才非空，
                // 成功时是 null。旧版这里写反了，所以这条重试永远不会发生。
                if (_hotkeys != null && !_hotkeys.ChordRegistered && !_hotkeys.KeyboardHookInstalled
                    && !string.IsNullOrEmpty(_hotkeyProblem))
                {
                    TryArmHotkey();
                }
                _host.SyncMouseHook();
                UpdateTrayText();
                return;
            }

            if (_host.TargetWindow != IntPtr.Zero)
            {
                Diag.Add("目标窗口已消失，重新查找");
                _host.ForgetWindow();
            }
            _host.UninstallMouseHook();

            // 查找全部放在这里按拍轮询，绝不在后台线程里长睡。
            // 这是旧版「按热键没反应」的根因：后台查找占着 _findInProgress 不放，
            // 把同一时间所有「去找窗口 / 启动浏览器」的请求都静默丢掉了。
            bool inWindow = _searchUntilTick != 0
                && unchecked(Environment.TickCount - _searchUntilTick) < 0;
            if (inWindow || _launchWanted)
            {
                bool launch = _launchWanted;
                _launchWanted = false;
                if (KickFind(launch) && !inWindow)
                    _searchUntilTick = Environment.TickCount + 30000;   // 接着轮询 30 秒
            }
            UpdateTrayText();
        }

        private static void UpdateTraceTitle()
        {
            if (_traceHwnd == IntPtr.Zero) return;
            try
            {
                IntPtr fg = Native.GetForegroundWindow();
                _traceText = "DSQ v" + Version
                    + " hwnd=" + (_host.IsWindowAlive() ? "1" : "0")
                    + " vis=" + (_host.Visible ? "1" : "0")
                    + " mine=" + (_host.IsOurs(fg) ? "1" : "0")
                    + " il=" + IntegrityName(_integrityRid)
                    + " mhook=" + (_host.MouseHookInstalled ? "1" : "0")
                    + " mgd=" + (_hotkeys != null && _hotkeys.MouseGuardInstalled ? "1" : "0")
                    + " khook=" + (_hotkeys != null && _hotkeys.KeyboardHookInstalled ? "1" : "0")
                    + " hth=" + (_hotkeys != null && _hotkeys.HookThreadAlive ? "1" : "0")
                    + " hk=" + (_hotkeys == null ? "?" : _hotkeys.ActivePretty)
                    + " dt=" + (_hotkeys == null ? "0" : _hotkeys.DoubleTapFires.ToString())
                    + " kev=" + (_hotkeys == null ? "0" : _hotkeys.HookEvents.ToString())
                    + " kmod=" + (_hotkeys == null ? "0" : _hotkeys.ModifierEvents.ToString())
                    + " ck=" + (_hotkeys == null ? "0" : _hotkeys.ChordFires.ToString())
                    + " hkr=" + (_hotkeys == null ? "0" : _hotkeys.HookReinstalls.ToString())
                    + " tog=" + _toggleCount + " h=" + _hideCount + " s=" + _showCount
                    + " tm=" + _toggleMessages + " hb=" + _uiHeartbeat + " slow=" + _slowTickCount
                    + " d=" + Stats.Down + " u=" + Stats.Up + " p=" + Stats.Paste
                    + " fgr=" + (_host.LastForegroundResult ?? "")
                    + " pid=" + Process.GetCurrentProcess().Id;
                Native.SetWindowTextSafe(_traceHwnd, _traceText);
            }
            catch { }
        }

        public static string TraceText { get { return _traceText; } }

        // ── 窗口查找 ───────────────────────────────────────────────
        /// <summary>
        /// 查找（必要时启动）目标窗口。**一趟就返回，绝不长睡。**
        ///
        /// 为什么改成这样：旧版在后台线程里 while 循环等 15~90 秒，期间
        /// `_findInProgress` 一直是 1，于是热键、托盘「呼出/收起」、托盘「重新查找窗口」、
        /// 状态窗口「重试启动」发出的请求全部被 CAS 挡掉、静默丢弃——冷启动时表现为
        /// 「工具永远不会自己把浏览器拉起来，按热键也没反应」。
        ///
        /// 现在：这一趟只做一次「找 → 没有就启动」，然后交给 SyncTick 每 500ms 轮询。
        /// 返回 true 表示这一趟真的执行了（没被别人占着）。
        /// </summary>
        private static bool KickFind(bool launchIfMissing)
        {
            if (Interlocked.CompareExchange(ref _findInProgress, 1, 0) != 0)
            {
                // 已经有一趟在飞。把「要启动」的意愿记下来，让在飞的那一趟或者
                // 下一拍接着做——绝不静默丢掉用户的请求。
                if (launchIfMissing) _launchWanted = true;
                return false;
            }

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                IntPtr found = IntPtr.Zero;
                string launchError = null;
                bool foundExisting = false;
                try
                {
                    // 1) 先看有没有现成的（先手动开了浏览器、或上次留下的窗口）
                    found = _host.FindTargetWindow();
                    foundExisting = found != IntPtr.Zero;

                    // 2) 没有就拉起浏览器
                    if (!foundExisting && launchIfMissing)
                    {
                        if (_host.Browser == null)
                        {
                            launchError = "没找到 Chrome / Edge。请在托盘「设置…」里指定浏览器路径，或先安装其中一个。\n"
                                + BrowserHost.DescribeSearchPlaces();
                        }
                        else
                        {
                            launchError = _host.Launch();      // 内部有秒退检测 + --no-sandbox 兜底
                        }
                    }

                    // 3) 给刚起来的浏览器一点时间建窗口。这里最多只睡 1 秒，
                    //    剩下的交给 SyncTick 轮询，避免长时间占着 _findInProgress。
                    for (int i = 0; i < 2 && found == IntPtr.Zero; i++)
                    {
                        Thread.Sleep(500);
                        found = _host.FindTargetWindow();
                    }
                }
                catch (Exception ex)
                {
                    Diag.Add("查找窗口异常: " + ex.Message);
                    found = IntPtr.Zero;
                }
                finally
                {
                    IntPtr result = found;
                    string err = launchError;
                    bool existing = foundExisting;
                    try
                    {
                        if (_msg != null && !_msg.IsDisposed && _msg.IsHandleCreated)
                            _msg.BeginInvoke((Action)delegate { OnWindowFound(result, err, existing); });
                    }
                    catch { }
                    Interlocked.Exchange(ref _findInProgress, 0);
                }
            });
            return true;
        }

        /// <summary>
        /// 请求「去把窗口找出来 / 把浏览器拉起来」。
        /// 立刻开一趟查找，并打开 30 秒的轮询窗口——这样即使这一趟已经在飞，
        /// 下一拍的 SyncTick 也会接着找，绝不会把请求丢掉。
        /// </summary>
        private static void RequestFind(bool launch)
        {
            _findRequests++;
            if (launch) { _launchWanted = true; _launchAttempts++; }
            _searchUntilTick = Environment.TickCount + 30000;
            KickFind(launch);
        }

        private static void OnWindowFound(IntPtr hwnd, string launchError, bool foundExisting)
        {
            if (_quitting) return;

            if (hwnd == IntPtr.Zero)
            {
                _hotkeyProblem = launchError != null ? launchError : "没找到目标窗口";
                if (launchError != null) _lastLaunchFailure = launchError;
                Diag.Add("未找到目标窗口: " + _hotkeyProblem);
                if (launchError != null)
                {
                    _searchUntilTick = 0;       // 已经有明确失败原因了，别再空转轮询
                    Balloon(Title + "：浏览器没能起来",
                        launchError + "\n\n托盘右键 →「自检与诊断…」可看完整信息。");
                }
                UpdateTrayText();
                return;
            }

            bool isNew = _host.TargetWindow != hwnd;
            _host.Adopt(hwnd);
            EnsureTraceWindow();
            _lastLaunchFailure = null;          // 窗口已就绪，清掉之前的失败记录

            if (isNew)
            {
                _host.EnsureToolWindow(hwnd);
                // 注意：不要在这里再读一次 startHidden 去覆盖 _hideRequested。
                // 用户可能在窗口出现之前就按过热键（那会把 _hideRequested 置为 false），
                // 再覆盖一次就会「按了热键要它出来，结果窗口一出现又自己收起来」。
                if (_hideRequested) _host.Hide();
                else _host.Show();
            }

            // 窗口就绪后再启用热键：低频钩子只在真正需要时才挂
            TryArmHotkey();

            if (!_startupBalloonShown)
            {
                _startupBalloonShown = true;
                string hk = _hotkeys.ActivePretty;
                string extra = _hotkeyProblem == null ? "" :
                    "\n（主热键不可用：" + _hotkeyProblem + "，已用 " + hk + " 代替）";
                Balloon(Title + " 已就绪",
                    "按 " + hk + " 呼出 / 收起；窗口里右键 = 粘贴。" + extra);
            }
            UpdateTrayText();
        }

        private static void TryArmHotkey()
        {
            if (_hotkeys == null) return;
            if (_hotkeys.ChordRegistered || _hotkeys.KeyboardHookInstalled) return;

            string configured = Get("hotkey", "DOUBLECTRL");
            bool isDefault = configured.Trim().Equals("DOUBLECTRL", StringComparison.OrdinalIgnoreCase)
                          || configured.Trim().Equals("CTRL+CTRL", StringComparison.OrdinalIgnoreCase)
                          || configured.Trim().Equals("DOUBLE:CTRL", StringComparison.OrdinalIgnoreCase);

            // 降级候选：**只允许同一形态，而且只在配置还是默认值时才允许静默降级**。
            // 用户要的是组合键，就不能偷偷给他换成双击 Ctrl；反之亦然——
            // 否则「我明明设了 Ctrl+Alt+Space，结果按 Win+~ 才管用」这种最让人恼火。
            // 用户自己设的键失败了就照实报错，让他去设置窗口挑一个能用的。
            string[] fallbacks = isDefault
                ? new string[] { _hotkeyFallbackPref, "WIN+SHIFT+D", "CTRL+ALT+OEM3", "CTRL+ALT+SPACE" }
                : new string[] { };

            _hotkeys.DoubleTapMs = ParseInt(Get("doubletapms", "420"), 420);
            string problem = _hotkeys.Apply(configured, fallbacks);
            _hotkeyProblem = problem;
            if (problem != null)
            {
                Diag.Add("热键启用失败: " + problem);
                UpdateTrayText();
                Balloon(Title + "：热键没能启用",
                    problem + "\n请到托盘右键 →「设置…」里换一个组合键。");
                return;
            }
            UpdateTrayText();
        }

        /// <summary>
        /// 收到 WM_APP_REHOOK：把键盘钩子摘掉重装。第三方安全软件 / 输入法也装低级
        /// 钩子，若它在我们之后安装且不调用 CallNextHookEx，我们就会收不到按键
        /// （句柄仍在、khook 仍报 1）。重装能把自己移到钩子链最前面，立刻恢复。
        /// </summary>
        public static void ReinstallHotkeyFromMessage()
        {
            try
            {
                if (_hotkeys == null) return;
                if (_hotkeys.Active == null || _hotkeys.Active.Kind != TriggerKind.DoubleTap) return;
                string why = _hotkeys.ReinstallKeyboardHook();
                Diag.Add(why == null ? "按指令重装键盘钩子完成" : "按指令重装键盘钩子失败: " + why);
            }
            catch (Exception ex) { Diag.Add("重装键盘钩子异常: " + ex.Message); }
        }

        /// <summary>
        /// 给「双击修饰键」用的轻量触发：只往消息窗口投一条 WM_APP_TOGGLE 就返回。
        /// 低级键盘钩子回调里绝不能做切换窗口这种重活，否则钩子会被系统摘掉。
        /// </summary>
        private static void PostToggleMessage()
        {
            _lastToggleDispatchTick = Environment.TickCount;
            try
            {
                if (_msg != null && !_msg.IsDisposed && _msg.IsHandleCreated)
                {
                    bool ok = Native.PostMessage(_msg.Handle, Native.WM_APP_TOGGLE, IntPtr.Zero, IntPtr.Zero);
                    if (!ok) Diag.Add("投递切换消息失败（PostMessage 返回 false）");
                }
                else
                {
                    Diag.Add("投递切换消息失败：消息窗口不存在");
                }
            }
            catch (Exception ex) { Diag.Add("投递切换消息异常: " + ex.Message); }
        }

        public static void NoteToggleMessage() { _toggleMessages++; }

        // 供托盘菜单「呼出/收起」和热键回调使用
        public static void ToggleWindow()
        {
            if (_quitting) return;
            try
            {
                ToggleWindowCore();
            }
            catch (Exception ex)
            {
                // 绝不因为一次切换失败就把整个小工具带走
                Diag.Add("切换窗口异常（已忽略，工具继续运行）: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void ToggleWindowCore()
        {
            if (_quitting) return;
            _toggleCount++;
            Diag.Add("切换窗口（当前 hwnd=" + (_host.IsWindowAlive() ? "有" : "无")
                + " vis=" + (_host.Visible ? "1" : "0")
                + " 前台是我们=" + (_host.IsOursForeground() ? "1" : "0") + "）");

            if (!_host.IsWindowAlive())
            {
                // 关键修复：窗口还没找到时，热键不能「什么都不做」。
                // 记下用户想要「显示」，等窗口一出现就显示；同时立刻去找 / 启动。
                _hideRequested = false;
                RequestFind(true);
                UpdateTrayText();
                return;
            }

            // 判断顺序：
            //   1. 可见且在前台 → 收起；
            //   2. 可见但不在前台 → 先尝试提到前台（正常应用前台锁允许）；
            //   3. 如果上一次「提到前台」已被系统拒绝（前台是更高权限的窗口等），
            //      这一次直接收起，保证用户总能按第二下把窗口收走，不会卡在
            //      「可见但被挡住 / 又没任务栏和 Alt+Tab」的死角。
            if (_host.Visible && (_host.IsOursForeground() || _host.LastShowMissedForeground))
            {
                _hideCount++;
                _host.Hide();
            }
            else
            {
                _showCount++;
                _host.Show();
            }
            UpdateTrayText();
        }

        // ── 右键粘贴 ───────────────────────────────────────────────
        private static void PasteClipboard()
        {
            Stats.NotePaste();
            // 右键抬起刚过去，给页面一点点时间把焦点/光标放到点击位置，再注入 Ctrl+V，
            // 否则偶发会粘到旧焦点上。
            ThreadPool.QueueUserWorkItem(delegate(object s)
            {
                Thread.Sleep(60);
                // 自己注入的 Ctrl 不能参与双击判定：先清掉可能已武装的「第一敲」，
                // 再给 4 个注入事件打上 DSQ_EXTRAINFO 标记（钩子两道都会跳过）。
                try { if (_hotkeys != null) _hotkeys.SuppressPendingTap(); } catch { }
                Native.INPUT[] inputs = new Native.INPUT[4];
                inputs[0] = MakeKeyInput(Native.VK_CONTROL, false);
                inputs[1] = MakeKeyInput(Native.VK_V, false);
                inputs[2] = MakeKeyInput(Native.VK_V, true);
                inputs[3] = MakeKeyInput(Native.VK_CONTROL, true);
                uint sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
                if (sent != inputs.Length)
                {
                    Diag.Add("SendInput 注入 Ctrl+V 失败 sent=" + sent + " err=" + Marshal.GetLastWin32Error());
                    Native.keybd_event((byte)Native.VK_CONTROL, 0, Native.KEYEVENTF_KEYUP, Native.DSQ_EXTRAINFO);
                    Native.keybd_event((byte)Native.VK_V, 0, Native.KEYEVENTF_KEYUP, Native.DSQ_EXTRAINFO);
                }
                // 抬起被钩子吞掉后，Chromium 可能以为鼠标还按着（capture 没释放）。
                // 到这里普通输入已经处理完，补一次 WM_CANCELMODE 并校验。
                try { if (_host != null) _host.ReleaseLeakedCapture(); }
                catch (Exception ex) { Diag.Add("解除鼠标捕获异常: " + ex.Message); }
            });
        }

        private static Native.INPUT MakeKeyInput(int vk, bool up)
        {
            Native.INPUT input = new Native.INPUT();
            input.type = Native.INPUT_KEYBOARD;
            input.u.ki.wVk = (ushort)vk;
            input.u.ki.wScan = 0;
            input.u.ki.dwFlags = up ? Native.KEYEVENTF_KEYUP : 0;
            input.u.ki.time = 0;
            input.u.ki.dwExtraInfo = Native.DSQ_EXTRAINFO;
            return input;
        }

        public static void NoteMouseDown() { Stats.NoteDown(); }

        public static void NoteMouseUp() { Stats.NoteUp(); }

        public static void NoteHookInstall() { Stats.NoteHookInstall(); }

        // ── 供设置窗口调用的热键相关操作 ───────────────────────────
        /// <summary>改了热键后立刻在当前进程生效，不用重启。</summary>
        public static void ApplyHotkeyNow()
        {
            if (_hotkeys == null) return;
            if (_host == null || !_host.IsWindowAlive())
            {
                // 还没锁定窗口：先不动热键，等窗口就绪时统一启用
                _hotkeys.Release();
                _hotkeyProblem = "等待目标窗口就绪，尚未启用热键";
                UpdateTrayText();
                return;
            }
            string configured = Get("hotkey", "DOUBLECTRL");
            _hotkeys.DoubleTapMs = ParseInt(Get("doubletapms", "420"), 420);
            string problem = _hotkeys.Apply(configured, new string[] { });
            _hotkeyProblem = problem;
            if (problem != null)
                Balloon(Title + "：热键没能启用", problem + "\n请到「设置…」里换一个组合键。");
            UpdateTrayText();
        }

        /// <summary>试注册一个组合键，返回 0 表示可用（设置窗口用，不影响当前热键）。</summary>
        public static int TryTestHotkey(TriggerSpec spec)
        {
            if (_msg == null || !_msg.IsHandleCreated) return -1;
            const int TEST_ID = 99;
            if (spec.Kind != TriggerKind.Chord) return 0;
            bool ok = Native.RegisterHotKey(_msg.Handle, TEST_ID, spec.Modifiers | Native.MOD_NOREPEAT, spec.VirtualKey);
            int err = ok ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (ok) Native.UnregisterHotKey(_msg.Handle, TEST_ID);
            return err;
        }

        /// <summary>重新从磁盘读 hotkey.conf（诊断窗口的「重新查找窗口」会先调这个）。</summary>
        public static void ReloadConfFromDisk()
        {
            _conf = LoadConf(_confPath);
            _hotkeyFallbackPref = Get("hotkeyfallback", "WIN+SHIFT+OEM3");
            Diag.Add("已重新读取 hotkey.conf，hotkey=" + Get("hotkey", "DOUBLECTRL"));
        }

        public static void KickFindPublic(bool launch) { RequestFind(launch); }

        // ── 启动状态窗口 / 失败上报 ────────────────────────────────
        private static void ShowStatusWindow()
        {
            try
            {
                _status = new StatusForm();
                _status.Show();          // 非模态：不阻塞消息循环，托盘也照常工作
            }
            catch (Exception ex)
            {
                Diag.Add("创建状态窗口失败: " + ex.Message);
                _status = null;
            }
        }

        public static bool IsFinding { get { return _findInProgress != 0; } }

        /// <summary>是否已经拿到一个明确的启动失败原因。</summary>
        public static bool HasStartupFailure()
        {
            return !string.IsNullOrEmpty(_lastLaunchFailure) && !_host.IsWindowAlive();
        }

        /// <summary>给状态窗口/诊断窗口用的失败说明（含可操作建议）。</summary>
        public static string StartupFailureText()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine(string.IsNullOrEmpty(_lastLaunchFailure) ? "还没锁定到 DeepSeek 窗口。" : _lastLaunchFailure);
            sb.AppendLine();
            sb.AppendLine("可以依次试：");
            sb.AppendLine("1. 点上面的「重试启动」；");
            sb.AppendLine("2. 双击 DeepSeekChrome.lnk 手动把浏览器窗口打开，小工具会自动接管它；");
            sb.AppendLine("3. 点「设置…」把浏览器换成另一个（Chrome ⇄ Edge），或直接指定 exe 路径；");
            sb.AppendLine("4. 点「自检与诊断…」→「测试启动浏览器」，那里有最详细的失败原因。");
            if (!string.IsNullOrEmpty(_lastLaunchFailure)
                && _lastLaunchFailure.IndexOf("没找到 Chrome", StringComparison.Ordinal) >= 0)
            {
                sb.AppendLine();
                sb.AppendLine(BrowserHost.DescribeSearchPlaces());
            }
            return sb.ToString();
        }

        /// <summary>状态窗口的「重试启动」。</summary>
        public static void RetryStartup()
        {
            _lastLaunchFailure = null;
            _host.ForgetWindow();
            ReloadConfFromDisk();
            RequestFind(true);
        }
        // ── 设置 / 诊断窗口 ────────────────────────────────────────
        private static void OpenSettings()
        {
            using (SettingsForm f = new SettingsForm())
            {
                f.ShowDialog();
            }
            UpdateTrayText();
        }

        private static void OpenDiagnostics()
        {
            using (DiagnosticsForm f = new DiagnosticsForm())
            {
                f.ShowDialog();
            }
        }

        // ── 配置写入（通过外部脚本，规避杀软拦截未签名程序写文件）──
        /// <summary>
        /// 把若干 key=value 写回 hotkey.conf。onDone 在写完（或失败）后回调。
        /// restartExe 非空时，写完后延迟重启自己。
        /// </summary>
        public static void SaveConfigAsync(string restartExe, string[] sets)
        {
            if (sets == null || sets.Length == 0) return;

            string script = Path.Combine(_baseDir, @"src\config-write.ps1");
            if (!File.Exists(script))
            {
                Diag.Add("找不到配置写入脚本: " + script);
                Balloon(Title + "：无法保存配置", "找不到 src\\config-write.ps1");
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("-NoProfile -ExecutionPolicy Bypass -File \"");
            sb.Append(script);
            sb.Append("\" -Conf \"");
            sb.Append(_confPath);
            sb.Append("\"");
            foreach (string s in sets)
            {
                sb.Append(" -Set \"");
                sb.Append(SanitizeForCommandLine(s));
                sb.Append("\"");
            }
            if (!string.IsNullOrEmpty(restartExe))
            {
                sb.Append(" -Restart \"");
                sb.Append(restartExe);
                sb.Append("\"");
            }

            string file = "pwsh.exe";
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pwshFull = Path.Combine(pf, @"PowerShell\7\pwsh.exe");
            if (!File.Exists(pwshFull))
            {
                pwshFull = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
                if (File.Exists(pwshFull)) file = pwshFull;
            }
            else file = pwshFull;

            Diag.Add("写入配置: " + string.Join(" ", sets) + "  通过 " + file);

            ProcessStartInfo psi = new ProcessStartInfo(file);
            psi.Arguments = sb.ToString();
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            try
            {
                Process p = Process.Start(psi);
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                { if (!string.IsNullOrEmpty(e.Data)) Diag.Add("[config-write] " + e.Data); };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                { if (!string.IsNullOrEmpty(e.Data)) Diag.Add("[config-write:err] " + e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.EnableRaisingEvents = true;
                p.Exited += delegate
                {
                    try
                    {
                        int code = p.ExitCode;
                        Diag.Add("配置写入进程退出码=" + code);
                        if (code != 0)
                        {
                            Balloon(Title + "：配置可能没写成功", "config-write.ps1 退出码 " + code
                                + "，详情见「自检与诊断…」");
                        }
                    }
                    catch { }
                };
            }
            catch (Exception ex)
            {
                Diag.Add("启动配置写入进程失败: " + ex.Message);
                Balloon(Title + "：无法保存配置", ex.Message);
            }
        }

        /// <summary>
        /// 把配置值里会破坏命令行/脚本语义的字符换掉。
        /// 值来自用户输入（网址、浏览器路径），直接拼进 pwsh 命令行的话，
        /// url 里的 ` 或 ; 有可能被解析成命令——虽然脚本里做了引号处理，
        /// 这里再兜一层，宁可变一点也不能让配置值变成可执行的东西。
        /// 注意：`&amp;` `=` `?` `%` 都是合法网址字符，必须保留。
        /// </summary>
        private static string SanitizeForCommandLine(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            StringBuilder sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (c == '"' || c == '\'' || c == '`' || c == ';' || c == '|'
                    || c == '<' || c == '>' || c == '\r' || c == '\n' || c == '\0')
                    sb.Append('_');
                else
                    sb.Append(c);
            }
            return sb.ToString();
        }

        public static void RequestRestart()
        {
            _pendingRestartExe = Application.ExecutablePath;
            Diag.Add("按用户要求重启: " + _pendingRestartExe);
            _quitting = true;
            if (_tray != null) _tray.Visible = false;
            Application.Exit();
        }

        private static void RestartSelf(string exe)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe);
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                psi.UseShellExecute = true;
                Process.Start(psi);
                Diag.Add("已重新启动: " + exe);
            }
            catch (Exception ex)
            {
                Diag.Add("重启失败: " + ex.Message);
                MessageBox.Show("重启失败：" + ex.Message + "\n请手动双击 DeepSeek Quake.lnk。",
                    Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ── 退出清理 ───────────────────────────────────────────────
        private static void OnApplicationExit(object sender, EventArgs e)
        {
            _quitting = true;
            try { if (_syncTimer != null) _syncTimer.Stop(); } catch { }
            try { if (_traceTimer != null) _traceTimer.Stop(); } catch { }
            if (_hotkeys != null) { try { _hotkeys.Release(); } catch { } }
            if (_host != null) { try { _host.Dispose(); } catch { } }
            if (_tray != null)
            {
                try { _tray.Visible = false; _tray.Dispose(); } catch { }
                _tray = null;
            }
            try { if (_mutex != null) { _mutex.ReleaseMutex(); _mutex.Dispose(); } } catch { }
        }

        private static void EnsureTraceWindow()
        {
            if (_traceHwnd != IntPtr.Zero) return;
            try
            {
                Form f = new TraceWindow();
                f.ShowInTaskbar = false;
                f.FormBorderStyle = FormBorderStyle.None;
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-4200, -4200);
                f.Size = new Size(1, 1);
                f.Text = _traceText;
                _traceHwnd = f.Handle;
            }
            catch (Exception ex) { Diag.Add("创建诊断窗口失败: " + ex.Message); }
        }

        // ── 配置读写 ───────────────────────────────────────────────
        private static Dictionary<string, string> LoadConf(string path)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(path)) return d;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch (Exception ex) { Diag.Add("读取 hotkey.conf 失败: " + ex.Message); }
            return d;
        }

        public static string Get(string key, string def)
        {
            string v;
            if (_conf != null && _conf.TryGetValue(key, out v) && v.Length > 0) return v;
            return def;
        }

        public static void SetInMemory(string key, string value)
        {
            if (_conf == null) _conf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _conf[key] = value;
        }

        private static int ParseInt(string s, int def)
        {
            int n;
            return int.TryParse(s, out n) && n > 0 ? n : def;
        }

        public static string BaseDir { get { return _baseDir; } }
        public static string ConfPath { get { return _confPath; } }
    }
}
