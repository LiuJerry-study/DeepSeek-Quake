// DeepSeek Quake —— 热键引擎（v3：支持多热键 + 无托盘可用）
//
// 两种触发形态：
//   Chord     ：RegisterHotKey 注册全局组合键（Win+~、Ctrl+Alt+Space …）
//   DoubleTap ：低级键盘钩子识别「双击 Ctrl / Shift / Alt / Win」
//
// v3 新增：除了呼出/收起，还能再注册几个**独立动作热键**。
// 为什么需要：本机 explorer.exe 没有运行（用户用的是 Themia 自定义外壳），
// 系统里根本没有任务栏和通知区域 —— 托盘图标无处可放。
// 所以「改设置 / 看诊断」不能只靠托盘右键，必须有不依赖界面的入口。
//
// 设计要点：
//   * 双击识别用低级键盘钩子；钩子回调里绝不做重活，只投递消息，
//     否则回调超时会被 Windows 静默摘钩（而诊断还报「已安装」）。
//   * 收到别的键就作废「待判定的一次敲击」，Ctrl+C 里的 Ctrl 不会被误判成双击。
//   * 合成按键（SendInput / 宏 / 远程桌面）不过滤：verify-quake.ps1 要用它做
//     端到端自检；但**工具自身注入的 Ctrl+V 带 DSQ_EXTRAINFO 标记**，会被
//     钩子直接跳过，不会在「按一下 Ctrl 后马上右键粘贴」时补成第二次敲击。
//   * 双击模式同时挂一个只写脏标记的低级鼠标钩子：Ctrl+滚轮 / Ctrl+左键 /
//     Ctrl+拖拽 的 Ctrl 抬起不会被当成一次干净敲击（审计 R5）。
//   * 抖动过滤：两次敲击间隔 < 40ms 视为同一次物理按下的重复事件；触发后先等
//     这次 Ctrl 抬起，避免「第三下」立刻又算新的一组双击。
//   * 钩子被摘掉能自动装回来（EnsureKeyboardHookAlive）。

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace DeepSeekQuake
{
    internal sealed class HotkeyManager : IDisposable
    {
        private const int HOTKEY_ID = 1;
        private const int ACTION_ID_BASE = 10;      // 动作热键从 10 开始编号

        private readonly Action _onTrigger;
        private readonly Action _onTriggerDeferred;    // 从低级钩子里触发时用：只投递消息，不做重活
        private readonly Log _log;

        private IntPtr _msgHwnd = IntPtr.Zero;
        private bool _registered;
        private TriggerSpec _active;
        private readonly List<ActionHotkey> _actions = new List<ActionHotkey>();

        // 双击识别
        private IntPtr _kbdHook = IntPtr.Zero;
        private Native.LowLevelProc _kbdProc;
        private IntPtr _mouseHook = IntPtr.Zero;
        private Native.LowLevelProc _mouseProc;
        private volatile int _tapVk;        // 正在等第二次敲击的修饰键
        private volatile int _lastTapTick;
        private volatile bool _tapPending;  // 第一次敲击已完整结束，等第二次
        private volatile bool _tapDirty;    // 这一次按住期间夹了别的键 / 鼠标动作 → 不能算「干净的一敲」
        private bool _awaitRelease;         // 刚触发过，等这次 Ctrl 抬起；抬起只复位，不再武装新的一敲
        private bool _modifierHeld;         // 目标修饰键当前是否物理按住（用来忽略系统自动重复的 down）
        private volatile int _doubleTapMs = 420;
        private const int MIN_TAP_GAP_MS = 40;   // 更近的第二次 down 视作同一次物理按下的抖动

        // 钩子必须活在一个有消息循环的独立线程上：如果和 UI 同线程，
        // 呼出/收起这种带 Sleep 的重活会把钩子回调一起堵住；Windows 等超时后
        // 会**静默摘掉**钩子（SetWindowsHookEx 的句柄还在，API 也查不出来），
        // 用户看到的就是「按了几下之后热键完全没反应」。
        private Thread _hookThread;
        private uint _hookThreadId;
        private ManualResetEvent _hookReady;
        private volatile bool _hookStop;
        private string _hookInstallError;

        // 计数（诊断用）
        private int _doubleTapFires;
        private int _chordFires;
        private int _hookReinstalls;
        private int _hookInstallAttempts;
        private int _hookEvents;            // 钩子收到过多少个键盘事件（任何键）
        private int _modEvents;             // 其中有多少个是目标修饰键的按下/抬起
        private int _lastHookEventTick;

        private sealed class ActionHotkey
        {
            public int Id;
            public TriggerSpec Spec;
            public Action Handler;
            public string Label;
            public bool Registered;
        }

        public HotkeyManager(Action onTrigger, Log log) : this(onTrigger, null, log) { }

        /// <summary>
        /// onTriggerDeferred：从**低级键盘钩子回调**里触发时走这个。
        /// 钩子回调必须尽快返回，否则 Windows 会静默把钩子摘掉
        /// （而 KeyboardHookInstalled 还报 true，用户只看到「按了没反应」）。
        /// 所以这里传进去的应该只是「PostMessage 一条消息」这种动作。
        /// </summary>
        public HotkeyManager(Action onTrigger, Action onTriggerDeferred, Log log)
        {
            _onTrigger = onTrigger;
            _onTriggerDeferred = onTriggerDeferred;
            _log = log;
        }

        public string ActivePretty { get { return _active == null ? "(无)" : _active.Pretty(); } }
        public bool ChordRegistered { get { return _registered; } }
        public bool KeyboardHookInstalled
        {
            get { return _kbdHook != IntPtr.Zero && _hookThread != null && _hookThread.IsAlive; }
        }
        public bool HookThreadAlive { get { return _hookThread != null && _hookThread.IsAlive; } }
        /// <summary>鼠标活动守卫钩子（双击 Ctrl 时用来过滤 Ctrl+鼠标手势）是否在线。</summary>
        public bool MouseGuardInstalled
        {
            get { return _mouseHook != IntPtr.Zero && _hookThread != null && _hookThread.IsAlive; }
        }
        public int DoubleTapMs
        {
            get { return _doubleTapMs; }
            set { _doubleTapMs = Math.Max(150, Math.Min(900, value)); }
        }
        public TriggerSpec Active { get { return _active; } }
        public int DoubleTapFires { get { return _doubleTapFires; } }
        public int ChordFires { get { return _chordFires; } }
        public int HookReinstalls { get { return _hookReinstalls; } }
        public int HookEvents { get { return _hookEvents; } }
        public int ModifierEvents { get { return _modEvents; } }
        public int HookInstallAttempts { get { return _hookInstallAttempts; } }

        /// <summary>已注册成功的「动作热键」列表，用于托盘提示 / 诊断显示。</summary>
        public List<string> RegisteredActionLabels
        {
            get
            {
                List<string> list = new List<string>();
                foreach (ActionHotkey a in _actions)
                    if (a.Registered) list.Add(a.Label + " → " + a.Spec.Pretty());
                return list;
            }
        }

        public void AttachWindow(IntPtr msgHwnd) { _msgHwnd = msgHwnd; }

        /// <summary>
        /// 取消「正在等第二次敲击」的状态。右键粘贴注入 Ctrl+V 之前调用：
        /// 自己合成的 Ctrl 不能被状态机当成用户按下的第二下；同时把 dirty 置位，
        /// 这样若此刻物理 Ctrl 还按着，它抬起时也不会重新武装。
        /// </summary>
        public void SuppressPendingTap()
        {
            _tapPending = false;
            _tapDirty = true;
            _lastTapTick = 0;
        }

        /// <summary>
        /// 注册一个独立动作热键（例如 Ctrl+Alt+S 打开设置）。
        /// 注册不上不算致命错误：记一笔日志就好，不影响主热键。
        /// </summary>
        public void AddActionHotkey(string spec, string label, Action handler)
        {
            TriggerSpec s = TriggerSpec.Parse(spec);
            if (!s.IsValid || s.Kind != TriggerKind.Chord)
            {
                if (!string.IsNullOrEmpty(spec) && _log != null)
                    _log.Add("动作热键写法无效，已忽略: " + spec);
                return;
            }
            ActionHotkey a = new ActionHotkey();
            a.Id = ACTION_ID_BASE + _actions.Count;
            a.Spec = s;
            a.Label = label;
            a.Handler = handler;
            _actions.Add(a);
        }

        /// <summary>
        /// 按顺序尝试候选热键，用第一个能生效的。
        /// 返回失败原因；成功时返回 null。
        /// </summary>
        public string Apply(string configured, string[] chordFallbacks)
        {
            Release();

            List<TriggerSpec> candidates = new List<TriggerSpec>();
            TriggerSpec want = TriggerSpec.Parse(configured);
            if (want.IsValid) candidates.Add(want);
            if (chordFallbacks != null)
            {
                foreach (string f in chordFallbacks)
                {
                    if (string.IsNullOrEmpty(f)) continue;
                    TriggerSpec c = TriggerSpec.Parse(f);
                    if (c.IsValid) candidates.Add(c);
                }
            }

            List<string> failures = new List<string>();
            foreach (TriggerSpec c in candidates)
            {
                if (c.Kind == TriggerKind.Chord)
                {
                    int err;
                    if (RegisterChord(c, out err))
                    {
                        _active = c;
                        _log.Add("热键已注册: " + c.Pretty() + "  (配置值 " + c.ToConf() + ")");
                        RegisterActions();
                        return null;
                    }
                    string why = err == Native.ERROR_HOTKEY_ALREADY_REGISTERED
                        ? "已被别的程序占用(1409)" : "注册失败 err=" + err;
                    failures.Add(c.Pretty() + " " + why);
                    _log.Add("热键 " + c.Pretty() + " " + why);
                }
                else
                {
                    string why = InstallKeyboardHook();
                    if (why == null)
                    {
                        _active = c;
                        _tapVk = c.TapModifierVk;
                        _log.Add("热键已启用: " + c.Pretty() + "（低级键盘钩子，间隔 " + _doubleTapMs + "ms）");
                        RegisterActions();
                        return null;
                    }
                    failures.Add(c.Pretty() + " " + why);
                    _log.Add("热键 " + c.Pretty() + " " + why);
                }
            }

            _active = null;
            string msg = failures.Count == 0
                ? "hotkey.conf 里的热键写法无法识别: " + configured
                : string.Join("；", failures.ToArray());
            _log.Add("所有热键候选都失败了: " + msg);
            return msg;
        }

        private void RegisterActions()
        {
            foreach (ActionHotkey a in _actions)
            {
                if (a.Registered) continue;
                if (_msgHwnd == IntPtr.Zero) return;
                if (Native.RegisterHotKey(_msgHwnd, a.Id, a.Spec.Modifiers | Native.MOD_NOREPEAT, a.Spec.VirtualKey))
                {
                    a.Registered = true;
                    _log.Add("动作热键已注册: " + a.Label + " = " + a.Spec.Pretty());
                }
                else
                {
                    _log.Add("动作热键注册失败（不影响主热键）: " + a.Label + " = " + a.Spec.Pretty()
                        + " err=" + Marshal.GetLastWin32Error());
                }
            }
        }

        private bool RegisterChord(TriggerSpec c, out int err)
        {
            err = 0;
            if (_msgHwnd == IntPtr.Zero) { err = -1; return false; }
            bool ok = Native.RegisterHotKey(_msgHwnd, HOTKEY_ID, c.Modifiers | Native.MOD_NOREPEAT, c.VirtualKey);
            if (ok) { _registered = true; return true; }
            err = Marshal.GetLastWin32Error();
            return false;
        }

        /// <summary>
        /// 在**独立线程**上安装并运行低级键盘钩子。
        ///
        /// 低级钩子的回调由安装它的线程负责调用；如果安装在线程池或 UI 线程上，
        /// 只要那个线程在忙（例如呼出窗口时同步等待 Chrome 响应），回调就会被推迟；
        /// 推迟超过系统 LowLevelHooksTimeout（默认约 300ms）后，Windows 会悄悄
        /// 把钩子摘掉，而且不改变 SetWindowsHookEx 返回的句柄——诊断里 khook 依旧
        /// 显示 1，用户再按却永远没反应。这就是旧版真正的主因。
        ///
        /// 独立线程只做两件事：收钩子回调、PostMessage；真正的窗口操作在 UI 线程做。
        /// 这样回调永远在几毫秒内返回，钩子不会再因超时被摘。
        /// </summary>
        private string InstallKeyboardHook()
        {
            if (_kbdHook != IntPtr.Zero && _hookThread != null && _hookThread.IsAlive && !_hookStop)
                return null;

            StopKeyboardHookThread();
            _hookInstallAttempts++;
            _hookInstallError = null;
            _hookReady = new ManualResetEvent(false);
            _hookStop = false;
            _kbdProc = KeyboardProc;
            _mouseProc = MouseProc;

            Thread t = new Thread(new ThreadStart(HookThreadMain));
            t.IsBackground = true;
            t.Name = "DeepSeekQuake keyboard hook";
            _hookThread = t;
            t.Start();

            if (!_hookReady.WaitOne(2000))
            {
                _hookInstallError = "低级键盘钩子线程启动超时";
                StopKeyboardHookThread();
                return _hookInstallError;
            }
            if (_kbdHook == IntPtr.Zero)
            {
                string why = _hookInstallError ?? "低级键盘钩子安装失败";
                _hookThread = null;
                return why;
            }
            return null;
        }

        /// <summary>钩子线程本体：装钩子 + 只收发消息，不干重活。</summary>
        private void HookThreadMain()
        {
            try
            {
                _hookThreadId = Native.GetCurrentThreadId();
                if (_hookStop) return;

                IntPtr h = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _kbdProc,
                    Native.GetModuleHandle(null), 0);
                if (h == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    _hookInstallError = "低级键盘钩子安装失败 err=" + err
                        + "（可能被安全软件拦截）";
                    if (_log != null) _log.Add(_hookInstallError);
                    return;
                }

                if (_hookStop)
                {
                    Native.UnhookWindowsHookEx(h);
                    return;
                }

                _kbdHook = h;
                _tapPending = false;
                _tapDirty = false;
                _awaitRelease = false;
                _modifierHeld = false;
                _lastTapTick = 0;

                // 再挂一个只写脏标记的鼠标钩子（同为低级钩子，回调只写 bool）。
                // 装不上不影响双击：只是 Ctrl+鼠标手势的过滤少一层，
                // HandleKey 里还有 GetAsyncKeyState 兜底。
                IntPtr mh = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc,
                    Native.GetModuleHandle(null), 0);
                if (mh == IntPtr.Zero)
                {
                    if (_log != null) _log.Add("鼠标活动守卫钩子安装失败 err="
                        + Marshal.GetLastWin32Error() + "（双击仍可用，鼠标手势过滤降级）");
                }
                else
                {
                    _mouseHook = mh;
                }

                if (_hookReady != null) _hookReady.Set();   // 安装方在等这个信号
                if (_log != null) _log.Add("低级键盘钩子已在线程 " + _hookThreadId + " 上安装"
                    + (_mouseHook != IntPtr.Zero ? "（含鼠标守卫钩子）" : ""));

                Native.MSG msg;
                while (!_hookStop)
                {
                    int r = Native.GetMessage(out msg, IntPtr.Zero, 0, 0);
                    if (r <= 0) break;        // WM_QUIT 或出错
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }
            }
            catch (Exception ex)
            {
                if (_log != null) _log.Add("键盘钩子线程异常: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                IntPtr h = _kbdHook;
                _kbdHook = IntPtr.Zero;
                if (h != IntPtr.Zero)
                {
                    try { Native.UnhookWindowsHookEx(h); } catch { }
                }
                IntPtr mh = _mouseHook;
                _mouseHook = IntPtr.Zero;
                if (mh != IntPtr.Zero)
                {
                    try { Native.UnhookWindowsHookEx(mh); } catch { }
                }
                _hookThreadId = 0;
                if (_log != null) _log.Add("低级键盘钩子线程已退出");
                if (_hookReady != null) _hookReady.Set();   // 让安装方别死等
            }
        }

        private void StopKeyboardHookThread()
        {
            try
            {
                _hookStop = true;
                uint tid = _hookThreadId;
                if (tid != 0) Native.PostThreadMessage(tid, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);

                IntPtr h = _kbdHook;
                if (h != IntPtr.Zero)
                {
                    // 先摘钩，确保即使线程卡住也不会再收到事件
                    try { Native.UnhookWindowsHookEx(h); } catch { }
                    _kbdHook = IntPtr.Zero;
                }
                IntPtr mh = _mouseHook;
                if (mh != IntPtr.Zero)
                {
                    try { Native.UnhookWindowsHookEx(mh); } catch { }
                    _mouseHook = IntPtr.Zero;
                }

                Thread t = _hookThread;
                if (t != null && t.IsAlive && t.ManagedThreadId != Thread.CurrentThread.ManagedThreadId)
                    t.Join(1000);
            }
            catch { }
            _hookThread = null;
            _hookThreadId = 0;
            _kbdHook = IntPtr.Zero;
            _mouseHook = IntPtr.Zero;
        }

        /// <summary>手动重组一遍钩子；设置里改热键、诊断按钮都调用它。</summary>
        public string ReinstallKeyboardHook()
        {
            if (_active == null || _active.Kind != TriggerKind.DoubleTap) return null;
            StopKeyboardHookThread();
            return InstallKeyboardHook();
        }

        /// <summary>
        /// UI 定时器每个心跳调用一次。独立线程本身已能避免超时摘钩，这里再兜一层：
        /// 线程死了 / 句柄没了就重装。注意：Windows 静默摘钩时句柄仍非零，
        /// 所以**必须同时检查线程存活**，只查句柄是查不出来的。
        /// </summary>
        public void EnsureKeyboardHookAlive()
        {
            if (_active == null || _active.Kind != TriggerKind.DoubleTap) return;
            bool alive = _kbdHook != IntPtr.Zero
                && _hookThread != null && _hookThread.IsAlive && !_hookStop;
            if (alive) return;

            string why = InstallKeyboardHook();
            if (why == null)
            {
                _hookReinstalls++;
                if (_log != null) _log.Add("检测到键盘钩子线程不在线，已自动重装（第 "
                    + _hookReinstalls + " 次）");
            }
            else if (_log != null)
            {
                _log.Add("键盘钩子重装失败: " + why);
            }
        }

        private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                // 先计数再判 nCode：只有这样才能区分「回调根本没被调用」和
                // 「被调用了但 nCode<0 / 解析失败」。
                Interlocked.Increment(ref _hookEvents);
                _lastHookEventTick = Environment.TickCount;
                if (nCode >= 0)
                {
                    Native.KBDLLHOOKSTRUCT k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));

                    // 不能按 LLKHF_INJECTED 过滤合成按键：
                    //   * 项目自带的 verify-quake.ps1 用 SendInput 合成「连按两下 Ctrl」，
                    //     过滤掉注入事件会让这条自检永远失败；
                    //   * 远程桌面 / 键鼠宏 / 无障碍工具注入的按键同理。
                    // 但工具自己注入的 Ctrl+V 带 DSQ_EXTRAINFO 标记：直接跳过，
                    // 否则「按一下 Ctrl 后 420ms 内右键粘贴」会被它补成双击。
                    if (k.dwExtraInfo == Native.DSQ_EXTRAINFO)
                        return Native.CallNextHookEx(_kbdHook, nCode, wParam, lParam);

                    int msg = wParam.ToInt32();
                    bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
                    bool up = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;
                    if (down || up) HandleKey(k.vkCode, down);
                }
            }
            catch (Exception ex)
            {
                if (_log != null) _log.Add("键盘钩子回调异常: " + ex.Message);
            }
            return Native.CallNextHookEx(_kbdHook, nCode, wParam, lParam);
        }

        /// <summary>
        /// 低级鼠标钩子：双击 Ctrl 期间，任何鼠标按键 / 滚轮动作都让这一次 Ctrl 按住作废。
        /// 这样 Ctrl+滚轮缩放、Ctrl+左键新标签、Ctrl+拖拽 的 Ctrl 抬起不会进入
        /// 「已敲了一下、等第二下」的状态。回调只写 bool，绝不做重活。
        /// </summary>
        private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    if (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN
                        || msg == Native.WM_MBUTTONDOWN || msg == Native.WM_XBUTTONDOWN
                        || msg == Native.WM_MOUSEWHEEL || msg == Native.WM_MOUSEHWHEEL)
                    {
                        _tapDirty = true;
                        if (_tapPending)
                        {
                            // 两次敲击之间插入了鼠标动作 → 上一敲作废，不能拼成双击
                            _tapPending = false;
                            _lastTapTick = 0;
                        }
                    }
                }
            }
            catch { }
            return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private void HandleKey(uint vk, bool down)
        {
            if (_tapVk == 0) return;

            bool isOurModifier = IsSameModifier((int)vk, _tapVk);
            if (isOurModifier) Interlocked.Increment(ref _modEvents);

            // 别的修饰键：不参与判定，也不作废当前这一次按住
            if (!isOurModifier && IsModifier((int)vk)) return;

            if (down)
            {
                if (isOurModifier)
                {
                    // 系统自动重复会连续发 down：只要修饰键还按着，后续 down 不是新的一次敲击。
                    // 不忽略的话，第二次 Ctrl 按住稍久会把 _awaitRelease 清掉，抬起时又武装一敲。
                    if (_modifierHeld) return;
                    _modifierHeld = true;

                    // 触发后一直没等到抬起（异常键序）：先清标志，按新的一轮处理
                    if (_awaitRelease) _awaitRelease = false;

                    int gap = _tapPending ? unchecked(Environment.TickCount - _lastTapTick) : -1;
                    if (_tapPending && gap >= MIN_TAP_GAP_MS && gap <= _doubleTapMs)
                    {
                        // 第二次敲击判定：上一次敲击已完整结束、间隔在时间窗内，
                        // 且不能近到像同一次物理按下的抖动（gap < MIN_TAP_GAP_MS）。
                        _tapPending = false;
                        _lastTapTick = 0;
                        _tapDirty = false;
                        _awaitRelease = true;      // 这次抬起只复位，不再武装新的第一敲
                        _doubleTapFires++;
                        if (_log != null) _log.Add("双击 " + TriggerSpec.ModifierName(_tapVk)
                            + " 触发（第 " + _doubleTapFires + " 次）");
                        FireDeferred();
                    }
                    else
                    {
                        // 第一次按下 / 上一次已超时 / 间隔太近像抖动：进入新的一轮按住
                        _tapPending = false;
                        _tapDirty = false;
                    }

                    // 鼠标键此刻还按着（Ctrl+拖拽之类）→ 这一轮也不算干净的一敲
                    if (Native.IsAnyMouseButtonDown()) _tapDirty = true;
                    return;
                }

                // 按住期间夹了别的键（Ctrl+A、Ctrl+C、Ctrl+Shift+T …）→ 这一轮不算干净的一敲。
                // 关键：不能只在按下时清 _tapPending 就完事——修饰键抬起时还会重新置位，
                // 那样「Ctrl+A 之后松开 Ctrl，再快速按一下 Ctrl」就会被误判成双击。
                _tapPending = false;
                _tapDirty = true;
                return;
            }

            // up
            if (isOurModifier)
            {
                _modifierHeld = false;
                if (_awaitRelease)
                {
                    // 刚刚触发过：这一次抬起只复位，不再把「第一敲」武装起来，
                    // 否则双击后 420ms 内顺手按一下 Ctrl 会再触发一次。
                    _awaitRelease = false;
                    _tapPending = false;
                    _tapDirty = false;
                    return;
                }

                if (_tapDirty || Native.IsAnyMouseButtonDown())
                {
                    _tapPending = false;      // 这一轮是组合键 / 鼠标手势，不算一次敲击
                }
                else
                {
                    _tapPending = true;
                    _lastTapTick = Environment.TickCount;
                }
                _tapDirty = false;
                return;
            }

            _tapPending = false;
        }

        private static bool IsModifier(int vk)
        {
            return vk == Native.VK_CONTROL || vk == 0xA2 || vk == 0xA3
                || vk == Native.VK_SHIFT || vk == 0xA0 || vk == 0xA1
                || vk == Native.VK_MENU || vk == 0xA4 || vk == 0xA5
                || vk == Native.VK_LWIN || vk == Native.VK_RWIN;
        }

        /// <summary>左右 Ctrl/Shift/Alt 视为同一个修饰键；Win 也左右归一。</summary>
        private static bool IsSameModifier(int vk, int tapVk)
        {
            if (tapVk == Native.VK_CONTROL) return vk == 0xA2 || vk == 0xA3 || vk == Native.VK_CONTROL;
            if (tapVk == Native.VK_SHIFT) return vk == 0xA0 || vk == 0xA1 || vk == Native.VK_SHIFT;
            if (tapVk == Native.VK_MENU) return vk == 0xA4 || vk == 0xA5 || vk == Native.VK_MENU;
            if (tapVk == Native.VK_LWIN) return vk == 0x5B || vk == 0x5C;
            return vk == tapVk;
        }

        private void Fire()
        {
            if (_onTrigger == null) return;
            try { _onTrigger(); }
            catch (Exception ex) { if (_log != null) _log.Add("热键处理异常: " + ex.Message); }
        }

        /// <summary>钩子回调里用：只投递，不干活。没有 deferred 回调时降级为直接调用（安全网）。</summary>
        private void FireDeferred()
        {
            Action a = _onTriggerDeferred ?? _onTrigger;
            if (a == null) return;
            try { a(); }
            catch (Exception ex) { if (_log != null) _log.Add("热键投递异常: " + ex.Message); }
        }

        /// <summary>由消息窗口在收到 WM_HOTKEY 时调用。wParam 是热键 id。</summary>
        public void OnHotkeyMessage(IntPtr id)
        {
            int hotkeyId = id.ToInt32();
            if (hotkeyId == HOTKEY_ID)
            {
                _chordFires++;
                if (_log != null) _log.Add("收到 WM_HOTKEY（" + ActivePretty + "，第 " + _chordFires + " 次）");
                Fire();
                return;
            }
            foreach (ActionHotkey a in _actions)
            {
                if (a.Id != hotkeyId || a.Handler == null) continue;
                if (_log != null) _log.Add("动作热键触发: " + a.Label);
                try { a.Handler(); }
                catch (Exception ex) { if (_log != null) _log.Add("动作热键处理异常: " + ex.Message); }
                return;
            }
            if (_log != null) _log.Add("收到未知热键 id=" + hotkeyId);
        }

        public void Release()
        {
            if (_msgHwnd != IntPtr.Zero)
            {
                if (_registered) { try { Native.UnregisterHotKey(_msgHwnd, HOTKEY_ID); } catch { } }
                foreach (ActionHotkey a in _actions)
                {
                    if (!a.Registered) continue;
                    try { Native.UnregisterHotKey(_msgHwnd, a.Id); } catch { }
                    a.Registered = false;
                }
            }
            _registered = false;

            StopKeyboardHookThread();
            _kbdProc = null;
            _mouseProc = null;
            _tapPending = false;
            _tapDirty = false;
            _awaitRelease = false;
            _modifierHeld = false;
            _tapVk = 0;
        }

        public void Dispose() { Release(); }
    }
}
