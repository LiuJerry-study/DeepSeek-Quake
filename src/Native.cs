// DeepSeek Quake —— Win32 / GDI+ 互操作声明
//
// 单独成文件只是为了不让主逻辑被一大坨 DllImport 淹没；编译时和 DeepSeekQuake.cs
// 一起交给 csc 即可（见 README「重新编译」）。

using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DeepSeekQuake
{
    internal static class Native
    {
        // ── 热键 / 窗口 ─────────────────────────────────────────────
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int cmd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindowAsync(IntPtr hWnd, int cmd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        /// <summary>
        /// 窗口是否已被系统判定为「无响应」（目标线程不泵消息超过约 5 秒）。
        /// 用于诊断「收起没生效」到底是浏览器卡住了，还是本工具自己的缺陷。
        /// </summary>
        [DllImport("user32.dll")]
        public static extern bool IsHungAppWindow(IntPtr hWnd);

        // ── 窗口位置枚举（交叉核对「到底隐藏了没有」时要用的常量）──────
        // 为什么要交叉核对：ShowWindowAsync 只是往目标线程投一条请求，
        // 目标线程不处理时它会"报告成功"但什么都没发生，只信一个信号会误判。
        // 注意：SW_HIDE / SW_SHOW / SW_RESTORE 本文件里已经有了，不要再定义。
        public const int GWL_STYLE = -16;
        public const int WS_VISIBLE = 0x10000000;

        public const int SW_SHOWNORMAL = 1;
        public const int SW_SHOWNOACTIVATE = 4;
        public const int SW_MINIMIZE = 6;
        public const int SW_SHOWMINNOACTIVE = 7;
        public const int SW_SHOWNA = 8;

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern void SwitchToThisWindow(IntPtr hWnd, bool altTab);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        /// <summary>
        /// 把自己的输入队列临时挂到另一个线程上。配合 SetForegroundWindow 使用，
        /// 可以绕过 Windows 的前台锁——否则后台进程显示自己的窗口后，窗口仍会被
        /// 当前前台窗口盖住，表现为「按热键像没反应」。
        /// </summary>
        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder s, int n);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetWindowTextW")]
        private static extern bool SetWindowTextW(IntPtr hWnd, string text);

        /// <summary>设置窗口标题（失败不抛异常，只返回 false）。</summary>
        public static bool SetWindowTextSafe(IntPtr hWnd, string text)
        {
            if (hWnd == IntPtr.Zero) return false;
            try { return SetWindowTextW(hWnd, text ?? ""); }
            catch { return false; }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassNameW(IntPtr hWnd, StringBuilder s, int n);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        public static extern int GetWindowLong32(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        public static extern int SetWindowLong32(IntPtr hWnd, int index, int value);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        public static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        public static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int index, IntPtr value);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowW(string className, string windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr child, string className, string windowName);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        // ── 自建钩子线程用：每条低级钩子都必须由一个带消息循环的线程拥有 ──
        [DllImport("user32.dll")]
        public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern int GetMessageTime();

        // ── 输入注入 ───────────────────────────────────────────────
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

        // ── 全局低级钩子（键盘 / 鼠标）─────────────────────────────
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc proc, IntPtr hMod, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string name);

        // ── 进程完整性级别（诊断低完整性沙箱导致钩子收不到输入）─────
        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
            IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

        [DllImport("advapi32.dll")]
        public static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

        // ── 前台窗口变化通知 / 跨进程消息超时 ─────────────────────
        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hModWinEventProc,
            WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
            uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        // ── 64/32 位通用的窗口样式读写 ─────────────────────────────
        public static long GetWindowLongPtr(IntPtr hWnd, int index)
        {
            long value = IntPtr.Size == 8
                ? GetWindowLongPtr64(hWnd, index).ToInt64()
                : (long)GetWindowLong32(hWnd, index);
            return value & 0xFFFFFFFFL;                  // 扩展样式始终是 32 位
        }

        public static void SetWindowLongPtr(IntPtr hWnd, int index, long value)
        {
            if (IntPtr.Size == 8) SetWindowLongPtr64(hWnd, index, new IntPtr(value));
            else SetWindowLong32(hWnd, index, unchecked((int)value));
        }

        public delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

        public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

        public delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct GUITHREADINFO
        {
            public int cbSize;
            public uint flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public RECT rcCaret;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SID_AND_ATTRIBUTES
        {
            public IntPtr Sid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TOKEN_MANDATORY_LABEL
        {
            public SID_AND_ATTRIBUTES Label;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        // ── 常量 ───────────────────────────────────────────────────
        public const int GWL_EXSTYLE = -20;
        public const uint GW_OWNER = 4;
        public const uint GW_HWNDNEXT = 2;
        public const uint GA_ROOT = 2;

        public const long WS_EX_TOOLWINDOW = 0x80L;
        public const long WS_EX_APPWINDOW = 0x40000L;

        /// <summary>置顶状态就存在这个扩展样式位里。清掉它 = 取消置顶，
        /// 而且是本地操作，不像 SetWindowPos 那样要等目标线程。</summary>
        public const long WS_EX_TOPMOST = 0x08L;

        public const int SW_HIDE = 0;
        public const int SW_SHOW = 5;
        public const int SW_RESTORE = 9;

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_FRAMECHANGED = 0x0020;
        public const uint SWP_SHOWWINDOW = 0x0040;

        /// <summary>
        /// 跨线程/跨进程调用 SetWindowPos 时，让系统把请求异步投递给目标线程的队列，
        /// **立刻返回**而不是等它处理完。
        ///
        /// 为什么必须知道这个标志：同步 SetWindowPos 会一直等目标线程泵消息。实测
        /// （audit-2\host\NT_HostProbe 的 t1）目标线程一旦不泵消息，同步 SetWindowPos
        /// **8000ms 都不返回**，只有结束那个进程才返回——而它本来只是个「顺手取消置顶」
        /// 的收尾动作。UI 线程卡在这里 = 双击 Ctrl 永远收不回去。
        /// </summary>
        public const uint SWP_ASYNCWINDOWPOS = 0x4000;

        // SetWindowPos 的特殊 hWndInsertAfter 值（IntPtr 不能做 const）
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        public const int WM_HOTKEY = 0x0312;
        public const uint WM_QUIT = 0x0012;
        public const int WM_CANCELMODE = 0x001F;
        public const uint WM_APP_TOGGLE = 0x8000 + 0x50;
        public const uint WM_APP_PASTE = 0x8000 + 0x51;
        public const uint WM_APP_REHOOK = 0x8000 + 0x52;

        public const int WH_KEYBOARD_LL = 13;
        public const int WH_MOUSE_LL = 14;

        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;

        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONDOWN = 0x0204;
        public const int WM_RBUTTONUP = 0x0205;
        public const int WM_MBUTTONDOWN = 0x0207;
        public const int WM_MBUTTONUP = 0x0208;
        public const int WM_MOUSEWHEEL = 0x020A;
        public const int WM_XBUTTONDOWN = 0x020B;
        public const int WM_MOUSEHWHEEL = 0x020E;

        public const int LLKHF_INJECTED = 0x10;
        public const int LLMHF_INJECTED = 0x01;

        public const int VK_SHIFT = 0x10;
        public const int VK_CONTROL = 0x11;
        public const int VK_MENU = 0x12;
        public const int VK_LWIN = 0x5B;
        public const int VK_RWIN = 0x5C;
        public const int VK_V = 0x56;
        public const int VK_LBUTTON = 0x01;
        public const int VK_RBUTTON = 0x02;
        public const int VK_MBUTTON = 0x04;
        public const int VK_XBUTTON1 = 0x05;
        public const int VK_XBUTTON2 = 0x06;
        public const int VK_ESCAPE = 0x1B;
        public const int VK_OEM_3 = 0xC0;

        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint INPUT_KEYBOARD = 1;

        /// <summary>
        /// 本工具自己注入按键时写在 dwExtraInfo 上的标记。低级键盘钩子看到这个标记就
        /// 直接放行、不参与双击 Ctrl 判定——否则「按一下 Ctrl 后 420ms 内右键粘贴」
        /// 会被自己注入的 Ctrl+V 补成第二次敲击。不能改用 LLKHF_INJECTED 过滤：自检
        /// 脚本、键鼠宏和远程桌面都用普通 SendInput，过滤所有注入事件会让它们全失效。
        /// </summary>
        public static readonly IntPtr DSQ_EXTRAINFO = new IntPtr(0x44535131);   // "DSQ1"

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;

        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

        public const uint SMTO_ABORTIFHUNG = 0x0002;

        /// <summary>空消息。用 SendMessageTimeout 发它 = 探测目标线程是否在泵消息。</summary>
        public const uint WM_NULL = 0x0000;

        public const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

        /// <summary>取窗口标题（失败返回空串，绝不抛异常）。</summary>
        public static string TitleOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "";
            try
            {
                StringBuilder sb = new StringBuilder(512);
                GetWindowTextW(hwnd, sb, sb.Capacity);
                return sb.ToString();
            }
            catch { return ""; }
        }

        /// <summary>枚举所有可见的顶层窗口，一行一个：hwnd / 类名 / 标题。
        /// 用来回答「屏幕上现在到底有什么」——排查时比猜可靠得多。</summary>
        public static string ListVisibleTopLevelWindows()
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                EnumWindows(delegate(IntPtr h, IntPtr l)
                {
                    if (!IsWindowVisible(h)) return true;
                    string t = TitleOf(h);
                    if (t.Length == 0) return true;
                    uint pid;
                    GetWindowThreadProcessId(h, out pid);
                    string proc = "?";
                    try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }
                    sb.AppendLine("  hwnd=0x" + h.ToInt64().ToString("X")
                        + "  " + proc + "  [" + ClassOf(h) + "]  " + t);
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return sb.ToString();
        }

        /// <summary>取窗口类名（失败返回空串）。</summary>
        public static string ClassOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "";
            try
            {
                StringBuilder sb = new StringBuilder(256);
                GetClassNameW(hwnd, sb, sb.Capacity);
                return sb.ToString();
            }
            catch { return ""; }
        }

        /// <summary>某个修饰键此刻是否按下（左/右任一）。</summary>
        public static bool IsDown(int vk)
        {
            if (vk == VK_CONTROL) return (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            if (vk == VK_SHIFT) return (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
            if (vk == VK_MENU) return (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
            if (vk == VK_LWIN) return (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        /// <summary>
        /// 此刻是否有任意鼠标按键按下（配合低级鼠标钩子识别 Ctrl+鼠标手势）。
        ///
        /// ⚠️ **不要用它做热键状态机的判据**：它读的是 GetAsyncKeyState，而右键粘贴
        /// 会吞掉 WM_RBUTTONUP，被吞掉的那次抬起之后 VK_RBUTTON 的 0x8000 位会
        /// **永远挂着**，这个函数从此恒为真。热键那边请用
        /// <see cref="IsAnyMouseButtonPhysicallyDown"/>。
        /// </summary>
        public static bool IsAnyMouseButtonDown()
        {
            return (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0
                || (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0
                || (GetAsyncKeyState(VK_MBUTTON) & 0x8000) != 0
                || (GetAsyncKeyState(VK_XBUTTON1) & 0x8000) != 0
                || (GetAsyncKeyState(VK_XBUTTON2) & 0x8000) != 0;
        }

        // ── 鼠标按键的「共享真实状态」─────────────────────────────────
        //
        // 为什么需要它：进程里有**两条**低级鼠标钩子——
        //   * HotkeyManager 的手势守卫钩子（装在专用钩子线程）
        //   * BrowserHost 的右键粘贴钩子（装在 UI 线程）
        // 钩子链是**后装的先跑**，而右键粘贴钩子通常后装 → 它先看到 WM_RBUTTONUP
        // 并 `return 1` 吞掉 → **守卫钩子根本看不到那次抬起**。所以只靠守卫钩子
        // 自己记账，右键按下的状态会一直挂着；而它读 GetAsyncKeyState 同样中毒。
        // 两边都必须走这份共享状态，并且由**吞掉抬起的那一方**主动清掉。
        private static volatile bool _mouseL, _mouseR, _mouseM;
        private static volatile int _swallowedRmbUp;

        /// <summary>低级鼠标钩子记账：按键的真实物理状态（钩子看得最全，不受吞事件影响）。</summary>
        public static void NoteMouseButton(int msg, bool down)
        {
            if (msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP) _mouseL = down;
            else if (msg == WM_RBUTTONDOWN || msg == WM_RBUTTONUP) _mouseR = down;
            else if (msg == WM_MBUTTONDOWN || msg == WM_MBUTTONUP) _mouseM = down;
        }

        /// <summary>
        /// 右键粘贴吞掉了一次 WM_RBUTTONUP：告诉所有人这颗键**物理上已经松开**。
        /// 不这么做，守卫方的判据还会以为右键一直按着 → 第一敲永不武装 →
        /// 此后每一次双击 Ctrl 都被静默忽略（用户症状：右键一次之后收不回去）。
        /// </summary>
        public static void NoteRmbUpSwallowed()
        {
            _mouseR = false;
            Interlocked.Increment(ref _swallowedRmbUp);
        }

        public static int SwallowedRmbUps { get { return _swallowedRmbUp; } }

        /// <summary>把共享鼠标按键状态清干净（切换热键 / 卸载钩子时调用，避免陈旧状态）。</summary>
        public static void ResetMouseButtonState()
        {
            _mouseL = _mouseR = _mouseM = false;
        }

        /// <summary>
        /// 供热键状态机使用的判据：真实物理状态 + 共享记账，**不会被吞事件或
        /// GetAsyncKeyState 残留带偏**。
        /// </summary>
        public static bool IsAnyMouseButtonPhysicallyDown()
        {
            if (_mouseL || _mouseR || _mouseM) return true;
            // 中键/侧键从不被我们吞掉，GetAsyncKeyState 对它们可信。
            return (GetAsyncKeyState(VK_XBUTTON1) & 0x8000) != 0
                || (GetAsyncKeyState(VK_XBUTTON2) & 0x8000) != 0;
        }

        /// <summary>Win 键（左或右）此刻是否按下。</summary>
        public static bool IsWinDown()
        {
            return (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
        }

        /// <summary>
        /// 当前进程的完整性级别 RID：0x1000=Low，0x2000=Medium，0x3000=High。
        /// 低完整性进程的全局低级钩子收不到普通输入（UIPI 限制），呼出/收起热键
        /// 会完全失效——很多「沙箱/自动化工具里启动后热键没反应」就是这个原因。
        /// 读取失败返回 0。
        /// </summary>
        public static int CurrentIntegrityRid()
        {
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out token)) return 0;
                int len;
                GetTokenInformation(token, 25, IntPtr.Zero, 0, out len);
                if (len <= 0) return 0;
                IntPtr buf = Marshal.AllocHGlobal(len);
                try
                {
                    int ret;
                    if (!GetTokenInformation(token, 25, buf, len, out ret)) return 0;
                    TOKEN_MANDATORY_LABEL label = (TOKEN_MANDATORY_LABEL)Marshal.PtrToStructure(
                        buf, typeof(TOKEN_MANDATORY_LABEL));
                    IntPtr sub = GetSidSubAuthority(label.Label.Sid, 0);
                    if (sub == IntPtr.Zero) return 0;
                    return Marshal.ReadInt32(sub);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { return 0; }
            finally
            {
                if (token != IntPtr.Zero) CloseHandle(token);
            }
        }
    }
}
