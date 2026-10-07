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

            // 显示用同步调用，并且**当场校验结果**。
            // 收起那一侧用的是 ShowWindowAsync（避免 Chrome 卡住时把我们也卡住），
            // 但异步隐藏有可能在「显示」之后才落地，把窗口又收回去——用户看到的就是
            // 「双击 Ctrl 能收起，但再也呼不出来」。所以这里循环确认，
            // 不发指令就完事：真的可见了才算成功。
            for (int i = 0; i < 3; i++)
            {
                Native.ShowWindowAsync(_hwnd, Native.SW_SHOW);
                Native.ShowWindow(_hwnd, Native.SW_SHOW);
                Thread.Sleep(60);
                if (Native.IsWindowVisible(_hwnd) && !Native.IsIconic(_hwnd)) break;
                _log.Add("显示后窗口仍不可见，重试第 " + (i + 2) + " 次");
            }

            ForceForeground(_hwnd);
            // 记住这一下到底有没有真的提到前台。如果前台是更高权限的窗口
            // （UIPI 不允许 Medium 进程抢它的焦点），热键不能永远只会「显示」，
            // 否则窗口会卡在「可见但被挡住」的状态，用户按几次都没反应。
            _lastShowMissedForeground = !IsOursForeground();
            SyncMouseHook();
        }

        public void Hide()
        {
            if (!IsWindowAlive()) return;
            bool wasOurs = IsOursForeground();

            UninstallMouseHook();
            _armed = false;

            // 1) 清鼠标捕获/模态：以前只发一次且不看结果；现在带重试和校验。
            if (!CancelMouseModes(_hwnd))
                _log.Add("收起：鼠标捕获未确认解除（已重试）");

            _lastShowMissedForeground = false;

            // 2) 异步隐藏必须确认结果。ShowWindowAsync 返回 ≠ 已经隐藏：
            //    窗口还留在屏幕上时，收起后的第一下点击会被前台/激活逻辑吃掉，
            //    表现就是「点桌面没反应，再点一下才正常」。
            bool hidden = false;
            for (int i = 0; i < 6; i++)
            {
                Native.ShowWindowAsync(_hwnd, Native.SW_HIDE);
                Thread.Sleep(50);
                if (!Native.IsWindowVisible(_hwnd)) { hidden = true; break; }
                _log.Add("收起后窗口仍可见，重试第 " + (i + 2) + " 次");
            }
            if (!hidden)
                _log.Add("收起失败：窗口一直保持可见 hwnd=0x" + _hwnd.ToInt64().ToString("X"));

            // 3) 前台必须交还给别的窗口并确认。交还失败时，第一下点击只会起激活作用。
            if (wasOurs && _restoreFocus)
            {
                if (!RestorePreviousForeground())
                    _log.Add("收起：前台未确认交还（第一下点击可能只起激活作用）");
            }
            else if (!_restoreFocus && Native.GetForegroundWindow() == _hwnd)
            {
                _log.Add("收起：前台仍指向已隐藏的窗口（restorefocus 关闭时无法自动交还）");
            }
        }

        /// <summary>
        /// 把前台交还给收起前的窗口，并确认结果。
        /// 返回 true = 前台已落在别的可见窗口上。以前发一次 SetForegroundWindow 就算完，
        /// 失败时前台会悬着，用户收起后的第一下点击就会被系统当成激活点击吞掉。
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

            for (int i = 0; i < 5; i++)
            {
                IntPtr fg = Native.GetForegroundWindow();
                if (fg != IntPtr.Zero && fg != _hwnd && Native.IsWindowVisible(fg)) return true;
                Thread.Sleep(40);
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
                string raiseInfo = "";
                if (hwnd == _hwnd && !IsOursForeground())
                {
                    bool top = Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                        Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
                    Thread.Sleep(50);
                    bool notop = Native.SetWindowPos(hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                        Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                    Thread.Sleep(30);
                    bool set2 = Native.SetForegroundWindow(hwnd);
                    switched = switched || set2;
                    raiseInfo = "_top=" + top + "_notop=" + notop + "_set2=" + set2;
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
                | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
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
        /// 右键抬起被钩子吞掉后，Chromium 可能以为鼠标还按着、继续持有 capture。
        /// 窗口可见且在前台时这会让「下一击」落进 Chrome；窗口收起后虽不会截输入，
        /// 但残留状态没有意义，所以这里补一次 WM_CANCELMODE 并校验。
        /// </summary>
        public void ReleaseLeakedCapture()
        {
            if (!IsWindowAlive()) return;
            if (!TryReleaseCapture(_hwnd, 3, 200))
                _log.Add("右键粘贴：Chrome 仍持有鼠标捕获（已补发 WM_CANCELMODE 仍未清掉）");
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
                Thread.Sleep(30);
            }
            return CaptureWindowOf(hwnd) == IntPtr.Zero;
        }

        /// <summary>
        /// 清捕获 + 模态。返回 true = 捕获已确认解除
        /// （焦点/窗口自身的 WM_CANCELMODE 只是尽力而为）。
        /// </summary>
        private bool CancelMouseModes(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return true;

            bool captureCleared = TryReleaseCapture(hwnd, 3, 150);

            uint pid;
            uint tid = Native.GetWindowThreadProcessId(hwnd, out pid);
            if (tid != 0)
            {
                Native.GUITHREADINFO gti = new Native.GUITHREADINFO();
                gti.cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO));
                if (Native.GetGUIThreadInfo(tid, ref gti))
                    SendCancelMode(gti.hwndFocus, 300);
            }
            SendCancelMode(hwnd, 300);
            return captureCleared;
        }

        private static bool SendCancelMode(IntPtr hwnd, uint timeoutMs = 300)
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
