// DeepSeek Quake —— 热键描述解析（纯逻辑，可单测）
//
// 支持两种形态：
//   1) 组合键： WIN+SHIFT+OEM3 / CTRL+ALT+SPACE / ALT+F1 …
//      必须「≥1 个修饰键 + 恰好 1 个主键」。
//   2) 双击修饰键： DOUBLECTRL / CTRL+CTRL / DOUBLE:CTRL / CTRL+CTRL 等价写法，
//      另有 DOUBLESHIFT / DOUBLEALT / DOUBLEWIN。默认就是 双击 Ctrl。

using System;
using System.Collections.Generic;
using System.Globalization;

namespace DeepSeekQuake
{
    internal enum TriggerKind
    {
        Chord,      // 组合键，用 RegisterHotKey 注册
        DoubleTap   // 双击某个修饰键，用低级键盘钩子识别
    }

    internal sealed class TriggerSpec
    {
        public TriggerKind Kind;
        public uint Modifiers;      // Chord：MOD_* 位组合
        public uint VirtualKey;     // Chord：主键 VK
        public int TapModifierVk;   // DoubleTap：要双击的修饰键 VK
        public string Source = "";  // 原始文本，用于回写配置

        public bool IsValid { get { return Kind == TriggerKind.Chord ? (Modifiers != 0 && VirtualKey != 0) : TapModifierVk != 0; } }

        /// <summary>给人看的名字：Win+Shift+~ / 双击 Ctrl。</summary>
        public string Pretty()
        {
            if (Kind == TriggerKind.DoubleTap) return "双击 " + ModifierName(TapModifierVk);
            List<string> parts = new List<string>();
            if ((Modifiers & Native.MOD_WIN) != 0) parts.Add("Win");
            if ((Modifiers & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((Modifiers & Native.MOD_ALT) != 0) parts.Add("Alt");
            if ((Modifiers & Native.MOD_SHIFT) != 0) parts.Add("Shift");
            parts.Add(KeyName(VirtualKey));
            return string.Join("+", parts.ToArray());
        }

        public static string ModifierName(int vk)
        {
            switch (vk)
            {
                case Native.VK_CONTROL: return "Ctrl";
                case Native.VK_SHIFT: return "Shift";
                case Native.VK_MENU: return "Alt";
                case Native.VK_LWIN: return "Win";
                default: return "?";
            }
        }

        /// <summary>VK → 显示名。</summary>
        public static string KeyName(uint vk)
        {
            if (vk >= 'A' && vk <= 'Z') return ((char)vk).ToString();
            if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1);   // F1..F24
            switch (vk)
            {
                case 0xC0: return "~";
                case 0x20: return "Space";
                case 0x0D: return "Enter";
                case 0x09: return "Tab";
                case 0x1B: return "Esc";
                case 0x08: return "Backspace";
                case 0x2E: return "Delete";
                case 0x2D: return "Insert";
                case 0x24: return "Home";
                case 0x23: return "End";
                case 0x21: return "PageUp";
                case 0x22: return "PageDown";
                case 0x25: return "Left";
                case 0x26: return "Up";
                case 0x27: return "Right";
                case 0x28: return "Down";
                case 0xBC: return ",";
                case 0xBE: return ".";
                case 0xBF: return "/";
                case 0xBA: return ";";
                case 0xDE: return "'";
                case 0xDB: return "[";
                case 0xDD: return "]";
                case 0xDC: return "\\";
                case 0xBD: return "-";
                case 0xBB: return "=";
                default: return "0x" + vk.ToString("X2", CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// 把用户按下的键翻译成可注册的 VK。不支持的键返回 0。
        /// （设置窗口的「按键即录」用这个。）
        /// </summary>
        public static uint VkFromKeyCode(System.Windows.Forms.Keys key)
        {
            System.Windows.Forms.Keys k = key & System.Windows.Forms.Keys.KeyCode;
            int v = (int)k;
            if (v >= (int)System.Windows.Forms.Keys.A && v <= (int)System.Windows.Forms.Keys.Z) return (uint)v;
            if (v >= (int)System.Windows.Forms.Keys.D0 && v <= (int)System.Windows.Forms.Keys.D9) return (uint)v;
            if (v >= (int)System.Windows.Forms.Keys.F1 && v <= (int)System.Windows.Forms.Keys.F24) return (uint)v;
            switch (k)
            {
                case System.Windows.Forms.Keys.Oemtilde: return 0xC0;      // ` ~
                case System.Windows.Forms.Keys.Space: return 0x20;
                case System.Windows.Forms.Keys.Oemcomma: return 0xBC;
                case System.Windows.Forms.Keys.OemPeriod: return 0xBE;
                case System.Windows.Forms.Keys.OemQuestion: return 0xBF;
                case System.Windows.Forms.Keys.OemSemicolon: return 0xBA;
                case System.Windows.Forms.Keys.OemQuotes: return 0xDE;
                case System.Windows.Forms.Keys.OemOpenBrackets: return 0xDB;
                case System.Windows.Forms.Keys.OemCloseBrackets: return 0xDD;
                case System.Windows.Forms.Keys.OemPipe: return 0xDC;
                case System.Windows.Forms.Keys.OemMinus: return 0xBD;
                case System.Windows.Forms.Keys.Oemplus: return 0xBB;
                case System.Windows.Forms.Keys.Insert: return 0x2D;
                case System.Windows.Forms.Keys.Delete: return 0x2E;
                case System.Windows.Forms.Keys.Home: return 0x24;
                case System.Windows.Forms.Keys.End: return 0x23;
                case System.Windows.Forms.Keys.PageUp: return 0x21;
                case System.Windows.Forms.Keys.PageDown: return 0x22;
                case System.Windows.Forms.Keys.Left: return 0x25;
                case System.Windows.Forms.Keys.Up: return 0x26;
                case System.Windows.Forms.Keys.Right: return 0x27;
                case System.Windows.Forms.Keys.Down: return 0x28;
                default: return 0;
            }
        }

        /// <summary>VK → 写回配置时用的键名。</summary>
        public static string ConfKeyName(uint vk)
        {
            if (vk == 0xC0) return "OEM3";
            if (vk == 0x20) return "SPACE";
            if (vk >= 'A' && vk <= 'Z') return ((char)vk).ToString();
            if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1);
            switch (vk)
            {
                case 0xBC: return "COMMA";
                case 0xBE: return "PERIOD";
                case 0xBF: return "SLASH";
                case 0xBA: return "SEMICOLON";
                case 0xDE: return "QUOTE";
                case 0xDB: return "LBRACKET";
                case 0xDD: return "RBRACKET";
                case 0xDC: return "BACKSLASH";
                case 0xBD: return "MINUS";
                case 0xBB: return "EQUALS";
                case 0x2D: return "INSERT";
                case 0x2E: return "DELETE";
                case 0x24: return "HOME";
                case 0x23: return "END";
                case 0x21: return "PAGEUP";
                case 0x22: return "PAGEDOWN";
                case 0x25: return "LEFT";
                case 0x26: return "UP";
                case 0x27: return "RIGHT";
                case 0x28: return "DOWN";
                default: return "";
            }
        }

        /// <summary>把 TriggerSpec 序列化回配置文本。</summary>
        public string ToConf()
        {
            if (Kind == TriggerKind.DoubleTap)
            {
                switch (TapModifierVk)
                {
                    case Native.VK_CONTROL: return "DOUBLECTRL";
                    case Native.VK_SHIFT: return "DOUBLESHIFT";
                    case Native.VK_MENU: return "DOUBLEALT";
                    case Native.VK_LWIN: return "DOUBLEWIN";
                }
                return "DOUBLECTRL";
            }
            List<string> parts = new List<string>();
            if ((Modifiers & Native.MOD_WIN) != 0) parts.Add("WIN");
            if ((Modifiers & Native.MOD_CONTROL) != 0) parts.Add("CTRL");
            if ((Modifiers & Native.MOD_ALT) != 0) parts.Add("ALT");
            if ((Modifiers & Native.MOD_SHIFT) != 0) parts.Add("SHIFT");
            string kn = ConfKeyName(VirtualKey);
            parts.Add(string.IsNullOrEmpty(kn) ? ("VK" + VirtualKey.ToString("X2", CultureInfo.InvariantCulture)) : kn);
            return string.Join("+", parts.ToArray());
        }

        public static TriggerSpec Parse(string spec)
        {
            TriggerSpec s = new TriggerSpec();
            s.Source = spec == null ? "" : spec.Trim();
            if (s.Source.Length == 0) { s.Kind = TriggerKind.Chord; return s; }   // 无效

            // 先看是不是「双击某键」写法
            // 注意：冒号在这里只是分隔符，要「去掉」而不是换成 '+'，
            // 否则 DOUBLE:CTRL 会变成 DOUBLE+CTRL 而漏掉双击分支。
            string compact = s.Source.ToUpperInvariant().Replace(" ", "").Replace(":", "");

            // 用 None 而不是 RemoveEmptyEntries：CTRL++A / +CTRL+A / WIN++SHIFT+A 里
            // 的空 token 必须判为非法，不能被悄悄吃掉当成合法组合键。
            string[] tokens = compact.Split(new char[] { '+' }, StringSplitOptions.None);
            foreach (string t in tokens)
            {
                if (t.Length == 0)
                {
                    s.Kind = TriggerKind.Chord;   // 无效（IsValid 为 false）
                    return s;
                }
            }

            // DOUBLEXXX 或 XXX+XXX（同一个修饰键写两遍）
            string first = tokens.Length > 0 ? tokens[0] : "";
            if (first.StartsWith("DOUBLE") && tokens.Length == 1)
            {
                int mvk = ModifierVk(first.Substring(6));
                if (mvk != 0)
                {
                    s.Kind = TriggerKind.DoubleTap;
                    s.TapModifierVk = mvk;
                    return s;
                }
                s.Kind = TriggerKind.Chord;   // DOUBLEFOO：无效
                return s;
            }
            if (tokens.Length == 2 && tokens[0] == tokens[1])
            {
                int mvk = ModifierVk(tokens[0]);
                if (mvk != 0)
                {
                    s.Kind = TriggerKind.DoubleTap;
                    s.TapModifierVk = mvk;
                    return s;
                }
            }

            // 组合键
            uint mods = 0, vk = 0;
            foreach (string t in tokens)
            {
                switch (t)
                {
                    case "WIN": case "SUPER": case "META": mods |= Native.MOD_WIN; continue;
                    case "CTRL": case "CONTROL": mods |= Native.MOD_CONTROL; continue;
                    case "ALT": mods |= Native.MOD_ALT; continue;
                    case "SHIFT": mods |= Native.MOD_SHIFT; continue;
                }
                uint k = MainKeyVk(t);
                if (k == 0) { s.Kind = TriggerKind.Chord; return s; }   // 无效
                if (vk != 0) { s.Kind = TriggerKind.Chord; return s; }  // 两个主键：无效
                vk = k;
            }
            s.Kind = TriggerKind.Chord;
            s.Modifiers = mods;
            s.VirtualKey = vk;
            return s;
        }

        private static int ModifierVk(string token)
        {
            switch (token)
            {
                case "CTRL": case "CONTROL": return Native.VK_CONTROL;
                case "SHIFT": return Native.VK_SHIFT;
                case "ALT": return Native.VK_MENU;
                case "WIN": case "SUPER": case "META": return Native.VK_LWIN;
                default: return 0;
            }
        }

        private static uint MainKeyVk(string token)
        {
            if (token.Length == 1)
            {
                char c = token[0];
                if (c >= 'A' && c <= 'Z') return (uint)c;
                if (c >= '0' && c <= '9') return (uint)c;
            }
            switch (token)
            {
                case "OEM3": case "BACKTICK": case "TILDE": return 0xC0;
                case "SPACE": return 0x20;
                case "COMMA": return 0xBC;
                case "PERIOD": return 0xBE;
                case "SLASH": return 0xBF;
                case "SEMICOLON": return 0xBA;
                case "QUOTE": return 0xDE;
                case "LBRACKET": return 0xDB;
                case "RBRACKET": return 0xDD;
                case "BACKSLASH": return 0xDC;
                case "MINUS": return 0xBD;
                case "EQUALS": return 0xBB;
                case "INSERT": return 0x2D;
                case "DELETE": return 0x2E;
                case "HOME": return 0x24;
                case "END": return 0x23;
                case "PAGEUP": return 0x21;
                case "PAGEDOWN": return 0x22;
                case "LEFT": return 0x25;
                case "UP": return 0x26;
                case "RIGHT": return 0x27;
                case "DOWN": return 0x28;
            }
            if (token.Length >= 2 && token[0] == 'F')
            {
                int n;
                if (int.TryParse(token.Substring(1), out n) && n >= 1 && n <= 24) return (uint)(0x70 + n - 1);
            }
            return 0;
        }
    }
}
