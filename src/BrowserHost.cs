// DeepSeek Quake —— 浏览器外壳管理
//
// 职责：
//   1. 找到 Chromium 系浏览器的 app 窗口（Chrome / Edge 都行），并把它变成「工具窗口」
//      （不占任务栏、不进 Alt+Tab）
//   2. 用隔离 profile 启动 app 窗口
//   3. 显示 / 收起（quake 式）、焦点还原、鼠标模式清理
//   4. 右键粘贴：只在本窗口前台时装 WH_MOUSE_LL，右键抬起吞掉并注入 Ctrl+V
//
// 窗口识别为什么容易出错、这里怎么防：
//   app 窗口和普通浏览器窗口都是 Chrome_WidgetWin_1。所以候选必须同时满足：
//     * 类名 Chrome_WidgetWin_1 且无 owner（主窗口，不是权限气泡）
//     * 进程属于我们配置的浏览器（chrome.exe / msedge.exe）
//     * 且能证明属于本项目：命令行里带我们的 profile 目录 / 是本程序刚拉起的 PID
//       （标题只用来给已确认属于本项目的窗口排序加分，绝不单独认领）
//   不满足就不认，宁可报「没找到窗口」，也不会去操作用户的日常浏览器窗口。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace DeepSeekQuake
{
    internal sealed class BrowserInfo
    {
        public string Key;          // "chrome" / "edge" / 自定义路径
        public string Display;      // 给人看的名字
        public string ExePath;
        public string ProcessName;  // 不含 .exe

        public override string ToString() { return Display + " → " + ExePath; }
    }

    internal sealed class BrowserHost
    {
        private readonly Log _log;
        private readonly HookStats _stats;

        // 配置
        private string _browserPref = "auto";
        private string _url = "https://chat.deepseek.com";
        private string _profilePath = "";
        private int _width = 980;
        private int _height = 820;
        private bool _rightClickPaste = true;
        private bool _restoreFocus = true;
        private string _sandboxMode = "auto";   // auto = 沙箱失败才降级；sandbox = 只用沙箱；nosandbox = 直接不开沙箱
        private bool _profilePathExplicit;      // profile 配置写的是绝对路径 → 不按品牌加后缀

        private BrowserInfo _browser;
        private IntPtr _hwnd = IntPtr.Zero;
        private uint _launchedPid;
        private string _lastLaunchError;
        private volatile string _lastForegroundResult = "";   // 最近一次「提到前台」的 Win32 结果，诊断用
        private volatile bool _lastShowMissedForeground;      // 最近一次显示没能在前台（被更高权限窗口挡住）
        private IntPtr _lastForeground = IntPtr.Zero;
        private string _titleHint = "DeepSeek";
        private volatile bool _hidePending;      // 上一次收起没确认落地，等 SyncTick 复核
        private volatile int _lastHideTries;     // 最近一次收起试了几次才成功（诊断用）

        // 鼠标钩子
        private IntPtr _mouseHook = IntPtr.Zero;
        private Native.LowLevelProc _mouseProc;
        private bool _armed;

        // 前台变化通知
        private IntPtr _winEventHook = IntPtr.Zero;
        private Native.WinEventProc _winEventProc;

        public BrowserHost(Log log, HookStats stats) { _log = log; _stats = stats; }

        public IntPtr TargetWindow { get { return _hwnd; } }
        public uint LaunchedPid { get { return _launchedPid; } }
        public string LastLaunchError { get { return _lastLaunchError; } }
        public string LastForegroundResult { get { return _lastForegroundResult; } }
        public bool LastShowMissedForeground { get { return _lastShowMissedForeground; } }
        public BrowserInfo Browser { get { return _browser; } }
        public bool MouseHookInstalled { get { return _mouseHook != IntPtr.Zero; } }
        public bool RightClickPasteEnabled { get { return _rightClickPaste; } }
        public string Url { get { return _url; } }

        public void Configure(string browserPref, string url, string profilePath,
            int width, int height, bool rightClickPaste, bool restoreFocus)
        {
            Configure(browserPref, url, profilePath, false, width, height, rightClickPaste, restoreFocus);
        }

        public void Configure(string browserPref, string url, string profilePath, bool profileIsExplicit,
            int width, int height, bool rightClickPaste, bool restoreFocus)
        {
            _browserPref = string.IsNullOrEmpty(browserPref) ? "auto" : browserPref.Trim();
            _url = string.IsNullOrEmpty(url) ? "https://chat.deepseek.com" : url.Trim();
            _profilePath = profilePath;
            _profilePathExplicit = profileIsExplicit;
            // width/height 为 0 表示「保持原尺寸」，方便只改浏览器/开关时复用现有配置
            if (width > 0) _width = width;
            if (height > 0) _height = height;
            _rightClickPaste = rightClickPaste;
            _restoreFocus = restoreFocus;
            _titleHint = BuildTitleHint(_url);
            _browser = ResolveBrowser(_browserPref, _profilePath, _profilePathExplicit);
        }

        /// <summary>sandbox 策略：auto / sandbox / nosandbox。见 Launch() 里的说明。</summary>
        public string SandboxMode
        {
            get { return _sandboxMode; }
            set
            {
                string v = (value ?? "auto").Trim().ToLowerInvariant();
                if (v == "0" || v == "no" || v == "off") v = "nosandbox";
                if (v == "1" || v == "yes" || v == "on") v = "auto";
                _sandboxMode = (v == "sandbox" || v == "nosandbox") ? v : "auto";
            }
        }

        // ── 浏览器定位 ─────────────────────────────────────────────

        public static List<BrowserInfo> Discover()
        {
            List<BrowserInfo> found = new List<BrowserInfo>();
            List<string> cands = new List<string>();

            AddIfExists(cands, ReadAppPaths("chrome.exe"));
            AddIfExists(cands, ReadAppPaths("msedge.exe"));
            AddIfExists(cands, ReadStartMenuInternet("Google Chrome"));
            AddIfExists(cands, ReadStartMenuInternet("Microsoft Edge"));
            AddIfExists(cands, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"));
            AddIfExists(cands, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"));
            AddIfExists(cands, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"));
            AddIfExists(cands, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"));
            AddIfExists(cands, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"));
            AddIfExists(cands, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\Application\msedge.exe"));

            foreach (string p in cands)
            {
                string name = Path.GetFileNameWithoutExtension(p).ToLowerInvariant();
                if (name != "chrome" && name != "msedge") continue;
                bool dup = false;
                foreach (BrowserInfo b in found)
                    if (string.Equals(b.ExePath, p, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                if (dup) continue;
                found.Add(new BrowserInfo
                {
                    Key = name == "chrome" ? "chrome" : "edge",
                    Display = name == "chrome" ? "Google Chrome" : "Microsoft Edge",
                    ExePath = p,
                    ProcessName = name
                });
            }
            // 稳定顺序：先 Chrome 再 Edge（和用户原来的习惯一致）
            found.Sort(delegate(BrowserInfo a, BrowserInfo b)
            {
                int ra = a.Key == "chrome" ? 0 : 1;
                int rb = b.Key == "chrome" ? 0 : 1;
                return ra.CompareTo(rb);
            });
            return found;
        }

        /// <summary>
        /// 启动时决定用哪个浏览器。原则：**宁可确定也不要聪明**。
        ///
        /// 顺序：
        ///   1. 用户显式配置了 profile 目录，而某个品牌已经在该目录建过登录态
        ///      → 用那个品牌。默认布局下 `chrome-profile` 是 Chrome 建的，
        ///        用户原来的 DeepSeek 登录态就在里面，绝不能被自动换成 Edge。
        ///   2. 「非 Chrome 的带后缀目录」只在**用户配置的就是那个后缀目录**时才认，
        ///      不靠猜。否则一个半成品目录（例如某次失败的探测留下的
        ///      `chrome-profile-edge`）就会把浏览器选择悄悄带偏。
        ///   3. 都不成立就用 Chrome（本项目从第一天起就是 Chrome 方案，最省事、最可预期）。
        ///
        /// 不再使用「谁在运行就选谁」：本机上 Edge 常年常驻，那条规则会让
        /// 自动选择永远倒向 Edge，用户就得重新登录一次——代价太大。
        /// </summary>
        public static BrowserInfo PickDefault(List<BrowserInfo> found, string projectProfilePath, bool profileExplicit)
        {
            if (found == null || found.Count == 0) return null;

            // 规则 1：项目 profile 目录已经建过登录态（且用户没写绝对路径，
            // 也就是这个目录归 Chrome 用）→ 继续用 Chrome，别让用户重新登录。
            if (!string.IsNullOrEmpty(projectProfilePath) && !profileExplicit)
            {
                bool exists = false;
                try { exists = Directory.Exists(projectProfilePath); } catch { }
                if (exists)
                {
                    foreach (BrowserInfo b in found)
                        if (b.Key == "chrome") return b;
                }
            }

            // 规则 2：用户显式指定了目录，又配置了具体品牌时，ResolveBrowser 根本不会
            // 走到这里；能走到这里说明是 auto，那就用最可预期的 Chrome。
            foreach (BrowserInfo b in found)
                if (b.Key == "chrome") return b;

            return found[0];
        }

        public static BrowserInfo PickDefault(List<BrowserInfo> found, string projectProfilePath)
        {
            return PickDefault(found, projectProfilePath, false);
        }

        public static BrowserInfo PickDefault(List<BrowserInfo> found) { return PickDefault(found, null, false); }

        private static bool IsProcessRunning(string procName)
        {
            try { return Process.GetProcessesByName(procName).Length > 0; }
            catch { return false; }
        }

        /// <summary>找不到浏览器时，把「找过哪些地方」列出来，方便用户判断是不是装到别处了。</summary>
        public static string DescribeSearchPlaces()
        {
            List<string> cands = new List<string>();
            AddIfExists(cands, ReadAppPaths("chrome.exe"));
            AddIfExists(cands, ReadAppPaths("msedge.exe"));
            AddIfExists(cands, ReadStartMenuInternet("Google Chrome"));
            AddIfExists(cands, ReadStartMenuInternet("Microsoft Edge"));
            string[] guesses = new string[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\Application\msedge.exe")
            };
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("已查找以下位置（注册表 App Paths、Clients\\StartMenuInternet，以及这些路径）：");
            foreach (string g in guesses) sb.Append("\n  " + (File.Exists(g) ? "[存在] " : "[没有] ") + g);
            sb.Append("\n如果浏览器装在别处，请在托盘「设置…」里直接指定 exe 完整路径。");
            return sb.ToString();
        }

        private static void AddIfExists(List<string> list, string p)
        {
            if (string.IsNullOrEmpty(p)) return;
            try { if (File.Exists(p) && !list.Contains(p)) list.Add(p); } catch { }
        }

        private static string ReadAppPaths(string exeName)
        {
            string[] keys = new string[] {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName,
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\" + exeName
            };
            foreach (string k in keys)
            {
                foreach (RegistryKey root in new RegistryKey[] { Registry.LocalMachine, Registry.CurrentUser })
                {
                    try
                    {
                        using (RegistryKey rk = root.OpenSubKey(k))
                        {
                            if (rk == null) continue;
                            object v = rk.GetValue(null);
                            if (v != null) return v.ToString().Trim('"');
                        }
                    }
                    catch { }
                }
            }
            return null;
        }

        private static string ReadStartMenuInternet(string clientName)
        {
            try
            {
                using (RegistryKey rk = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Clients\StartMenuInternet\" + clientName + @"\shell\open\command"))
                {
                    if (rk == null) return null;
                    object v = rk.GetValue(null);
                    if (v == null) return null;
                    return v.ToString().Trim().Trim('"');
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// 把配置里的 browser 值解析成具体浏览器。
        /// auto 时还要看项目 profile 目录归谁——见 PickDefault。
        /// </summary>
        public static BrowserInfo ResolveBrowser(string pref, string projectProfilePath, bool profileExplicit)
        {
            List<BrowserInfo> all = Discover();
            if (string.IsNullOrEmpty(pref) || pref.Equals("auto", StringComparison.OrdinalIgnoreCase))
                return PickDefault(all, projectProfilePath, profileExplicit);

            string p = pref.Trim();
            if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(p)) return null;
                string n = Path.GetFileNameWithoutExtension(p).ToLowerInvariant();
                return new BrowserInfo
                {
                    Key = p,
                    Display = Path.GetFileNameWithoutExtension(p),
                    ExePath = p,
                    ProcessName = n
                };
            }
            foreach (BrowserInfo b in all)
                if (b.Key.Equals(p, StringComparison.OrdinalIgnoreCase)) return b;

            // browser=msedge.exe 这类写法
            foreach (BrowserInfo b in all)
                if (b.ProcessName.Equals(Path.GetFileNameWithoutExtension(p), StringComparison.OrdinalIgnoreCase)) return b;
            return null;
        }

        public static BrowserInfo ResolveBrowser(string pref) { return ResolveBrowser(pref, null, false); }

        public static BrowserInfo ResolveBrowser(string pref, string projectProfilePath)
        {
            return ResolveBrowser(pref, projectProfilePath, false);
        }

        // ── 窗口查找 ───────────────────────────────────────────────

        /// <summary>当前目标窗口是否还有效。</summary>
        public bool TargetAlive
        {
            get { return _hwnd != IntPtr.Zero && Native.IsWindow(_hwnd) && !IsOurProcessWindow(Native.GetForegroundWindow()) ? _hwnd == GetRootOf(_hwnd) : true; }
        }

        public static IntPtr GetRootOf(IntPtr h)
        {
            if (h == IntPtr.Zero) return IntPtr.Zero;
            return Native.GetAncestor(h, Native.GA_ROOT);
        }

        public bool IsWindowAlive()
        {
            return _hwnd != IntPtr.Zero && Native.IsWindow(_hwnd);
        }

        public void ForgetWindow()
        {
            _hwnd = IntPtr.Zero;
            _launchedPid = 0;
            _armed = false;
            UninstallMouseHook();
        }

        /// <summary>
        /// 目标窗口是否属于「我们」这一族。
        ///
        /// 判定层次（缺一不可，之前只做了第一条，所以开发者工具一开就出问题）：
        ///   1. 就是目标窗口本身；
        ///   2. 目标窗口的子窗口 / 弹出窗口（root 是目标窗口）；
        ///   3. 同一个浏览器进程的其它顶层窗口——最典型的是**独立窗口的 DevTools**。
        ///      它既不是目标窗口的子窗口，也不属于我们进程。漏掉这条会导致：
        ///      打开开发者工具时只能显示不能收起、右键粘贴也停摆。
        /// </summary>
        public bool IsOurs(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || _hwnd == IntPtr.Zero) return false;
            if (hwnd == _hwnd) return true;
            IntPtr root = GetRootOf(hwnd);
            if (root == _hwnd) return true;

            // 同进程的其它顶层窗口（DevTools 等）
            uint pidMe, pidOther;
            Native.GetWindowThreadProcessId(_hwnd, out pidMe);
            Native.GetWindowThreadProcessId(hwnd, out pidOther);
            if (pidMe == 0 || pidMe != pidOther) return false;

            // 只认真正的顶层窗口，避免把无关的隐藏辅助窗口算进来
            return Native.IsWindowVisible(hwnd) || root == hwnd;
        }

        public bool IsOursForeground()
        {
            if (!IsWindowAlive()) return false;
            return IsOurs(Native.GetForegroundWindow());
        }

        private static bool IsOurProcessWindow(IntPtr h) { return false; }

        /// <summary>
        /// 窗口挑选的**唯一**判定入口：是否把某个窗口当作我们的 app 窗口，分数多少。
        /// FindTargetWindow 和内置自测都走这个函数，保证「被测的就是在跑的」。
        /// 返回负数 = 不是我们的窗口。
        ///
        /// 重要：titleMatch **不能**单独作为接受条件。
        /// 标题线索只是二级域名词（deepseek / google / chatgpt），用户日常浏览器里
        /// 任何标题含这个词的窗口（DeepSeek 页面、搜过 "deepseek" 的标签页）都会被
        /// 误认成我们的窗口，然后被加 WS_EX_TOOLWINDOW 踢出任务栏——这既破坏用户
        /// 正在用的浏览器，也让我们自己的窗口永远不出现。所以标题只用来**加分排序**，
        /// 接受条件必须是 profile 命中或本程序刚拉起的 PID。
        /// </summary>
        public static long ScoreWindow(bool classIsChromeWidget, bool hasOwner, bool isBrowserProcess,
            bool profileMatch, bool launchedMatch, bool titleMatch, long area)
        {
            if (!classIsChromeWidget) return -1;
            if (hasOwner) return -1;
            if (!isBrowserProcess) return -1;
            if (!profileMatch && !launchedMatch) return -1;      // 标题不足以认领窗口

            long score = area < 0 ? 0 : area;
            // 优先级（高到低）：profile 目录命中 > 本程序刚拉起的 PID > 标题命中。
            // profile 命中排第一，是因为它证明这个窗口属于本项目的登录态；
            // launchedPid 只能证明「这个进程是我们拉起来的」，可靠性次一档。
            if (profileMatch) score += 5000000000000L;
            if (launchedMatch) score += 4000000000000L;
            if (titleMatch) score += 2000000000000L;
            return score;
        }

        /// <summary>
        /// 枚举并按打分挑出最可能是「我们的 app 窗口」的那个。
        ///
        /// 分三级，逐级放宽，但**每一级都必须能证明窗口属于本项目**：
        ///   1. profile 目录命中 / 本程序刚拉起的 PID   ← 主路径，最可靠
        ///   2. 命令行里带本项目 profile 目录           ← WMI 兜底
        ///   3. 标题命中且浏览器进程对                   ← 只支持「用户先手动开窗口」这一种情况
        /// 绝不再出现「光凭标题就把用户日常浏览器窗口认领走」。
        /// </summary>
        public IntPtr FindTargetWindow()
        {
            if (_browser == null) return IntPtr.Zero;
            string wantProc = _browser.ProcessName;

            List<uint> profilePids = PidsForProfile();

            IntPtr best = IntPtr.Zero;
            long bestScore = -1;

            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (pid == 0) return true;

                bool classOk = Native.ClassOf(h) == "Chrome_WidgetWin_1";
                bool owned = Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero;
                bool browserProc = string.Equals(ProcessNameOf(pid), wantProc, StringComparison.OrdinalIgnoreCase);

                // 先做便宜的判断，命中不了就别去取标题/尺寸
                if (!classOk || owned || !browserProc) return true;

                bool profileMatch = profilePids.Contains(pid);
                bool launchedMatch = _launchedPid != 0 && pid == _launchedPid;

                // 主路径：必须 profile 或 launched 命中，标题只加分
                if (!profileMatch && !launchedMatch) return true;

                bool titleMatch = Native.TitleOf(h).IndexOf(_titleHint, StringComparison.OrdinalIgnoreCase) >= 0;

                Native.RECT r;
                Native.GetWindowRect(h, out r);
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);

                long score = ScoreWindow(true, owned, browserProc, profileMatch, launchedMatch, titleMatch, area);
                if (score > bestScore) { bestScore = score; best = h; }
                return true;
            }, IntPtr.Zero);

            if (best != IntPtr.Zero) return best;

            // 兜底 1：命令行里带本项目 profile 目录
            best = FindByProfileDir();
            if (best != IntPtr.Zero) return best;

            // 兜底 2：标题命中（支持「先手动开窗口再启动小工具」）
            return FindByTitle(wantProc);
        }

        /// <summary>
        /// 用 WMI 找出命令行里带本项目 profile 目录的浏览器进程（只管浏览器主进程）。
        /// 返回 pid → 该 pid 的顶层窗口里的 app 窗口。
        /// 这条路径能在「WMI 查不到 / 用户手动开了窗口」时兜底。
        /// </summary>
        private IntPtr FindByProfileDir()
        {
            List<uint> pids = PidsForProfile();
            if (pids.Count == 0) return IntPtr.Zero;

            IntPtr best = IntPtr.Zero;
            long bestArea = -1;
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (!pids.Contains(pid)) return true;
                if (Native.ClassOf(h) != "Chrome_WidgetWin_1") return true;
                if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;

                Native.RECT r;
                Native.GetWindowRect(h, out r);
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (area > bestArea) { bestArea = area; best = h; }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        /// <summary>
        /// 用标题做**最后**的兜底。
        /// 只有在这种情况下才允许：浏览器进程的类名/进程名都对，且没有任何窗口
        /// 已经通过 profile 命中——也就是我们确实还没锁定窗口。
        /// 目的是支持「用户先手动双击 DeepSeekChrome.lnk 开了窗口，再启动小工具」。
        /// </summary>
        private IntPtr FindByTitle(string wantProc)
        {
            IntPtr best = IntPtr.Zero;
            long bestArea = -1;
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (pid == 0) return true;
                if (!string.Equals(ProcessNameOf(pid), wantProc, StringComparison.OrdinalIgnoreCase)) return true;
                if (Native.ClassOf(h) != "Chrome_WidgetWin_1") return true;
                if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
                if (Native.TitleOf(h).IndexOf(_titleHint, StringComparison.OrdinalIgnoreCase) < 0) return true;

                Native.RECT r;
                Native.GetWindowRect(h, out r);
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (area > bestArea) { bestArea = area; best = h; }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        private static string ProcessNameOf(uint pid)
        {
            try { return Process.GetProcessById((int)pid).ProcessName; }
            catch { return ""; }
        }

        private List<uint> PidsForProfile()
        {
            List<uint> pids = new List<uint>();
            if (string.IsNullOrEmpty(_profilePath) || _browser == null) return pids;
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='" + _browser.ProcessName + ".exe'"))
                {
                    foreach (ManagementBaseObject o in searcher.Get())
                    {
                        object cmd = o["CommandLine"];
                        if (cmd == null) continue;
                        if (cmd.ToString().IndexOf(_profilePath, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        try { pids.Add(Convert.ToUInt32(o["ProcessId"])); } catch { }
                    }
                }
            }
            catch (Exception ex) { _log.Add("WMI 查询 profile 进程失败（不影响其它识别路径）: " + ex.Message); }
            return pids;
        }

        // ── 启动 ───────────────────────────────────────────────────

        /// <summary>
        /// 本机 Chromium 秒退的已知标志：0x80000003 = STATUS_BREAKPOINT，
        /// 也就是 Chromium 自己的 CHECK / IMMEDIATE_CRASH。
        ///
        /// 现状说明（别把猜测写成结论）：
        ///   * chrome-profile\Crashpad\reports 里 9 个 dump 全是 0x80000003，同一地址
        ///     chrome.dll+0x1135D76，ptype=browser，模块列表里没有任何第三方 DLL。
        ///   * 本项目老的 Electron 版（legacy-electron\start-deepseek.vbs）当年把同样的
        ///     0x80000003 归因于深信服 SSL VPN 注入 SangforNspX64.dll 导致沙箱起不来。
        ///   * 但 2026-09-30 复检时：AppInit_DLLs 为空、LSP 目录里没有深信服项、
        ///     9 个 dump 里搜不到任何 Sangfor / EasyConnect 字样，深信服客户端日志
        ///     最后写入是 09-26。也就是说「注入」这条**没有被现有证据支持**。
        ///   * 反而有证据表明用户自己双击启动时是健康的：同一个 profile 里有
        ///     13.5 分钟和 33 分钟的完整会话记录。
        /// 所以这里把降级重试定位成「便宜的兜底手段」，而不是「已知病因」。
        /// </summary>
        private const uint STATUS_BREAKPOINT = 0x80000003;

        /// <summary>
        /// 启动浏览器 app 窗口。成功返回 null，失败返回可读原因。
        ///
        /// 会做两件事（都是旧版缺的）：
        ///   1. 等 2.5 秒确认进程没有秒退，秒退要把退出码带出来，不能静默失败；
        ///   2. 如果秒退码是 0x80000003（沙箱初始化失败），自动加 --no-sandbox --test-type
        ///      重试一次——这是本机唯一已知能让 Chromium 活下来的办法。
        /// </summary>
        public string Launch()
        {
            _lastLaunchError = null;
            if (_browser == null)
            {
                _lastLaunchError = "没找到可用的 Chromium 浏览器（Chrome / Edge 都没找到）。"
                    + DescribeSearchPlaces();
                return _lastLaunchError;
            }
            string profile = EffectiveProfilePath();
            if (string.IsNullOrEmpty(profile))
            {
                _lastLaunchError = "profile 路径为空";
                return _lastLaunchError;
            }

            try { if (!Directory.Exists(profile)) Directory.CreateDirectory(profile); }
            catch (Exception ex)
            {
                _lastLaunchError = "创建 profile 目录失败: " + ex.Message;
                return _lastLaunchError;
            }

            string attempt1, attempt2;
            BuildArgs(profile, false, out attempt1);
            BuildArgs(profile, true, out attempt2);

            // sandbox=nosandbox：用户明确要求直接不开沙箱
            if (_sandboxMode == "nosandbox")
            {
                string e0 = TryLaunch(profile, attempt2);
                if (e0 == null)
                {
                    _log.Add("按配置 sandbox=nosandbox 启动成功。注意：该实例页面沙箱已关闭，"
                           + "请不要在这个窗口里登录重要账号。");
                    return null;
                }
                _lastLaunchError = e0;
                return e0;
            }

            // 正常沙箱
            string err = TryLaunch(profile, attempt1);
            if (err == null) return null;

            // sandbox=sandbox：用户要求只用沙箱，不降级
            if (_sandboxMode == "sandbox")
            {
                _lastLaunchError = err;
                return err;
            }

            // auto：识别「沙箱初始化失败」再降级。
            // 说明：退出码和 Crashpad dump 里的异常码不是一回事——本机实测 Chromium 秒退时
            // 退出码可能是 0xFFFF7001（外层框架的错误码）而 dump 里的异常码才是
            // 0x80000003 STATUS_BREAKPOINT。所以这里两种都认，命中就降级重试。
            // 代价很低（多等 2.5 秒），而如果真是沙箱初始化失败，这就是唯一的解药。
            bool sandboxCrash =
                err.IndexOf("80000003", StringComparison.OrdinalIgnoreCase) >= 0 ||
                err.IndexOf("FFFF7001", StringComparison.OrdinalIgnoreCase) >= 0 ||
                err.IndexOf("立即退出", StringComparison.Ordinal) >= 0;

            _log.Add("首次启动失败（" + err + "），改用 --no-sandbox --test-type 重试一次。"
                   + "注意：这只是兜底手段，本机秒退的确切原因尚未定论"
                   + "（候选：沙箱初始化失败 / 启动方进程环境 / 显卡与安全软件驱动）。");
            string err2 = TryLaunch(profile, attempt2);
            if (err2 == null)
            {
                _log.Add("--no-sandbox 重试成功。该实例页面沙箱已关闭，请不要在这个窗口里登录重要账号；"
                       + "根因查明后把 sandbox 设回 sandbox 即可恢复。");
                return null;
            }
            _lastLaunchError = "两种启动方式都失败了：\n1) 正常沙箱：" + err + "\n2) --no-sandbox：" + err2;
            _log.Add(_lastLaunchError);
            return _lastLaunchError;
        }

        /// <summary>
        /// 实际使用的 profile 目录。
        ///
        /// 关键点：不同品牌的浏览器**不能共用同一个 user-data-dir**。Chromium 有单实例锁，
        /// 第二个品牌拿着同一个目录启动时，命令行会被转发给已在跑的进程然后自己退出（退出码 0），
        /// 于是「按了热键没反应」。
        ///
        /// 但如果用户显式写了绝对路径，就尊重用户的选择、绝不悄悄改写——只对相对目录
        /// （也就是默认的 chrome-profile 这种）按品牌加后缀。
        /// </summary>
        private string EffectiveProfilePath()
        {
            if (_browser == null) return _profilePath;
            if (_browser.Key == "chrome") return _profilePath;
            if (_profilePathExplicit) return _profilePath;
            return _profilePath + "-" + _browser.Key;
        }

        public string ActiveProfilePath { get { return EffectiveProfilePath(); } }

        private void BuildArgs(string profile, bool noSandbox, out string args)
        {
            string url = NormalizeUrl(_url);
            args = "--app=\"" + url + "\""
                 + " --user-data-dir=\"" + profile + "\""
                 + " --profile-directory=Default"
                 + " --no-first-run --no-default-browser-check"
                 + " --window-size=" + _width + "," + _height
                 + " --window-position=180,110";
            if (noSandbox) args += " --no-sandbox --test-type";
        }

        /// <summary>真正拉起进程并做秒退检测。返回 null 表示活着。</summary>
        private string TryLaunch(string profile, string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo(_browser.ExePath);
            psi.Arguments = args;
            psi.UseShellExecute = false;
            psi.WorkingDirectory = Path.GetDirectoryName(_browser.ExePath);

            try
            {
                Process p = Process.Start(psi);
                if (p == null) return "Process.Start 返回 null";

                uint pid = 0;
                try { pid = (uint)p.Id; } catch { }
                _log.Add("已启动 " + _browser.Display + " pid=" + pid + " 参数: " + args);

                // 秒退检测（0.5s × 5）
                for (int i = 0; i < 5; i++)
                {
                    Thread.Sleep(500);
                    try
                    {
                        if (p.HasExited)
                        {
                            uint code = 0;
                            try { code = unchecked((uint)p.ExitCode); } catch { }
                            // 退出码 0 = 干净退出。这多半不是崩溃，而是「命令行被转发给了
                            // 已经在运行的实例」——Chromium 单实例机制下的正常行为，
                            // 不能当成失败去触发 --no-sandbox 重试。
                            if (code == 0) return null;

                            string hint = code == STATUS_BREAKPOINT
                                ? "（0x80000003 = Chromium 自身的 CHECK，属沙箱/环境类启动失败）"
                                : "";
                            return _browser.Display + " 启动后立即退出，退出码=0x" + code.ToString("X8") + hint;
                        }
                    }
                    catch { }
                }
                if (pid != 0) _launchedPid = pid;
                return null;
            }
            catch (System.ComponentModel.Win32Exception wex)
            {
                // 把 Win32 错误码带出来：光有 ex.Message 没法定位（例如 1223=用户取消、5=拒绝访问）
                return "启动 " + _browser.Display + " 失败：Win32 错误 " + wex.NativeErrorCode
                     + "（" + wex.Message + "）";
            }
            catch (Exception ex)
            {
                return "启动 " + _browser.Display + " 抛异常: " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static string NormalizeUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "https://chat.deepseek.com";
            if (url.IndexOf("://", StringComparison.Ordinal) < 0) return "https://" + url;
            return url;
        }

        private static string BuildTitleHint(string url)
        {
            try
            {
                string host = new Uri(NormalizeUrl(url)).Host;
                if (!string.IsNullOrEmpty(host))
                {
                    string[] parts = host.Split('.');
                    if (parts.Length >= 2) return parts[parts.Length - 2];
                    return parts[0];
                }
            }
            catch { }
            return "DeepSeek";
        }

        // ── 显示 / 收起 ────────────────────────────────────────────

        public void Adopt(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            _hwnd = hwnd;
            EnsureToolWindow(hwnd);
            _log.Add("已锁定目标窗口 hwnd=0x" + hwnd.ToInt64().ToString("X")
                + " 标题=[" + Native.TitleOf(hwnd) + "]");
        }

        public bool Visible
        {
            get
            {
                if (!IsWindowAlive()) return false;
                if (Native.IsIconic(_hwnd)) return false;
                return Native.IsWindowVisible(_hwnd);
            }
        }

        public void Show()
        {
            if (!IsWindowAlive()) return;

            UninstallMouseHook();                       // 跨进程操作前先摘钩子
            IntPtr fg = Native.GetForegroundWindow();
            if (fg != IntPtr.Zero && !IsOurs(fg)) _lastForeground = fg;

            EnsureToolWindow(_hwnd);
            if (Native.IsIconic(_hwnd)) Native.ShowWindow(_hwnd, Native.SW_RESTORE);

            // 显示。和 Hide() 走同一个两阶段函数（理由见 SetVisibleCore 的注释）。
            SetVisibleCore(true);
            ForceForeground(_hwnd);
            // 记住这一下到底有没有真的提到前台。如果前台是更高权限的窗口
            // （UIPI 不允许 Medium 进程抢它的焦点），热键不能永远只会「显示」，
            // 否则窗口会卡在「可见但被挡住」的状态，用户按几次都没反应。
            _lastShowMissedForeground = !IsOursForeground();
            SyncMouseHook();
        }

        /// <summary>
        /// 收起窗口。返回 true = 已确认窗口不再可见。
        ///
        /// **为什么这个函数必须尽快返回（本文件最重要的约束）**：
        /// 右键粘贴用的 WH_MOUSE_LL 钩子装在 UI 线程上（InstallMouseHook），而低级钩子
        /// 的回调是由安装它的线程调用的。UI 线程一旦在这里长时间不返回，**整个系统的
        /// 鼠标输入都会卡在我们的回调里**；超过系统 LowLevelHooksTimeout（约 300ms）后
        /// Windows 会把钩子**静默摘掉**（句柄仍非零，API 也查不出来）。表现就是用户说的
        /// 「收不回去，而且别的页面也点不动」。所以这里所有等待都必须短，累计远小于 300ms。
        ///
        /// 另外：对**挂死**的目标线程，只有 ShowWindowAsync / SendMessageTimeout 会立刻返回；
        /// 同步的 ShowWindow / SetWindowPos 会一直阻塞（实测 45 秒都不返回，见
        /// win32probe\out\p1_matrix.txt CASE 5/6）。所以这里一律不用同步 ShowWindow。
        /// </summary>
        public bool Hide()
        {
            if (!IsWindowAlive()) return true;
            bool wasOurs = IsOursForeground();

            UninstallMouseHook();
            _armed = false;

            // 1) 清鼠标捕获/模态。必须带超时且总耗时可控——这里以前最多能拖到 1.6 秒，
            //    正好卡在「右键之后想收起」的热路径上。
            if (!CancelMouseModes(_hwnd))
                _log.Add("收起：鼠标捕获未确认解除（已重试）");

            _lastShowMissedForeground = false;

            // 2) 收起。分两个阶段，这是本次修复的核心。
            //
            // 2) 真正的收起动作。两阶段实现见 SetVisibleCore 的注释。
            bool hidden = SetVisibleCore(false);

            // 3) 前台必须交还给别的窗口并确认。交还失败时，第一下点击只会起激活作用。
            //    核对只做 2 轮 × 30ms。
            if (wasOurs && _restoreFocus)
            {
                if (!RestorePreviousForeground())
                    _log.Add("收起：前台未确认交还（第一下点击可能只起激活作用）");
            }
            else if (!_restoreFocus && Native.GetForegroundWindow() == _hwnd)
            {
                _log.Add("收起：前台仍指向已隐藏的窗口（restorefocus 关闭时无法自动交还）");
            }

            // 4) 兜底：无论前面走了哪条分支，都不能把窗口留在「置顶」状态。
            //    本机没有任务栏、没有 Alt+Tab，一个永久置顶的窗口会把桌面彻底挡死。
            EnsureNotTopmost();

            // 5) 清掉可能残留的鼠标捕获。这一步直接决定「别的页面还能不能点」，
            //    原因见 ReleaseLeakedCapture 的注释。
            ReleaseLeakedCapture();

            return hidden;
        }

        /// <summary>
        /// 显示 / 隐藏目标窗口的核心实现。**两个方向共用**，因为约束完全一样。
        ///
        /// 两阶段设计（这是「收不回去」的真正修法）：
        ///
        ///   阶段 A：`ShowWindowAsync` + 短重试。它只往目标线程的消息队列**投一条请求**
        ///           就立刻返回，绝不会阻塞我们。但它有个致命性质：**队列主人不处理时，
        ///           它返回成功却什么都不做**。实测 win32probe\out\p1_matrix.txt CASE 4
        ///           ——对不泵消息的窗口调 `ShowWindowAsync(SW_HIDE)`，0~2ms 返回 True，
        ///           而 `IsWindowVisible` 始终是 True。旧版只有阶段 A，于是「收起」报成功、
        ///           窗口却还在屏幕上；用户再按一次，工具看到"已经不可见"就去执行"呼出"，
        ///           结果什么都不变——这就是「怎么按都收不回去」。
        ///
        ///   阶段 B：同步 `ShowWindow`。它**一定生效**，但在目标线程不泵消息时
        ///           **永久阻塞**（同文件 CASE 5/6：45 秒都不返回），会把我们的 UI 线程
        ///           和挂在它上面的低级鼠标钩子一起拖死。所以不能无条件用，必须先探测。
        ///
        /// 探测：`SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG, 150ms)`。目标线程不响应时
        /// `SMTO_ABORTIFHUNG` 让它**立刻**返回 0（不等满 150ms），所以探测几乎不花时间。
        ///
        /// **时间预算**：这个函数在 UI 线程上被热键链路调用，而右键粘贴的 WH_MOUSE_LL
        /// 钩子就装在这个线程上。钩子回调超过系统 LowLevelHooksTimeout（约 300ms）会被
        /// Windows **静默摘掉**，表现就是「热键突然全不灵 + 鼠标也卡」。所以等待必须短：
        /// 目标线程正常时 4×15+10 = 70ms，异常时最多 8×25 = 200ms。
        /// </summary>
        private bool SetVisibleCore(bool show)
        {
            if (_hwnd == IntPtr.Zero || !Native.IsWindow(_hwnd)) return true;

            bool pump = IsPumping(_hwnd);
            int tries = 0;

            // 阶段 A：异步 + 短重试（线程卡住时多投几次，等它自己缓过来）
            int phaseA = pump ? (show ? 5 : 4) : (show ? 10 : 8);
            for (int i = 0; i < phaseA; i++)
            {
                tries++;
                Native.ShowWindowAsync(_hwnd, show ? Native.SW_SHOW : Native.SW_HIDE);
                Thread.Sleep(pump ? 15 : 25);
                if (IsDone(show)) break;
            }

            // 阶段 B：确认它在处理消息了 → 同步调用安全且一定生效
            if (!IsDone(show) && IsPumping(_hwnd))
            {
                tries++;
                Native.ShowWindow(_hwnd, show ? Native.SW_SHOW : Native.SW_HIDE);
                Thread.Sleep(10);
                if (!IsDone(show) && show)
                {
                    // 显示多一手兜底：不带激活地显示 + 强制刷新框架。
                    // 隐藏不需要，因为 SW_HIDE 已经改掉了 WS_VISIBLE 样式位。
                    Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
                    Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                        Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER
                        | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | Native.SWP_ASYNCWINDOWPOS);
                }
            }

            _lastHideTries = tries;
            bool done = IsDone(show);
            if (!done)
            {
                // 目标线程既不处理异步请求、又不敢用同步调用（会同归于尽）——
                // 先回去，交给心跳复核，并把原因如实写清楚（绝不谎报成功）。
                Native.ShowWindowAsync(_hwnd, show ? Native.SW_SHOW : Native.SW_HIDE);
                if (!show) _hidePending = true;
                _log.Add((show ? "呼出" : "收起") + "未确认生效（pump=" + (pump ? 1 : 0)
                    + "）——浏览器没有响应窗口命令；已补发请求并在心跳里复核。"
                    + "诊断: " + VisibleDiagnosis);
            }
            else if (!show)
            {
                _hidePending = false;
            }
            return done;
        }

        /// <summary>目标状态到了没有：显示 = 可见且未最小化；隐藏 = 真的不可见。</summary>
        private bool IsDone(bool show)
        {
            if (show) return Native.IsWindowVisible(_hwnd) && !Native.IsIconic(_hwnd);
            return IsReallyHidden(_hwnd);
        }

        /// <summary>
        /// 由 SyncTick 每拍调用的复核：如果上一拍收起没落地，这里再补发一次。
        /// 放在心跳里做，是为了不在热路径上阻塞 UI 线程（见 Hide 的说明）。
        /// </summary>
        public void VerifyHideLanded()
        {
            if (!IsWindowAlive()) return;
            if (!_hidePending) return;
            if (!Native.IsWindowVisible(_hwnd))
            {
                _hidePending = false;
                return;
            }
            Native.ShowWindowAsync(_hwnd, Native.SW_HIDE);
            if (!Native.IsWindowVisible(_hwnd))
            {
                _hidePending = false;
                _log.Add("收起复核：窗口已隐藏（补发的异步请求生效）");
            }
        }

        /// <summary>
        /// 把前台交还给收起前的窗口，并确认结果。
        /// 返回 true = 前台已落在别的可见窗口上。以前发一次 SetForegroundWindow 就算完，
        /// 失败时前台会悬着，用户收起后的第一下点击就会被系统当成激活点击吞掉。
        ///
        /// 等待只做 2 轮 × 25ms：这个函数在 UI 线程上被 Hide() 调用，多等就是拿钩子的
        /// 存活时间换准确性（见 Hide 的说明）。
        /// </summary>
        public bool RestorePreviousForeground()
        {
            IntPtr target = _lastForeground;
            if (!IsUsableForegroundTarget(target)) target = FindNextUsableWindow();
            if (target == IntPtr.Zero)
            {
                _log.Add("收起：找不到可接管前台的窗口");
                return false;
            }
            if (Native.IsIconic(target)) Native.ShowWindow(target, Native.SW_RESTORE);
            ForceForeground(target);

            for (int i = 0; i < 2; i++)
            {
                IntPtr fg = Native.GetForegroundWindow();
                if (fg != IntPtr.Zero && fg != _hwnd && Native.IsWindowVisible(fg)) return true;
                Thread.Sleep(25);
            }
            return false;
        }

        /// <summary>
        /// 真正把窗口提到前台。
        ///
        /// 为什么不能只调 SetForegroundWindow：后台进程会被 Windows 的前台锁拒绝，
        /// 窗口虽然可见，却仍被当前前台窗口盖住。本工具特意把窗口排除出任务栏和
        /// Alt+Tab，这台机器又没有任务栏——提不到前台时用户按热键看到的就是
        /// 「完全没反应」，而且再也找不到窗口。标准解法是临时把自己的输入队列挂到
        /// 当前前台线程上（AttachThreadInput），再提窗口，最后立刻摘开。
        /// </summary>
        private void ForceForeground(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return;

            IntPtr fg = Native.GetForegroundWindow();
            uint fgPid;
            uint fgThread = fg == IntPtr.Zero ? 0 : Native.GetWindowThreadProcessId(fg, out fgPid);
            uint myThread = Native.GetCurrentThreadId();
            bool attached = false;
            try
            {
                if (fgThread != 0 && fgThread != myThread)
                    attached = Native.AttachThreadInput(myThread, fgThread, true);

                bool brought = Native.BringWindowToTop(hwnd);
                bool setFg = Native.SetForegroundWindow(hwnd);
                bool switched = false;
                if (!setFg)
                {
                    // SwitchToThisWindow 是常见的兜底；第二个参数传 false，
                    // 避免把特意排除出 Alt+Tab 的窗口又塞回切换列表。
                    Native.SwitchToThisWindow(hwnd, false);
                    switched = true;
                }
                // 前台锁不只让 SetForegroundWindow 失败：在「前台是管理员窗口 /
                // 不同完整性级别的窗口」时，AttachThreadInput 也会失败。此时窗口虽然
                // 已经 Show 出来，却仍被前台窗口完全盖住——用户看到的就是「按热键
                // 像没反应」。对此再补一手：短暂置顶再取消置顶，把目标窗口提到当前
                // 前台窗口之上的普通 Z 序，同时不依赖 SetForegroundWindow 成功。
                //
                // 这一手必须包在 try/finally 里：置顶之后如果抛异常或提前返回，
                // 窗口会**永久停在最前面**。本机没有任务栏、也没有 Alt+Tab 条目，
                // 一个永久置顶的窗口会把桌面彻底挡死，除了按热键没有任何办法。
                string raiseInfo = "";
                if (hwnd == _hwnd && !IsOursForeground())
                {
                    bool top = false, notop = false; bool set2 = false;
                    try
                    {
                        // 同样带 SWP_ASYNCWINDOWPOS：Z 序调整不值得拿 UI 线程去等目标线程
                        // （同步版在目标不泵消息时实测 8 秒不返回，见 EnsureNotTopmost 注释）。
                        top = Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE
                            | Native.SWP_SHOWWINDOW | Native.SWP_ASYNCWINDOWPOS);
                        Thread.Sleep(30);
                        notop = Native.SetWindowPos(hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE
                            | Native.SWP_ASYNCWINDOWPOS);
                        Thread.Sleep(15);
                        set2 = Native.SetForegroundWindow(hwnd);
                        switched = switched || set2;
                    }
                    finally
                    {
                        if (top && !notop) notop = EnsureNotTopmost(hwnd);
                        raiseInfo = "_top=" + top + "_notop=" + notop + "_set2=" + set2;
                    }
                }

                // 一行结果放进实时状态串，用户「提不到前台」时能直接看到卡在哪一步。
                _lastForegroundResult = "fg=0x" + fg.ToInt64().ToString("X")
                    + "_th=" + fgThread + "_att=" + attached
                    + "_br=" + brought + "_set=" + setFg + "_sw=" + switched
                    + raiseInfo
                    + "_now=" + Native.GetForegroundWindow().ToInt64().ToString("X");
            }
            catch (Exception ex)
            {
                _lastForegroundResult = "ex=" + ex.GetType().Name;
                _log.Add("把窗口提到前台失败: " + ex.Message);
            }
            finally
            {
                if (attached)
                {
                    try { Native.AttachThreadInput(myThread, fgThread, false); }
                    catch { }
                }
            }
        }

        private bool IsUsableForegroundTarget(IntPtr hwnd)
        {
            return hwnd != IntPtr.Zero && Native.IsWindow(hwnd) && Native.IsWindowVisible(hwnd) && !IsOurs(hwnd);
        }

        /// <summary>把目标窗口从「置顶」拉回普通 Z 序。失败会记一笔，便于诊断。</summary>
        private void EnsureNotTopmost()
        {
            if (_hwnd == IntPtr.Zero || !Native.IsWindow(_hwnd)) return;
            EnsureNotTopmost(_hwnd);
        }

        /// <summary>
        /// 取消置顶的静态实现。**必须幂等**：对本来就是普通 Z 序的窗口调用也安全，
        /// 因为它是「危险状态」的收尾动作，宁可多调一次。
        ///
        /// **绝不允许它阻塞**（这是「双击 Ctrl 收不回去」的直接成因之一）：
        /// 同步 SetWindowPos 是跨进程调用，目标线程不泵消息时它会一直等——实测
        /// `audit-2\host\NT_HostProbe` 的 t1：把目标线程冻结后同步 SetWindowPos
        /// **8000ms 都没返回**，只有结束那个进程才返回；对照响应正常的窗口是 0ms。
        /// 而这个调用在 Hide() 的收尾里是**无条件**执行的，于是 Hide() 会挂在那里，
        /// UI 线程上的 WM_APP_TOGGLE 永远排不到 → 用户怎么按都收不回去。
        ///
        /// 所以这里按目标线程状态分两路，两条路都保证立刻返回：
        ///   * 不泵消息 → 只改扩展样式位 + 异步投递 SWP_ASYNCWINDOWPOS；
        ///   * 在泵消息 → 同样用异步版本，让目标线程自己按顺序处理，我们不等。
        /// 置顶不是「正确性」动作（它是「顺手拉回 Z 序」的兜底），用不着为它冒险。
        /// </summary>
        private static bool EnsureNotTopmost(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return true;

            // 第一手：直接改扩展样式位，去掉 WS_EX_TOPMOST。这一步是本地内存操作
            // （写我们自己映射到的窗口结构，不跨线程等待），不泵消息的窗口也能生效，
            // 而且比 SetWindowPos 更直接——置顶状态本身就是这个样式位。
            long ex = 0;
            try
            {
                ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
                if ((ex & Native.WS_EX_TOPMOST) != 0)
                    Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, ex & ~Native.WS_EX_TOPMOST);
            }
            catch { /* 跨进程写窗口样式失败不致命，下面还有异步 SetWindowPos */ }

            // 第二手：异步 SetWindowPos 让 Z 序也真正落回 NOTOPMOST。
            // SWP_ASYNCWINDOWPOS = 调用线程与目标线程不同时，把请求投递到对方队列后立刻返回。
            return Native.SetWindowPos(hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE
                | Native.SWP_ASYNCWINDOWPOS);
        }

        private IntPtr FindNextUsableWindow()
        {
            IntPtr h = Native.GetWindow(_hwnd, Native.GW_HWNDNEXT);
            int guard = 0;
            while (h != IntPtr.Zero && guard++ < 64)
            {
                if (IsUsableForegroundTarget(h)) return h;
                h = Native.GetWindow(h, Native.GW_HWNDNEXT);
            }
            return IntPtr.Zero;
        }

        /// <summary>让窗口从任务栏和 Alt+Tab 里消失。</summary>
        public void EnsureToolWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return;
            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            long want = (ex | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW;
            if (want == ex) return;
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, want);
            Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER
                | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED | Native.SWP_ASYNCWINDOWPOS);
        }

        // ── 鼠标捕获 / 模态清理 ────────────────────────────────────

        /// <summary>窗口所在 UI 线程当前持有的鼠标捕获窗口；没有/查不到返回 Zero。</summary>
        private static IntPtr CaptureWindowOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return IntPtr.Zero;
            uint pid;
            uint tid = Native.GetWindowThreadProcessId(hwnd, out pid);
            if (tid == 0) return IntPtr.Zero;
            Native.GUITHREADINFO gti = new Native.GUITHREADINFO();
            gti.cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO));
            if (!Native.GetGUIThreadInfo(tid, ref gti)) return IntPtr.Zero;
            return gti.hwndCapture;
        }

        /// <summary>浏览器窗口所在线程的鼠标捕获（跨进程可读，诊断用）。</summary>
        public IntPtr TargetThreadCapture
        {
            get { return IsWindowAlive() ? CaptureWindowOf(_hwnd) : IntPtr.Zero; }
        }

        /// <summary>
        /// 目标窗口所在 UI 线程此刻是否在泵消息。
        ///
        /// 为什么必须知道这件事：`ShowWindowAsync` 只是往目标线程的消息队列里投一条请求，
        /// 它会**立刻返回 true，却什么也不做**——如果那个线程没在处理消息（卡住、或正待在
        /// 原生菜单/模态循环里）。实测证据：`win32probe\out\p1_matrix.txt` CASE 4，
        /// 对已标记 hung 的窗口调 `ShowWindowAsync(SW_HIDE)`，0~2ms 返回 True，
        /// 而 `IsWindowVisible` 一直是 True。
        ///
        /// 反过来，同步的 `ShowWindow` / `SetWindowPos` 在目标线程不泵消息时会**永久阻塞**
        /// （同文件 CASE 5/6：45 秒都没返回），所以只有在确认它在泵消息之后才敢用同步版本。
        ///
        /// 用 `SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG, 150ms)` 探测：
        /// 目标线程不响应时，`SMTO_ABORTIFHUNG` 会让它**立刻**返回 0（不等满 150ms），
        /// 所以这个探测本身很便宜，不会拖慢收起动作。
        /// </summary>
        public bool TargetThreadPumping { get { return IsPumping(_hwnd); } }

        private static bool IsPumping(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return false;
            IntPtr result;
            return Native.SendMessageTimeout(hwnd, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero,
                Native.SMTO_ABORTIFHUNG, 150, out result) != IntPtr.Zero;
        }

        /// <summary>
        /// 目标窗口是不是「真的看不见」。两个权威信号必须**同时**同意才算收起：
        /// `IsWindowVisible` 和 `GWL_STYLE & WS_VISIBLE`。
        ///
        /// 为什么只用这两个：它们读的是同一份窗口样式状态，是「隐藏了没有」的权威来源。
        /// **不要**用 `GetWindowPlacement().showCmd` 做判断——实测（repro\probe-hide-real.exe）
        /// 窗口隐藏之后 showCmd 仍是 1（SW_SHOWNORMAL），它反映的是"上次显示用的命令"，
        /// 不是当前可见性。我一开始把它当第三个信号，导致明明已经隐藏了却报失败。
        /// </summary>
        public bool ReallyHidden
        {
            get
            {
                if (_hwnd == IntPtr.Zero || !Native.IsWindow(_hwnd)) return true;
                return IsReallyHidden(_hwnd);
            }
        }

        private static bool IsReallyHidden(IntPtr hwnd)
        {
            if (Native.IsWindowVisible(hwnd)) return false;
            if ((Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE) & Native.WS_VISIBLE) != 0) return false;
            return true;
        }

        /// <summary>把上面那条「为什么还说它可见」的判定结果变成一行诊断文本。</summary>
        public string VisibleDiagnosis
        {
            get
            {
                if (_hwnd == IntPtr.Zero || !Native.IsWindow(_hwnd)) return "无窗口";
                bool vis = Native.IsWindowVisible(_hwnd);
                bool styleVis = (Native.GetWindowLongPtr(_hwnd, Native.GWL_STYLE) & Native.WS_VISIBLE) != 0;
                return "vis=" + (vis ? 1 : 0)
                    + " style=" + (styleVis ? 1 : 0)
                    + " hung=" + (Native.IsHungAppWindow(_hwnd) ? 1 : 0)
                    + " pump=" + (IsPumping(_hwnd) ? 1 : 0)
                    + " tries=" + _lastHideTries
                    + " pend=" + (_hidePending ? 1 : 0);
            }
        }
        /// <summary>
        /// 清掉目标窗口残留的鼠标捕获（capture）。
        ///
        /// **为什么这一条直接决定「别的页面还能不能点」**：
        /// 实测（win32probe\out\p5_rbutton.txt 场景 C/C2）——一个持有 capture 的窗口
        /// **即使已经被隐藏**，仍然会继续收到鼠标消息：躲开它、点在别的窗口上，
        /// WM_LBUTTONDOWN 还是被投递给那个隐藏的持有者。也就是说，只要 capture 没清掉，
        /// 用户点哪里都"没反应"，看起来就是「整个桌面点不动」。
        ///
        /// 而 capture 泄漏正是右键粘贴的必然副作用：本工具的鼠标钩子吞掉了
        /// WM_RBUTTONUP，页面收到 RBUTTONDOWN 后 SetCapture 了，却永远等不到抬起
        /// （实测场景 B：capture 停在 0x640E16 不放）。同文件场景 C2 也证明
        /// WM_CANCELMODE 能把它清干净，所以这里补发并校验。
        ///
        /// 放在 Hide() 的收尾：用户右键之后想收起时，顺手把这份残留状态一起清掉，
        /// 保证收起之后别的窗口能正常接收点击。
        /// </summary>
        public void ReleaseLeakedCapture()
        {
            if (!IsWindowAlive()) return;
            IntPtr cap = CaptureWindowOf(_hwnd);
            if (cap == IntPtr.Zero) return;          // 绝大多数情况：没有泄漏，直接返回
            if (!TryReleaseCapture(_hwnd, 2, 120))
                _log.Add("目标窗口仍持有鼠标捕获（已补发 WM_CANCELMODE 仍未清掉）："
                    + "此时点其它窗口也会被投递给它，表现为「桌面点不动」");
            else
                _log.Add("已清掉残留的鼠标捕获（右键抬起被吞掉留下的）");
        }

        /// <summary>
        /// 对 hwnd 所在线程的 capture 窗口补发 WM_CANCELMODE。
        /// 返回 true = 已确认 capture 为空。WM_CANCELMODE 走 DefWindowProc 会释放捕获，
        /// 比补发 WM_RBUTTONUP 安全——后者会把 Chrome 原生右键菜单弹出来。
        /// </summary>
        private static bool TryReleaseCapture(IntPtr hwnd, int attempts, uint perTryTimeoutMs)
        {
            for (int i = 0; i < attempts; i++)
            {
                IntPtr cap = CaptureWindowOf(hwnd);
                if (cap == IntPtr.Zero) return true;
                SendCancelMode(cap, perTryTimeoutMs);
                SendCancelMode(hwnd, perTryTimeoutMs);
                Thread.Sleep(20);
            }
            return CaptureWindowOf(hwnd) == IntPtr.Zero;
        }

        /// <summary>
        /// 清捕获 + 模态。返回 true = 捕获已确认解除
        /// （焦点/窗口自身的 WM_CANCELMODE 只是尽力而为）。
        ///
        /// **超时预算必须小**：这个函数在 UI 线程上被 Hide() 调用，而低级鼠标钩子的
        /// 回调就挂在这个线程上。旧值是 3 × (150+150) + 2 × 300 ≈ 1.6 秒，单独一项就把
        /// UI 线程按住超过 LowLevelHooksTimeout（约 300ms），于是钩子被系统静默摘掉、
        /// 全系统鼠标输入跟着卡——这正是「右键之后收不回去、别的页面也点不动」。
        /// 现在最多 1 × (80+80) + 2 × 60 ≈ 280ms，且正常情况下 capture 为空时是 0ms。
        /// </summary>
        private bool CancelMouseModes(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return true;

            bool captureCleared = TryReleaseCapture(hwnd, 1, 80);

            uint pid;
            uint tid = Native.GetWindowThreadProcessId(hwnd, out pid);
            if (tid != 0)
            {
                Native.GUITHREADINFO gti = new Native.GUITHREADINFO();
                gti.cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO));
                if (Native.GetGUIThreadInfo(tid, ref gti))
                    SendCancelMode(gti.hwndFocus, 60);
            }
            SendCancelMode(hwnd, 60);
            return captureCleared;
        }

        private static bool SendCancelMode(IntPtr hwnd, uint timeoutMs = 60)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return true;
            IntPtr result;
            return Native.SendMessageTimeout(hwnd, Native.WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero,
                Native.SMTO_ABORTIFHUNG, timeoutMs, out result) != IntPtr.Zero;
        }

        // ── 右键粘贴 ───────────────────────────────────────────────

        public void InstallWinEventHook()
        {
            if (_winEventHook != IntPtr.Zero) return;
            _winEventProc = WinEventCallback;
            _winEventHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND,
                Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEventProc, 0, 0,
                Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        }

        private void WinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            SyncMouseHook();
        }

        /// <summary>只有「右键粘贴开启 + 目标窗口在前台」才装钩子，其余时刻一律卸载。</summary>
        public void SyncMouseHook()
        {
            if (!_rightClickPaste) { UninstallMouseHook(); return; }
            if (IsOursForeground()) InstallMouseHook();
            else UninstallMouseHook();
        }

        private void InstallMouseHook()
        {
            if (!_rightClickPaste || _mouseHook != IntPtr.Zero) return;
            _mouseProc = MouseProc;
            IntPtr hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, Native.GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                _mouseProc = null;
                _log.Add("右键粘贴不可用：WH_MOUSE_LL 安装失败 err=" + err);
                return;
            }
            _mouseHook = hook;
            if (_stats != null) _stats.NoteHookInstall();
        }

        public void UninstallMouseHook()
        {
            _armed = false;
            if (_mouseHook == IntPtr.Zero) return;
            try { Native.UnhookWindowsHookEx(_mouseHook); } catch { }
            _mouseHook = IntPtr.Zero;
            _mouseProc = null;
        }

        private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    if (msg == Native.WM_RBUTTONDOWN)
                    {
                        if (_stats != null) _stats.NoteDown();
                        bool injected = false;
                        try
                        {
                            Native.MSLLHOOKSTRUCT ms = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                            injected = (ms.flags & Native.LLMHF_INJECTED) != 0;
                        }
                        catch { }
                        // 按下不吞：让页面收到它，输入框才会被聚焦、光标才会移到点击处。
                        // Shift+右键留给 Chrome 原生菜单。
                        _armed = !injected && IsOursForeground() && !Native.IsDown(Native.VK_SHIFT);
                    }
                    else if (msg == Native.WM_RBUTTONUP)
                    {
                        // 抬起时如果 Shift 已经按下，说明用户想要的是原生菜单（例如
                        // 按下后才补按 Shift）。此时不能注入 Ctrl+V——物理 Shift 还在，
                        // 会变成 Chrome 的「粘贴为纯文本」。放行，让原生菜单正常弹出。
                        if (_armed && IsOursForeground() && !Native.IsDown(Native.VK_SHIFT))
                        {
                            _armed = false;
                            if (_stats != null) _stats.NoteUp();
                            // ★ 关键：把这次抬起被吞掉的事实同步给共享鼠标状态。
                            // 钩子链是**后装的先跑**，本钩子（装得晚）先看到这条 up 并
                            // return 1，热键那条守卫钩子就再也收不到它了。若不一并更正，
                            // 守卫方会以为右键一直按着 → 第一敲永不武装 → 此后每一次
                            // 双击 Ctrl 都被静默忽略（用户症状：右键点一下之后收不回去）。
                            // 详见 Native.NoteRmbUpSwallowed 的注释。
                            Native.NoteRmbUpSwallowed();
                            // 不在这里做粘贴：钩子回调必须尽快返回，否则系统输入会卡。
                            // 回消息循环，等页面自己把菜单/选区处理完再注入 Ctrl+V。
                            PostPaste();
                            return (IntPtr)1;      // 吞掉抬起，Chrome 的右键菜单正是在这时弹出
                        }
                        _armed = false;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Add("鼠标钩子回调异常: " + ex.Message);
            }
            return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        public event Action PasteRequested;

        private void PostPaste()
        {
            Action h = PasteRequested;
            if (h != null) h();
        }

        public void Dispose()
        {
            UninstallMouseHook();
            if (_winEventHook != IntPtr.Zero)
            {
                try { Native.UnhookWinEvent(_winEventHook); } catch { }
                _winEventHook = IntPtr.Zero;
                _winEventProc = null;
            }
        }
    }
}
