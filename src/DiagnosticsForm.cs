// DeepSeek Quake —— 自检与诊断窗口
//
// 用户遇到「按了没反应」时，最需要的是「到底卡在哪一步」。
// 旧版把一切都藏在托盘提示里，所以只能看到「没反应」。
// 这里把每一环都摊开：浏览器找到没、窗口锁定没、热键注册成功没、钩子装上没。
//
// 本程序不写文件，所以诊断信息只能这样看/复制。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DeepSeekQuake
{
    internal sealed class DiagnosticsForm : Form
    {
        private TextBox _box;
        private Label _head;

        public DiagnosticsForm()
        {
            Text = Program.Title + " 自检与诊断";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(700, 520);
            Font = new Font("Microsoft YaHei UI", 9f);
            Icon = SystemIcons.Shield;
            MinimumSize = new Size(560, 400);

            _head = new Label();
            _head.SetBounds(12, 10, 676, 22);
            _head.Font = new Font(Font, FontStyle.Bold);
            Controls.Add(_head);

            _box = new TextBox();
            _box.Multiline = true;
            _box.ReadOnly = true;
            _box.ScrollBars = ScrollBars.Both;
            _box.WordWrap = false;
            _box.SetBounds(12, 38, 676, 400);
            _box.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _box.Font = new Font("Consolas", 9f);
            _box.BackColor = Color.FromArgb(250, 250, 250);
            Controls.Add(_box);

            int by = 448;
            Button copy = new Button();
            copy.Text = "复制全部";
            copy.SetBounds(12, by, 90, 30);
            copy.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            copy.Click += delegate
            {
                try { Clipboard.SetText(_box.Text); copy.Text = "已复制"; }
                catch (Exception ex) { copy.Text = "复制失败"; Program.Diag.Add("复制失败: " + ex.Message); }
            };
            Controls.Add(copy);

            Button retest = new Button();
            retest.Text = "重新自检";
            retest.SetBounds(110, by, 90, 30);
            retest.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            retest.Click += delegate { Refresh2(); };
            Controls.Add(retest);

            Button testBrowser = new Button();
            testBrowser.Text = "测试启动浏览器";
            testBrowser.SetBounds(208, by, 130, 30);
            testBrowser.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            testBrowser.Click += delegate { TestBrowser(); };
            Controls.Add(testBrowser);

            Button refind = new Button();
            refind.Text = "重新查找窗口";
            refind.SetBounds(346, by, 120, 30);
            refind.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            refind.Click += delegate
            {
                Program.Diag.Add("用户点了「重新查找窗口」");
                Program.Host.ForgetWindow();
                Program.ReloadConfFromDisk();
                Program.KickFindPublic(true);
                _box.Text = BuildReport("已触发重新查找，等几秒再点「重新自检」看结果。");
            };
            Controls.Add(refind);

            Button close = new Button();
            close.Text = "关闭";
            close.SetBounds(598, by, 90, 30);
            close.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            close.DialogResult = DialogResult.OK;
            Controls.Add(close);

            Refresh2();
        }

        private void Refresh2()
        {
            _box.Text = BuildReport(null);
            _head.Text = Program.Host != null && Program.Host.IsWindowAlive()
                ? "状态：目标窗口已锁定，热键 " + Program.Hotkeys.ActivePretty
                : "状态：还没有锁定目标窗口";
        }

        private void TestBrowser()
        {
            _box.Text = "正在测试启动浏览器，最多等 12 秒…\r\n";
            Application.DoEvents();
            string err = null;
            try { err = Program.Host.Launch(); }
            catch (Exception ex) { err = ex.Message; }

            string found = "（12 秒内没等到窗口）";
            for (int i = 0; i < 24; i++)
            {
                Application.DoEvents();
                Thread.Sleep(500);
                IntPtr h = Program.Host.FindTargetWindow();
                if (h != IntPtr.Zero) { found = "找到窗口 hwnd=0x" + h.ToInt64().ToString("X") + "，标题=[" + Native.TitleOf(h) + "]"; break; }
            }
            _box.Text = BuildReport("浏览器启动测试：" + (err == null ? "启动命令已发出" : "失败 → " + err) + "\r\n窗口查找：" + found);
        }

        public static string BuildReport(string headline)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("DeepSeek Quake v" + Program.Version + " 自检报告");
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("Windows: " + Environment.OSVersion.VersionString + "   (" + (Environment.Is64BitProcess ? "64" : "32") + " 位进程)");
            sb.AppendLine("程序: " + Application.ExecutablePath);
            sb.AppendLine("配置: " + Program.ConfPath);
            sb.AppendLine();

            sb.AppendLine("──── 1. 浏览器外壳 ────");
            List<BrowserInfo> found = BrowserHost.Discover();
            if (found.Count == 0)
            {
                sb.AppendLine("  ✗ 没找到 Chrome / Edge");
            }
            else
            {
                foreach (BrowserInfo b in found) sb.AppendLine("  · " + b.Display + "  " + b.ExePath);
            }
            BrowserHost host = Program.Host;
            sb.AppendLine("  当前选用: " + (host.Browser == null ? "（无）" : host.Browser.Display + "  " + host.Browser.ExePath));
            sb.AppendLine("  配置 browser=" + Program.Get("browser", "auto"));
            sb.AppendLine("  url=" + Program.Get("url", ""));
            sb.AppendLine("  实际使用的 profile=" + host.ActiveProfilePath);
            sb.AppendLine("  沙箱策略 sandbox=" + host.SandboxMode + "（auto = 秒退后自动用 --no-sandbox 重试一次）");
            if (!string.IsNullOrEmpty(host.LastLaunchError))
                sb.AppendLine("  上次启动失败: " + host.LastLaunchError);
            sb.AppendLine();

            sb.AppendLine("──── 2. 目标窗口 ────");
            if (host.IsWindowAlive())
            {
                IntPtr h = host.TargetWindow;
                sb.AppendLine("  ✓ 已锁定 hwnd=0x" + h.ToInt64().ToString("X"));
                sb.AppendLine("    标题=[" + Native.TitleOf(h) + "]");
                sb.AppendLine("    可见=" + host.Visible + "   前台是我们=" + host.IsOursForeground());
                IntPtr cap = host.TargetThreadCapture;
                sb.AppendLine("    浏览器线程 capture=0x" + cap.ToInt64().ToString("X")
                    + (cap == IntPtr.Zero
                        ? "（未检测到捕获；注意查询失败时同样显示 0——看下面 gterr）"
                        : "（残留！隐藏的捕获持有者会把全桌面点击都抢走，这正是「收起后点不动」的元凶）"));
                sb.AppendLine("    浏览器线程 模态 flags=0x" + host.TargetThreadFlags.ToString("X")
                    + "  菜单宿主 hwndMenuOwner=0x" + host.TargetMenuOwner.ToInt64().ToString("X")
                    + (host.TargetInMenuMode ? "  ← 菜单模态循环正在吃点击（机制与捕获残留不同）" : ""));
                sb.AppendLine("    光标被裁剪(ClipCursor)=" + host.CursorClipped
                    + (host.CursorClipped ? "  ← 光标被困在小矩形里，同样会「点不动别的窗口」" : "（正常）"));
                sb.AppendLine("    捕获检测失败 gterr=" + host.GtiQueryFailures
                    + (host.GtiQueryFailures > 0 ? "  ← >0 说明检测有盲区，cap=0x0 不能当作「没有捕获」" : "（正常）"));
                sb.AppendLine("    强制夺回次数 fsteal=" + host.ForcedStealCount + "（收起后每次都夺一次，属正常）");
                sb.AppendLine("    夺回采样 stealn=" + host.StealAttempts + " 次调用 / " + host.StealGotCount + " 次夺到"
                    + "  stealfrom=0x" + host.LastStealFrom.ToInt64().ToString("X")
                    + (host.LastStealFrom != IntPtr.Zero
                        ? "  ← 非 0！这就是刚才攥着鼠标捕获的那个窗口（真凶指纹）"
                        : ""));
                sb.AppendLine("    收起后左键探针 probe=" + host.ProbeClicks + " 次点击"
                    + (string.IsNullOrEmpty(host.ProbeLast) ? "" : "；最近一次：" + host.ProbeLast));
                sb.AppendLine("    系统鼠标按键状态 btn=" + Native.AsyncMouseButtons()
                    + "（钩子看到的真实状态 " + Native.SharedMouseButtons() + "）"
                    + (Native.AsyncAnyButtonStuck()
                        ? "  ← 有键卡在「按下」！这就是右键抬起被吞掉的后遗症："
                          + "它不产生任何显式捕获，却能让后续鼠标行为失真（收起后点别的窗口没反应）"
                        : "（正常）"));
                sb.AppendLine("    注入合成抬起 pumps=" + host.ButtonPumpCount
                    + " 次（v2.5 的修复：把上面那个「假按下」状态抹掉）");
                if (!string.IsNullOrEmpty(host.LastPumpResult))
                    sb.AppendLine("    最近一次: " + host.LastPumpResult);
                IntPtr fgNow = Native.GetForegroundWindow();
                sb.AppendLine("    当前前台 hwnd=0x" + fgNow.ToInt64().ToString("X")
                    + "  可见=" + Native.IsWindowVisible(fgNow));
                long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
                sb.AppendLine("    WS_EX_TOOLWINDOW(不占任务栏/Alt+Tab)=" + ((ex & Native.WS_EX_TOOLWINDOW) != 0));
            }
            else
            {
                sb.AppendLine("  ✗ 还没有锁定目标窗口——按热键会重新查找并启动浏览器");
            }
            sb.AppendLine();

            sb.AppendLine("──── 3. 热键 ────");
            HotkeyManager hk = Program.Hotkeys;
            sb.AppendLine("  配置 hotkey=" + Program.Get("hotkey", "DOUBLECTRL"));
            sb.AppendLine("  实际生效=" + hk.ActivePretty);
            sb.AppendLine("  组合键已注册(RegisterHotKey)=" + hk.ChordRegistered);
            sb.AppendLine("  键盘钩子已安装(双击识别)=" + hk.KeyboardHookInstalled);
            sb.AppendLine("  双击判定窗口=" + hk.DoubleTapMs + " ms");
            if (!hk.ChordRegistered && !hk.KeyboardHookInstalled)
                sb.AppendLine("  ✗ 两种方式都没生效 → 到托盘「设置…」里换一个组合键");
            sb.AppendLine();

            sb.AppendLine("──── 4. 右键粘贴 ────");
            sb.AppendLine("  配置 rightclickpaste=" + Program.Get("rightclickpaste", "1"));
            sb.AppendLine("  鼠标钩子已安装=" + host.MouseHookInstalled + "（只在本窗口前台时装）");
            sb.AppendLine("  右键按下=" + Program.DownCount + " 次   命中粘贴=" + Program.UpCount
                + " 次   实际注入 Ctrl+V=" + Program.PasteCount + " 次");
            sb.AppendLine("  钩子安装次数=" + Program.HookInstallCount);
            sb.AppendLine();

            sb.AppendLine("──── 5. 内部状态 ────");
            sb.AppendLine("  " + Program.TraceText);
            sb.AppendLine();

            sb.AppendLine("──── 6. 事件日志（最近 60 条）────");
            string[] lines = Program.Diag.Snapshot();
            int from = Math.Max(0, lines.Length - 60);
            for (int i = from; i < lines.Length; i++) sb.AppendLine("  " + lines[i]);

            if (!string.IsNullOrEmpty(headline))
            {
                sb.AppendLine();
                sb.AppendLine("──── 本次操作 ────");
                sb.AppendLine(headline);
            }
            return sb.ToString();
        }
    }
}
