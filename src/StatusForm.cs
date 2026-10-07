// DeepSeek Quake —— 启动状态窗口
//
// 为什么要有这个窗口：
//   用户双击快捷方式后最怕的就是「启动之后啥都没有」——托盘图标小、不容易发现，
//   浏览器启动失败时更是完全静默。所以启动过程必须有一个**看得见的**窗口，
//   明确写出：小工具在跑、卡在哪一步、下一步该干什么。
//
//   * 浏览器窗口一就绪就自动关掉；
//   * 失败就停在那里，把具体原因和可点的按钮摆出来；
//   * 用户手动关掉也不影响小工具继续在托盘里跑。

using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace DeepSeekQuake
{
    internal sealed class StatusForm : Form
    {
        private Label _title;
        private Label _step1;
        private Label _step2;
        private Label _step3;
        private TextBox _detail;
        private Button _retry;
        private Button _settings;
        private Button _diagnose;
        private Button _close;
        private Label _hotkeyHint;

        private volatile bool _done;

        public StatusForm()
        {
            BuildUi();
            Shown += delegate { StartWatching(); };
        }

        private void BuildUi()
        {
            Text = Program.Title + " 正在启动";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(460, 340);
            Font = new Font("Microsoft YaHei UI", 9f);
            Icon = SystemIcons.Shield;
            TopMost = true;

            _title = new Label();
            _title.Text = "DeepSeek Quake 正在启动…";
            _title.Font = new Font(Font.FontFamily, 12f, FontStyle.Bold);
            _title.SetBounds(18, 16, 424, 26);
            Controls.Add(_title);

            _step1 = MakeStep("…  小工具已在运行", 18, 54);
            _step2 = MakeStep("…  正在查找 / 启动 DeepSeek 窗口", 18, 80);
            _step3 = MakeStep("…  准备热键", 18, 106);

            _detail = new TextBox();
            _detail.Multiline = true;
            _detail.ReadOnly = true;
            _detail.ScrollBars = ScrollBars.Vertical;
            _detail.SetBounds(18, 138, 424, 118);
            _detail.BackColor = Color.FromArgb(250, 250, 250);
            _detail.Font = new Font("Microsoft YaHei UI", 8.5f);
            _detail.Text = "";
            Controls.Add(_detail);

            _hotkeyHint = new Label();
            _hotkeyHint.SetBounds(18, 260, 424, 34);
            _hotkeyHint.ForeColor = Color.DimGray;
            BuildHotkeyHint();
            Controls.Add(_hotkeyHint);

            _retry = MakeButton("重试启动", 18, 292, 90, delegate { Retry(); });
            _settings = MakeButton("设置…", 116, 292, 80, delegate { OpenSettings(); });
            _diagnose = MakeButton("自检与诊断…", 204, 292, 110, delegate { OpenDiagnostics(); });
            _close = MakeButton("关闭（继续后台运行）", 322, 292, 120, delegate { Close(); });
        }

        private Label MakeStep(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.SetBounds(x, y, 424, 22);
            Controls.Add(l);
            return l;
        }

        private Button MakeButton(string text, int x, int y, int w, EventHandler onClick)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, y, w, 28);
            b.Click += onClick;
            Controls.Add(b);
            return b;
        }

        private void OpenSettings()
        {
            using (SettingsForm f = new SettingsForm()) f.ShowDialog(this);
        }

        private void OpenDiagnostics()
        {
            using (DiagnosticsForm f = new DiagnosticsForm()) f.ShowDialog(this);
        }

        private void Retry()
        {
            Program.Diag.Add("用户在状态窗口点了「重试启动」");
            _detail.Text = "正在重试…";
            Program.RetryStartup();
        }

        /// <summary>
        /// 每 400ms 刷新一次状态：窗口一就绪就自动关闭，失败满 30 秒就停下来报错。
        /// 用定时器而不是线程，避免跨线程访问控件。
        /// </summary>
        private void StartWatching()
        {
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 400;
            int ticks = 0;
            t.Tick += delegate
            {
                if (_done) { t.Stop(); t.Dispose(); return; }
                if (IsDisposed || !IsHandleCreated) { t.Stop(); t.Dispose(); return; }
                ticks++;

                UpdateSteps();

                if (Program.Host != null && Program.Host.IsWindowAlive())
                {
                    _done = true;
                    t.Stop();
                    t.Dispose();
                    Program.Diag.Add("状态窗口：目标窗口就绪，自动关闭");
                    Close();
                    return;
                }

                // 失败或等太久：停下来把原因摆出来
                if (ticks > 75 && Program.HasStartupFailure())   // 约 30 秒
                {
                    ShowFailure();
                }
            };
            t.Start();
        }

        private void UpdateSteps()
        {
            _step1.Text = "✓  小工具已在运行";

            bool finding = Program.IsFinding;
            if (Program.Host != null && Program.Host.IsWindowAlive())
                _step2.Text = "✓  已锁定 DeepSeek 窗口";
            else if (finding)
                _step2.Text = "…  正在查找 / 启动 DeepSeek 窗口";
            else
                _step2.Text = "…  等待下次查找";

            string hk = Program.Hotkeys == null ? "(未启用)" : Program.Hotkeys.ActivePretty;
            bool armed = Program.Hotkeys != null
                && (Program.Hotkeys.ChordRegistered || Program.Hotkeys.KeyboardHookInstalled);
            _step3.Text = armed ? ("✓  热键已启用：" + hk) : "…  热键要等窗口就绪后才启用";

            if (string.IsNullOrEmpty(_detail.Text) || _detail.Text.StartsWith("正在重试"))
            {
                string b = Program.Host != null && Program.Host.Browser != null
                    ? Program.Host.Browser.Display : "（没找到 Chrome / Edge）";
                _detail.Text = "浏览器：" + b + "\r\n网址：" + Program.Url.Replace("\r", "").Replace("\n", "\r\n")
                    + "\r\n\r\n首次启动要等浏览器把窗口拉起来，通常几秒到十几秒。";
            }
        }

        private void ShowFailure()
        {
            _done = true;
            Text = Program.Title + " 启动遇到问题";
            _title.Text = "启动没有成功";
            _title.ForeColor = Color.Firebrick;
            _step2.Text = "✗  没能拉起 DeepSeek 窗口";
            _detail.Text = Program.StartupFailureText();
            _hotkeyHint.Text = "小工具仍在后台运行（托盘图标）。修好后点「重试启动」即可。";
        }

        /// <summary>把「怎么操作」直接写清楚，尤其是没有托盘的时候。</summary>
        private void BuildHotkeyHint()
        {
            string hk = Program.Hotkeys == null ? "双击 Ctrl" : Program.Hotkeys.ActivePretty;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("呼出 / 收起：" + hk + "　·　窗口里点右键 = 粘贴");
            sb.Append("\r\n改设置：Ctrl+Alt+S　·　看诊断：Ctrl+Alt+D");
            if (Program.TrayUnavailable)
                sb.Append("（本机没有任务栏 / 通知区域，所以没有托盘图标，用这两个热键即可）");
            _hotkeyHint.Text = sb.ToString();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _done = true;
            base.OnFormClosed(e);
        }
    }
}
