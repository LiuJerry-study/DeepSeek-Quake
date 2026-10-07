// DeepSeek Quake —— 设置窗口
//
// 用户最关心的三件事之一：怎么方便地改快捷键。
// 所以这里做的是「按键即录」：点一下录制框，直接按下你想要的组合键，
// 立刻显示成 Win+Shift+~ 这样的名字，点保存就生效——不用手改配置文件。
//
// 保存流程（重要）：
//   1. 组合键先在本进程里试注册，注册不上（被占用）就直接报错、不落盘，
//      这样不会出现「存了个用不了的键，重启后热键还是没反应」。
//   2. 通过 src\config-write.ps1 写配置——小工具本身不写文件（火绒会拦未签名程序写文件）。
//   3. 组合键/双击这类热键改动立刻在当前进程生效；url / 浏览器 / 尺寸 改动需要重启，
//      由用户在窗口里点「保存并重启」显式触发。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace DeepSeekQuake
{
    internal sealed class SettingsForm : Form
    {
        private TextBox _hotkeyBox;
        private Label _hint;
        private CheckBox _doubleTap;
        private NumericUpDown _doubleTapMs;
        private CheckBox _rightClick;
        private CheckBox _restoreFocus;
        private ComboBox _browser;
        private TextBox _url;
        private NumericUpDown _width;
        private NumericUpDown _height;
        private Button _save;
        private Button _saveRestart;
        private Button _cancel;

        private uint _pendingMods;
        private uint _pendingVk;
        private string _pendingPreview = "";
        private bool _pendingDoubleTap;
        private string _pendingDoubleTapConf = "DOUBLECTRL";   // 双击模式具体是哪个修饰键，不能丢

        private readonly List<BrowserInfo> _browsers = new List<BrowserInfo>();
        // 与 _browser 下拉项一一对应；索引 0 是 auto，后面是 chrome / edge / 自定义路径。
        private readonly List<string> _browserKeys = new List<string>();

        public SettingsForm()
        {
            BuildUi();
            LoadValues();
        }

        // ── 界面 ───────────────────────────────────────────────────
        private void BuildUi()
        {
            Text = Program.Title + " 设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(520, 430);
            Font = new Font("Microsoft YaHei UI", 9f);
            Icon = SystemIcons.Shield;

            int y = 14;

            // ── 呼出热键 ──
            Label l1 = new Label();
            l1.Text = "呼出 / 收起热键";
            l1.SetBounds(14, y, 120, 20);
            Controls.Add(l1);

            _hotkeyBox = new TextBox();
            _hotkeyBox.SetBounds(150, y - 2, 220, 24);
            _hotkeyBox.ReadOnly = true;
            _hotkeyBox.TabStop = true;
            _hotkeyBox.Text = "点这里，然后按下你要的组合键";
            _hotkeyBox.KeyDown += HotkeyBoxKeyDown;
            _hotkeyBox.Enter += delegate { _hotkeyBox.BackColor = Color.FromArgb(255, 252, 220); };
            _hotkeyBox.Leave += delegate { _hotkeyBox.BackColor = SystemColors.Window; };
            Controls.Add(_hotkeyBox);

            Button clear = new Button();
            clear.Text = "清空";
            clear.SetBounds(378, y - 3, 60, 25);
            clear.Click += delegate
            {
                _pendingMods = 0; _pendingVk = 0; _pendingPreview = "";
                _pendingDoubleTap = false;
                _pendingDoubleTapConf = "DOUBLECTRL";
                if (_doubleTap.Checked) _doubleTap.Checked = false;
                _hotkeyBox.Text = "点这里，然后按下你要的组合键";
                _hint.Text = "要求：至少一个修饰键（Ctrl / Alt / Shift / Win）+ 一个普通键。";
            };
            Controls.Add(clear);

            y += 30;
            _doubleTap = new CheckBox();
            _doubleTap.Text = "改用「双击 Ctrl」呼出（不用记组合键，推荐）";
            _doubleTap.SetBounds(150, y, 300, 22);
            _doubleTap.CheckedChanged += delegate
            {
                if (_doubleTap.Checked)
                {
                    TriggerSpec dt = TriggerSpec.Parse(_pendingDoubleTapConf);
                    if (!dt.IsValid || dt.Kind != TriggerKind.DoubleTap)
                    {
                        _pendingDoubleTapConf = "DOUBLECTRL";
                        dt = TriggerSpec.Parse(_pendingDoubleTapConf);
                    }
                    _pendingDoubleTap = true;
                    _pendingMods = 0; _pendingVk = 0; _pendingPreview = "";
                    _hotkeyBox.Text = dt.Pretty();
                    _hint.Text = "连按两下 " + TriggerSpec.ModifierName(dt.TapModifierVk)
                        + " 就呼出 / 收起。绑了其它键（如 Ctrl+C）时不会误触发。";
                }
                else
                {
                    _pendingDoubleTap = false;
                    _hotkeyBox.Text = "点这里，然后按下你要的组合键";
                }
            };
            Controls.Add(_doubleTap);

            y += 26;
            Label l2 = new Label();
            l2.Text = "双击间隔（毫秒）";
            l2.SetBounds(150, y + 3, 110, 20);
            Controls.Add(l2);
            _doubleTapMs = new NumericUpDown();
            _doubleTapMs.SetBounds(262, y, 70, 24);
            _doubleTapMs.Minimum = 150;
            _doubleTapMs.Maximum = 900;
            _doubleTapMs.Increment = 20;
            _doubleTapMs.Value = 420;
            Controls.Add(_doubleTapMs);

            y += 30;
            _hint = new Label();
            _hint.SetBounds(150, y, 350, 34);
            _hint.ForeColor = Color.DimGray;
            _hint.Text = "要求：至少一个修饰键（Ctrl / Alt / Shift / Win）+ 一个普通键。";
            Controls.Add(_hint);

            y += 44;
            Controls.Add(MakeSeparator(y));
            y += 12;

            // ── 功能开关 ──
            _rightClick = new CheckBox();
            _rightClick.Text = "窗口里点右键 = 直接粘贴剪贴板（Shift+右键 = 原生菜单）";
            _rightClick.SetBounds(14, y, 480, 22);
            Controls.Add(_rightClick);

            y += 26;
            _restoreFocus = new CheckBox();
            _restoreFocus.Text = "收起后把焦点还给之前的窗口";
            _restoreFocus.SetBounds(14, y, 480, 22);
            Controls.Add(_restoreFocus);

            y += 34;
            Controls.Add(MakeSeparator(y));
            y += 12;

            // ── 浏览器与页面 ──
            Label l3 = new Label();
            l3.Text = "浏览器外壳";
            l3.SetBounds(14, y + 3, 90, 20);
            Controls.Add(l3);

            _browser = new ComboBox();
            _browser.DropDownStyle = ComboBoxStyle.DropDownList;
            _browser.SetBounds(150, y, 300, 24);
            Controls.Add(_browser);
            y += 30;

            Label l4 = new Label();
            l4.Text = "网址";
            l4.SetBounds(14, y + 3, 90, 20);
            Controls.Add(l4);
            _url = new TextBox();
            _url.SetBounds(150, y, 300, 24);
            Controls.Add(_url);
            y += 30;

            Label l5 = new Label();
            l5.Text = "窗口尺寸";
            l5.SetBounds(14, y + 3, 90, 20);
            Controls.Add(l5);
            _width = new NumericUpDown();
            _width.SetBounds(150, y, 80, 24);
            _width.Minimum = 320; _width.Maximum = 4000; _width.Increment = 20;
            Controls.Add(_width);
            Label lx = new Label();
            lx.Text = "×";
            lx.SetBounds(236, y + 3, 16, 20);
            Controls.Add(lx);
            _height = new NumericUpDown();
            _height.SetBounds(254, y, 80, 24);
            _height.Minimum = 240; _height.Maximum = 4000; _height.Increment = 20;
            Controls.Add(_height);

            // ── 底部按钮 ──
            _save = new Button();
            _save.Text = "保存";
            _save.SetBounds(150, 386, 90, 30);
            _save.Click += delegate { Save(false); };
            Controls.Add(_save);

            _saveRestart = new Button();
            _saveRestart.Text = "保存并重启";
            _saveRestart.SetBounds(250, 386, 100, 30);
            _saveRestart.Click += delegate { Save(true); };
            Controls.Add(_saveRestart);

            _cancel = new Button();
            _cancel.Text = "取消";
            _cancel.SetBounds(360, 386, 90, 30);
            _cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(_cancel);

            AcceptButton = _save;
            CancelButton = _cancel;
        }

        private static Control MakeSeparator(int y)
        {
            Label sep = new Label();
            sep.BorderStyle = BorderStyle.Fixed3D;
            sep.SetBounds(14, y, 490, 2);
            return sep;
        }

        // ── 载入当前值 ─────────────────────────────────────────────
        private void LoadValues()
        {
            string spec = Program.Get("hotkey", "DOUBLECTRL");
            TriggerSpec t = TriggerSpec.Parse(spec);
            if (t.Kind == TriggerKind.DoubleTap)
            {
                // 先把「具体是哪一个双击」记下来，再勾复选框（勾选事件会用它刷新显示）。
                // 否则 DOUBLESHIFT / DOUBLEALT 会在保存时被静默改写成 DOUBLECTRL。
                _pendingDoubleTapConf = t.ToConf();
                _doubleTap.Checked = true;
                _pendingDoubleTap = true;
                _hotkeyBox.Text = t.Pretty();
                _doubleTap.Text = "改用「" + t.Pretty() + "」呼出（不用记组合键，推荐）";
            }
            else if (t.IsValid)
            {
                _pendingMods = t.Modifiers;
                _pendingVk = t.VirtualKey;
                _pendingPreview = t.Pretty();
                _hotkeyBox.Text = t.Pretty();
            }
            _doubleTapMs.Value = Clamp(ParseNum(Program.Get("doubletapms", "420"), 420), 150, 900);
            _rightClick.Checked = Program.Get("rightclickpaste", "1") != "0";
            _restoreFocus.Checked = Program.Get("restorefocus", "1") != "0";
            _url.Text = Program.Get("url", "https://chat.deepseek.com");
            _width.Value = Clamp(ParseNum(Program.Get("width", "980"), 980), 320, 4000);
            _height.Value = Clamp(ParseNum(Program.Get("height", "820"), 820), 240, 4000);

            _browsers.Clear();
            _browsers.AddRange(BrowserHost.Discover());

            string cur = Program.Get("browser", "auto");
            int sel = 0;
            _browser.Items.Clear();
            _browserKeys.Clear();
            _browserKeys.Add("auto");
            _browser.Items.Add("自动（始终优先 Chrome）");
            for (int i = 0; i < _browsers.Count; i++)
            {
                _browserKeys.Add(_browsers[i].Key);
                _browser.Items.Add(_browsers[i].Display + " · " + _browsers[i].ExePath);
                if (_browsers[i].Key.Equals(cur, StringComparison.OrdinalIgnoreCase))
                    sel = _browserKeys.Count - 1;
            }

            // 手写的 browser=<exe 完整路径> 不在自动发现列表里：原样保留一项，
            // 否则用户只是改个网址点「保存」，便携版浏览器就会被静默换成 auto。
            if (!cur.Equals("auto", StringComparison.OrdinalIgnoreCase) && sel == 0)
            {
                _browserKeys.Add(cur);
                _browser.Items.Add("当前配置 · " + cur);
                sel = _browserKeys.Count - 1;
            }

            if (_browsers.Count == 0 && sel == 0)
            {
                _browserKeys.Add("");                       // 占位项：保存时按 auto 处理
                _browser.Items.Add("(没找到 Chrome / Edge)");
            }
            _browser.SelectedIndex = sel;
        }

        private static decimal Clamp(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        private static int ParseNum(string s, int def)
        {
            int n;
            return int.TryParse(s, out n) ? n : def;
        }

        // ── 按键即录 ───────────────────────────────────────────────
        private void HotkeyBoxKeyDown(object sender, KeyEventArgs e)
        {
            e.SuppressKeyPress = true;      // 别让按键跑到对话框的默认按钮上
            e.Handled = true;

            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete)
            {
                _pendingMods = 0; _pendingVk = 0; _pendingPreview = ""; _pendingDoubleTap = false;
                _hotkeyBox.Text = "点这里，然后按下你要的组合键";
                _hint.ForeColor = Color.DimGray;
                _hint.Text = "要「双击 Ctrl」的话，勾选下面的复选框。";
                return;
            }
            if (e.KeyCode == Keys.Escape) return;

            uint vk = TriggerSpec.VkFromKeyCode(e.KeyCode);
            if (vk == 0)
            {
                _hint.ForeColor = Color.Firebrick;
                _hint.Text = "这个键不支持（可用：A-Z、0-9、F1-F24、~ ` - = [ ] \\ ; ' , . / 空格 和方向键等）。";
                return;
            }

            uint mods = 0;
            if ((e.Modifiers & Keys.Control) != 0) mods |= Native.MOD_CONTROL;
            if ((e.Modifiers & Keys.Alt) != 0) mods |= Native.MOD_ALT;
            if ((e.Modifiers & Keys.Shift) != 0) mods |= Native.MOD_SHIFT;
            if ((Native.GetAsyncKeyState(Native.VK_LWIN) & 0x8000) != 0
                || (Native.GetAsyncKeyState(Native.VK_RWIN) & 0x8000) != 0) mods |= Native.MOD_WIN;

            if (mods == 0)
            {
                _hint.ForeColor = Color.Firebrick;
                _hint.Text = "必须带修饰键：按住 Ctrl / Alt / Shift / Win 再按主键。";
                return;
            }

            TriggerSpec spec = new TriggerSpec();
            spec.Kind = TriggerKind.Chord;
            spec.Modifiers = mods;
            spec.VirtualKey = vk;

            _pendingMods = mods;
            _pendingVk = vk;
            _pendingPreview = spec.Pretty();
            _pendingDoubleTap = false;
            if (_doubleTap.Checked) _doubleTap.Checked = false;   // 录组合键就自动取消双击模式
            _hotkeyBox.Text = _pendingPreview;
            _hint.ForeColor = Color.DimGray;
            _hint.Text = "已录制：" + _pendingPreview + "。按「保存」生效。";
        }

        // ── 保存 ───────────────────────────────────────────────────
        private void Save(bool restart)
        {
            string newSpec;
            TriggerSpec spec;
            if (_pendingDoubleTap)
            {
                spec = TriggerSpec.Parse(_pendingDoubleTapConf);
                if (!spec.IsValid || spec.Kind != TriggerKind.DoubleTap)
                {
                    _pendingDoubleTapConf = "DOUBLECTRL";
                    spec = TriggerSpec.Parse(_pendingDoubleTapConf);
                }
                newSpec = spec.ToConf();
            }
            else
            {
                if (_pendingVk == 0) { Warn("请先在上面的录制框里按一下你要的组合键，或勾选「双击 Ctrl」。"); return; }
                spec = new TriggerSpec();
                spec.Kind = TriggerKind.Chord;
                spec.Modifiers = _pendingMods;
                spec.VirtualKey = _pendingVk;
                if (!spec.IsValid) { Warn("这个组合键不合法，请重新录制。"); return; }
                newSpec = spec.ToConf();
            }

            // 组合键先在当前进程试注册：注册不上就别落盘，免得存了个用不了的键。
            //
            // 例外（旧版的致命 bug）：如果录的正好是**当前已经生效**的那个组合键，
            // 试注册必然返回 1409（同一个进程也不能重复注册同一个键），
            // 于是用户点「保存」永远被拦下，连改网址/浏览器都存不了。
            // 这种情况直接跳过试注册。
            if (spec.Kind == TriggerKind.Chord && !IsCurrentlyActiveChord(spec))
            {
                int err = TestRegister(spec);
                if (err != 0)
                {
                    string why = err == Native.ERROR_HOTKEY_ALREADY_REGISTERED
                        ? "已被别的程序占用" : "系统拒绝（错误码 " + err + "）";
                    Warn(spec.Pretty() + " " + why + "。\n请换一个组合键，或改用「双击 Ctrl」。");
                    return;
                }
            }

            bool needRestart = restart
                || !string.Equals(_url.Text.Trim(), Program.Get("url", "https://chat.deepseek.com"), StringComparison.OrdinalIgnoreCase)
                || BrowserSelectionChanged()
                || (int)_width.Value != ParseNum(Program.Get("width", "980"), 980)
                || (int)_height.Value != ParseNum(Program.Get("height", "820"), 820);

            List<string> sets = new List<string>();
            sets.Add("hotkey=" + newSpec);
            sets.Add("rightclickpaste=" + (_rightClick.Checked ? "1" : "0"));
            sets.Add("restorefocus=" + (_restoreFocus.Checked ? "1" : "0"));
            sets.Add("doubletapms=" + ((int)_doubleTapMs.Value));
            sets.Add("url=" + _url.Text.Trim());
            sets.Add("browser=" + SelectedBrowserKey());
            sets.Add("width=" + ((int)_width.Value));
            sets.Add("height=" + ((int)_height.Value));

            // 写进内存，让本次运行立刻用新设置
            Program.SetInMemory("hotkey", newSpec);
            Program.SetInMemory("rightclickpaste", _rightClick.Checked ? "1" : "0");
            Program.SetInMemory("restorefocus", _restoreFocus.Checked ? "1" : "0");
            Program.SetInMemory("doubletapms", ((int)_doubleTapMs.Value).ToString());
            Program.SetInMemory("url", _url.Text.Trim());
            Program.SetInMemory("browser", SelectedBrowserKey());
            Program.SetInMemory("width", ((int)_width.Value).ToString());
            Program.SetInMemory("height", ((int)_height.Value).ToString());

            // 落盘
            Program.SaveConfigAsync(needRestart ? Application.ExecutablePath : null, sets.ToArray());

            // 热键立刻生效（不用重启）
            Program.ApplyHotkeyNow();

            // 右键粘贴开关立刻生效
            if (Program.Host != null)
            {
                Program.Host.Configure(SelectedBrowserKey(), _url.Text.Trim(), Program.ProfilePath,
                    (int)_width.Value, (int)_height.Value, _rightClick.Checked, _restoreFocus.Checked);
                Program.Host.SyncMouseHook();
            }

            if (needRestart)
            {
                DialogResult = DialogResult.OK;
                Close();
                Program.RequestRestart();
                return;
            }

            MessageBox.Show(this,
                "已保存，热键 " + spec.Pretty() + " 立刻生效。\n\n"
                + "（url / 浏览器 / 尺寸 这类改动需要重启才生效，可以用「保存并重启」。）",
                Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }

        private bool BrowserSelectionChanged()
        {
            string cur = Program.Get("browser", "auto");
            return !string.Equals(SelectedBrowserKey(), cur, StringComparison.OrdinalIgnoreCase);
        }

        private string SelectedBrowserKey()
        {
            int i = _browser.SelectedIndex;
            if (i < 0 || i >= _browserKeys.Count) return "auto";
            string key = _browserKeys[i];
            return string.IsNullOrEmpty(key) ? "auto" : key;
        }

        /// <summary>录的组合键是不是当前正在生效的那个（是的话不用试注册）。</summary>
        private static bool IsCurrentlyActiveChord(TriggerSpec spec)
        {
            try
            {
                HotkeyManager hk = Program.Hotkeys;
                if (hk == null || hk.Active == null) return false;
                TriggerSpec cur = hk.Active;
                return cur.Kind == TriggerKind.Chord
                    && cur.Modifiers == spec.Modifiers
                    && cur.VirtualKey == spec.VirtualKey;
            }
            catch { return false; }
        }

        /// <summary>试注册一下组合键；返回 0 表示可用。</summary>
        private int TestRegister(TriggerSpec spec)
        {
            try
            {
                return Program.TryTestHotkey(spec);
            }
            catch (Exception ex)
            {
                Program.Diag.Add("试注册热键异常: " + ex.Message);
                return -1;
            }
        }

        private void Warn(string msg)
        {
            MessageBox.Show(this, msg, Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
