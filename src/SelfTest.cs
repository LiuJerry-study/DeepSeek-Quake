// DeepSeek Quake —— 内置自测（--selftest 调用，不建窗口、不碰托盘、不写文件）
//
// 目的：把「只能靠肉眼观察」的整机行为，尽量压缩成可自动判定的纯逻辑测试，
// 这样回归时不用每次都手动点一遍。
//
// 跑法：DeepSeekQuake.exe --selftest
//   输出每行 PASS/FAIL，全部通过退出码 0，否则 1。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace DeepSeekQuake
{
    internal static class SelfTest
    {
        private static int _pass, _fail, _skip;

        private static void Ok(string name) { _pass++; Console.WriteLine("PASS " + name); }

        private static void Bad(string name, string why)
        {
            _fail++;
            Console.WriteLine("FAIL " + name + " :: " + why);
        }

        private static void Skip(string name, string why)
        {
            _skip++;
            Console.WriteLine("SKIP " + name + " :: " + why);
        }

        private static void Check(string name, bool cond, string why)
        {
            if (cond) Ok(name); else Bad(name, why);
        }

        public static int Run()
        {
            // 这是 winexe，从「没有控制台」的环境（例如 GUI 启动、某些提权场景）调用时
            // Console.OutputEncoding 会抛 IOException，所以要兜住。
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
            catch { }

            try { Console.WriteLine("========== DeepSeek Quake 自测 v" + Program.Version + " =========="); }
            catch (Exception ex)
            {
                MessageBox.Show("自测需要通过控制台运行：\n\nDeepSeekQuake.exe --selftest\n\n(" + ex.Message + ")",
                    Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 2;
            }

            TestParse();
            TestDoubleTap();
            TestDisplay();
            TestWindowScoring();
            TestConfRoundTrip();
            TestBrowserDiscovery();

            Console.WriteLine("");
            Console.WriteLine("=================================================");
            Console.WriteLine("PASS=" + _pass + "  FAIL=" + _fail + "  SKIP=" + _skip);
            Console.WriteLine("=================================================");
            return _fail == 0 ? 0 : 1;
        }

        // ── 组合键解析 ─────────────────────────────────────────────
        private static void TestParse()
        {
            Console.WriteLine("");
            Console.WriteLine("---- A) 组合键解析 ----");

            Valid("WIN+SHIFT+OEM3");
            Valid("win+shift+oem3");
            Valid(" WIN + SHIFT + OEM3 ");
            Valid("CTRL+ALT+SPACE");
            Valid("CTRL+A");
            Valid("ALT+F1");
            Valid("CTRL+F24");
            Valid("WIN+SHIFT+D");
            Valid("CTRL+OEM3");
            Valid("CTRL+BACKTICK");
            Valid("CTRL+TILDE");
            Valid("CTRL+0");
            Valid("SHIFT+LEFT");
            Valid("CTRL+PERIOD");

            Invalid("A", "没有修饰键");
            Invalid("", "空");
            Invalid("CTRL+", "空的最后一个 token");
            Invalid("CTRL+A+B", "两个主键");
            Invalid("CTRL+FOO", "未知键名");
            Invalid("F25", "F25 不存在");
            Invalid("CTRL+F25", "F25 不存在");
            Invalid("SHIFT", "只有修饰键");
            Invalid("CTRL+SPACE+ALT", "2 修饰 1 主键其实合法");
        }

        private static void Valid(string spec)
        {
            TriggerSpec s = TriggerSpec.Parse(spec);
            Check("parse[" + spec + "] 合法", s.IsValid && s.Kind == TriggerKind.Chord,
                "期望合法组合键，实际 IsValid=" + s.IsValid + " Kind=" + s.Kind);
        }

        /// <summary>最后一条特殊：CTRL+SPACE+ALT 是合法的（2 修饰 1 主键），单独处理。</summary>
        private static void Invalid(string spec, string why)
        {
            if (spec == "CTRL+SPACE+ALT")
            {
                TriggerSpec sp = TriggerSpec.Parse(spec);
                Check("parse[" + spec + "] " + why, sp.IsValid,
                    "应当合法（2 修饰键 + 1 主键）");
                return;
            }
            TriggerSpec s = TriggerSpec.Parse(spec);
            Check("parse[" + spec + "] 非法(" + why + ")", !s.IsValid, "本应判为非法，实际合法");
        }

        private static void TestDoubleTap()
        {
            Console.WriteLine("");
            Console.WriteLine("---- B) 双击形态识别 ----");
            DoubleOk("DOUBLECTRL", Native.VK_CONTROL);
            DoubleOk("CTRL+CTRL", Native.VK_CONTROL);
            DoubleOk("DOUBLE:CTRL", Native.VK_CONTROL);
            DoubleOk("doublectrl", Native.VK_CONTROL);
            DoubleOk("DOUBLESHIFT", Native.VK_SHIFT);
            DoubleOk("SHIFT+SHIFT", Native.VK_SHIFT);
            DoubleOk("DOUBLEALT", Native.VK_MENU);
            DoubleOk("ALT+ALT", Native.VK_MENU);

            TriggerSpec bad = TriggerSpec.Parse("DOUBLEFOO");
            Check("DOUBLEFOO 非法", !bad.IsValid, "未知修饰键应判非法");

            // 关键回归：CTRL+CTRL 不能被当成「CTRL 修饰 + CTRL 主键」的组合键
            TriggerSpec s = TriggerSpec.Parse("CTRL+CTRL");
            Check("CTRL+CTRL 走双击分支", s.Kind == TriggerKind.DoubleTap,
                "应当识别为双击，实际 Kind=" + s.Kind);
        }

        private static void DoubleOk(string spec, int wantVk)
        {
            TriggerSpec s = TriggerSpec.Parse(spec);
            Check("doubletap[" + spec + "]",
                s.Kind == TriggerKind.DoubleTap && s.TapModifierVk == wantVk && s.IsValid,
                "期望双击 VK=0x" + wantVk.ToString("X2") + "，实际 Kind=" + s.Kind + " Vk=0x" + s.TapModifierVk.ToString("X2"));
        }

        private static void TestDisplay()
        {
            Console.WriteLine("");
            Console.WriteLine("---- C) 显示名 ----");
            Show("WIN+SHIFT+OEM3", "Win+Shift+~");
            Show("CTRL+ALT+SPACE", "Ctrl+Alt+Space");
            Show("CTRL+A", "Ctrl+A");
            Show("DOUBLECTRL", "双击 Ctrl");
            Show("DOUBLEALT", "双击 Alt");
        }

        private static void Show(string spec, string want)
        {
            string got = TriggerSpec.Parse(spec).Pretty();
            Check("pretty[" + spec + "] = " + want + "（实际 " + got + "）", got == want,
                "期望 " + want + " 实际 " + got);
        }

        // ── 窗口打分（直接调用产品代码 BrowserHost.ScoreWindow，不是复制一份规则）──
        private sealed class FakeWindow
        {
            public string Class = "Chrome_WidgetWin_1";
            public bool Owned;
            public bool IsBrowserProc = true;
            public bool ProfileMatch;
            public bool LaunchedMatch;
            public string Title = "";
            public long Area = 900 * 800;
        }

        private static void TestWindowScoring()
        {
            Console.WriteLine("");
            Console.WriteLine("---- D) 窗口挑选规则（调用 BrowserHost.ScoreWindow 本体）----");
            const string hint = "deepseek";

            Check("日常浏览器窗口(无匹配) 不被选中",
                !WouldSelect(new FakeWindow { Title = "百度一下，你就知道" }, hint), "不应选中普通浏览器窗口");
            Check("日常浏览器窗口(带 owner 的权限气泡) 不被选中",
                !WouldSelect(new FakeWindow { Title = "chat.deepseek.com 想要…", Owned = true }, hint), "有 owner 的窗口不应选中");
            Check("profile 命中则选中",
                WouldSelect(new FakeWindow { ProfileMatch = true, Title = "DeepSeek" }, hint), "profile 命中应选中");
            Check("本程序启动的 pid 则选中",
                WouldSelect(new FakeWindow { LaunchedMatch = true, Title = "DeepSeek" }, hint), "launched pid 命中应选中");
            Check("非浏览器进程不选中",
                !WouldSelect(new FakeWindow { IsBrowserProc = false, ProfileMatch = true }, hint), "非 chrome/msedge 进程不应选中");
            Check("类名不对不选中",
                !WouldSelect(new FakeWindow { Class = "Notepad", ProfileMatch = true }, hint), "类名必须 Chrome_WidgetWin_1");

            // 关键回归：光凭标题**不能**认领窗口。
            // 否则用户日常浏览器里任何标题含 deepseek 的标签页都会被加 WS_EX_TOOLWINDOW
            // 踢出任务栏，而我们自己的窗口永远不出现。
            Check("只有标题命中 → 不选中（避免误认领用户日常浏览器窗口）",
                !WouldSelect(new FakeWindow { Title = "DeepSeek - 深度求索" }, hint),
                "标题不足以认领窗口，必须 profile 或 launched 命中");
            Check("标题命中但属于我们的进程 → 选中",
                WouldSelect(new FakeWindow { LaunchedMatch = true, Title = "DeepSeek - 深度求索" }, hint),
                "launched 命中应选中");
            Check("标题命中只用于加分排序",
                Score(new FakeWindow { ProfileMatch = true, Title = "DeepSeek" }, hint)
                    > Score(new FakeWindow { ProfileMatch = true }, hint),
                "同样 profile 命中时，标题也命中应排更前");

            // 排序：profile 命中 > 本程序拉起的 pid > 纯标题命中
            long sProfile = Score(new FakeWindow { ProfileMatch = true }, hint);
            long sLaunch = Score(new FakeWindow { LaunchedMatch = true }, hint);
            long sTitle = Score(new FakeWindow { Title = "DeepSeek" }, hint);
            Check("优先级 profile > launched > title",
                sProfile > sLaunch && sLaunch > sTitle,
                "得分 profile=" + sProfile + " / launched=" + sLaunch + " / title=" + sTitle);
        }

        /// <summary>把假窗口喂给产品代码 BrowserHost.ScoreWindow，判据只有一处，不会测歪。</summary>
        private static bool WouldSelect(FakeWindow w, string titleHint) { return Score(w, titleHint) >= 0; }

        private static long Score(FakeWindow w, string titleHint)
        {
            bool titleMatch = w.Title.IndexOf(titleHint, StringComparison.OrdinalIgnoreCase) >= 0;
            return BrowserHost.ScoreWindow(
                w.Class == "Chrome_WidgetWin_1", w.Owned, w.IsBrowserProc,
                w.ProfileMatch, w.LaunchedMatch, titleMatch, w.Area);
        }

        // ── 配置回写 ───────────────────────────────────────────────
        private static void TestConfRoundTrip()
        {
            Console.WriteLine("");
            Console.WriteLine("---- E) 配置读写 ----");
            string tmp = Path.Combine(Path.GetTempPath(), "dsq-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".conf");
            try
            {
                string[] lines = new string[] {
                    "# 中文注释",
                    "hotkey=WIN+SHIFT+OEM3",
                    "",
                    "url=https://chat.deepseek.com",
                    "rightclickpaste=1"
                };
                File.WriteAllLines(tmp, lines, new System.Text.UTF8Encoding(false));

                // 复用主程序的解析逻辑
                Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in File.ReadAllLines(tmp))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }

                Check("配置解析 hotkey", d.ContainsKey("hotkey") && d["hotkey"] == "WIN+SHIFT+OEM3",
                    "解析结果不对: " + (d.ContainsKey("hotkey") ? d["hotkey"] : "(缺失)"));
                Check("配置解析忽略注释", !d.ContainsKey("# 中文注释"), "注释行被当成配置项");
                Check("配置解析 url 里的 = 不被截断",
                    d.ContainsKey("url") && d["url"] == "https://chat.deepseek.com", "url 解析错误");

                // config-write.ps1 存在性（真实回写由 verify 套件覆盖）
                string[] probes = new string[] {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"src\config-write.ps1"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\src\config-write.ps1"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\src\config-write.ps1")
                };
                bool foundScript = false;
                foreach (string p in probes)
                {
                    try { if (File.Exists(p)) { foundScript = true; break; } }
                    catch { }
                }
                if (foundScript) Ok("config-write.ps1 存在");
                else Skip("config-write.ps1 存在", "在 exe 附近及上两级都没找到 src\\config-write.ps1");
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        private static void TestBrowserDiscovery()
        {
            Console.WriteLine("");
            Console.WriteLine("---- F) 浏览器定位 ----");
            List<BrowserInfo> found = BrowserHost.Discover();
            foreach (BrowserInfo b in found)
                Console.WriteLine("  发现: " + b.Display + " → " + b.ExePath);

            if (found.Count == 0)
            {
                Skip("找到 Chromium 浏览器", "本机没找到 Chrome / Edge（产品会提示用户去装）");
                return;
            }
            bool allExist = true;
            foreach (BrowserInfo b in found) if (!File.Exists(b.ExePath)) allExist = false;
            Check("发现的浏览器路径都真实存在", allExist, "有路径不存在");

            BrowserInfo auto = BrowserHost.ResolveBrowser("auto");
            Check("browser=auto 能解析", auto != null, "auto 解析失败");

            // PickDefault 契约：宁可确定也不要聪明
            //   * 项目 profile 目录已存在（默认布局下是 Chrome 建的）→ 继续用 Chrome，
            //     不能因为 Edge 正在运行就把用户换过去（那样就得重新登录一次）
            //   * profile 目录不存在 → 也用 Chrome（最可预期）
            string probeDir = Path.Combine(Path.GetTempPath(), "dsq-probe-prof");
            string missingDir = Path.Combine(Path.GetTempPath(), "dsq-no-such-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            try
            {
                Directory.CreateDirectory(probeDir);
                List<BrowserInfo> fake = new List<BrowserInfo>();
                fake.Add(new BrowserInfo { Key = "chrome", Display = "Google Chrome", ExePath = @"C:\fake\chrome.exe", ProcessName = "chrome" });
                fake.Add(new BrowserInfo { Key = "edge", Display = "Microsoft Edge", ExePath = @"C:\fake\msedge.exe", ProcessName = "msedge" });

                BrowserInfo byProfile = BrowserHost.PickDefault(fake, probeDir);
                Check("项目 profile 已存在时 auto 选 Chrome（不因 Edge 在运行而改投）",
                    byProfile != null && byProfile.Key == "chrome",
                    "应选 chrome，实际 " + (byProfile == null ? "null" : byProfile.Key));

                BrowserInfo byMissing = BrowserHost.PickDefault(fake, missingDir);
                Check("profile 目录不存在时 auto 也选 Chrome",
                    byMissing != null && byMissing.Key == "chrome",
                    "应选 chrome，实际 " + (byMissing == null ? "null" : byMissing.Key));

                // 一个「半成品 Edge 目录」绝不能把选择带偏
                Directory.CreateDirectory(missingDir + "-edge");
                BrowserInfo notHijacked = BrowserHost.PickDefault(fake, missingDir);
                Check("残留的 <profile>-edge 目录不会劫持浏览器选择",
                    notHijacked != null && notHijacked.Key == "chrome",
                    "应选 chrome，实际 " + (notHijacked == null ? "null" : notHijacked.Key));
            }
            finally
            {
                try { if (Directory.Exists(probeDir)) Directory.Delete(probeDir, true); } catch { }
                try { if (Directory.Exists(missingDir)) Directory.Delete(missingDir, true); } catch { }
                try { if (Directory.Exists(missingDir + "-edge")) Directory.Delete(missingDir + "-edge", true); } catch { }
            }

            Check("PickDefault 空列表返回 null", BrowserHost.PickDefault(new List<BrowserInfo>()) == null,
                "空列表应返回 null");
            BrowserInfo byKey = BrowserHost.ResolveBrowser(found[0].Key);
            Check("按 key 解析 (" + found[0].Key + ")", byKey != null && byKey.ExePath == found[0].ExePath,
                "解析结果不一致");
            Check("不存在的自定义路径返回 null", BrowserHost.ResolveBrowser(@"C:\nope\nope.exe") == null,
                "不存在的路径应返回 null");
        }
    }
}
