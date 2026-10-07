# DeepSeek Quake

把 DeepSeek 网页版做成「一键掉下来 / 一键收起来」的 quake 式窗口：

- **双击 Ctrl**（默认）在任何界面呼出 / 收起，不用记组合键；
- 窗口里**点右键 = 直接粘贴**剪贴板，Shift+右键 = 浏览器原生菜单；
- 窗口不占任务栏、不进 Alt+Tab；
- 想换键？托盘图标右键 →「设置…」，**在窗口里按键即录**，存了立刻生效。

---

## 快速开始

1. 双击 **DeepSeek Quake.lnk**。
2. 屏幕上会立刻出现一个**启动状态窗口**，逐条显示进度：

   ```
   ✓  小工具已在运行
   …  正在查找 / 启动 DeepSeek 窗口
   …  热键要等窗口就绪后才启用
   ```

3. DeepSeek 窗口拉起来之后，状态窗口**自动关闭**。
4. **连按两下 Ctrl** 收起 / 呼出。
5. 窗口里点右键 = 粘贴；Shift+右键 = 原生菜单。
6. 改设置：**Ctrl+Alt+S**；看诊断：**Ctrl+Alt+D**。
7. 退出：`Ctrl+Alt+D` 打开诊断窗口后从任务管理器结束，或命令行 `taskkill /IM DeepSeekQuake.exe`。

> 如果浏览器没起来，状态窗口**不会**一闪而过——它会停在那里，
> 标题变成「启动没有成功」，写出具体原因，并给出「重试启动 / 设置… / 自检与诊断…」三个按钮。

### 你的机器上没有任务栏 / 托盘图标（重要）

小工具会**自动检测**系统有没有任务栏和通知区域：

- 有任务栏 → 正常显示托盘盾牌图标，双击 = 呼出/收起，右键 = 菜单；
- **没有任务栏**（例如 explorer.exe 没在运行、或用了 Themia 这类自定义桌面外壳）
  → 托盘图标无处可放，小工具改用**热键**作为全部入口：

  | 热键 | 作用 |
  |---|---|
  | 双击 Ctrl | 呼出 / 收起 |
  | `Ctrl+Alt+S` | 打开设置 |
  | `Ctrl+Alt+D` | 打开自检与诊断 |

  这三个键都能在 `hotkey.conf` 里改（`hotkey` / `settingshotkey` / `diagnosehotkey`）。

启动没成功时可以依次试：

- 状态窗口里点「**重试启动**」；
- 双击 **DeepSeekChrome.lnk** 手动把浏览器窗口打开，小工具会自动接管它；
- 把 `D:\Desktop\DeepSeek Quake` 加进火绒信任区（火绒主界面 → 右上角菜单 → 信任区 → 添加目录）；
- 小工具是单实例程序，任务管理器里如果有卡住的 `DeepSeekQuake.exe`，先结束掉。

**双击 Ctrl 完全没反应时（重点）**：

- 先确认小工具不是被自动化脚本、沙箱或任务计划启动的。Windows 会把这类启动
  标成「低完整性」，并阻止它收到普通键盘输入——此时任何全局热键都不可能生效。
  最明显的特征是诊断串里出现 `il=low`，或启动时弹出「低完整性」警告。
- 处理办法：任务管理器里结束所有 `DeepSeekQuake.exe`，然后**双击桌面上的
  DeepSeek Quake.lnk** 正常启动。不要用 `Start-Process`、WMI、计划任务或 CI
  拉起常驻实例。
- 呼出后可能仍被管理员权限的窗口（例如以管理员运行的 Edge）盖住：小工具会先把
  目标窗口临时置顶再取消置顶，尽量抬到前台；第一次按只做这一步，**第二次按一定
  能把窗口收起**，不会卡在「看得见但点不到」的死角。

---

## 怎么改快捷键（三种方式，任选）

### 方式 1：设置窗口（推荐）

**`Ctrl+Alt+S`**（有托盘的机器上也可以托盘右键 →「设置…」）
→ 点一下「呼出 / 收起热键」那个框 → **直接按下你想要的组合键**。

- 框里会立刻显示成 `Ctrl+Shift+A` 这样的名字；
- 点「保存」**马上生效，不用重启**（录的正好是当前生效的那个键也能存，不会误报“被占用”）；
- 不想用组合键，就勾选「改用双击 Ctrl 呼出」；
- **如果想用的组合键已被别的程序占用，保存时会直接告诉你**，不会存下一个用不了的热键。

### 方式 2：托盘菜单（有托盘时）

托盘右键菜单里第一项就写着当前生效的热键，点它等于按热键。

### 方式 3：编辑 hotkey.conf

`Ctrl+Alt+S` 打开设置窗口后点「打开 hotkey.conf」（有托盘时也可以从托盘菜单进），
或者用记事本直接打开同目录的 `hotkey.conf`：

```ini
hotkey=DOUBLECTRL          # 双击 Ctrl
# hotkey=CTRL+ALT+SPACE    # 也可以改成组合键
```

改完保存 → 退出小工具 → 重新双击快捷方式。

### 支持的写法

| 类型 | 写法 | 说明 |
|---|---|---|
| 双击修饰键 | `DOUBLECTRL` | 默认。连按两下 Ctrl |
| | `DOUBLESHIFT` / `DOUBLEALT` / `DOUBLEWIN` | 换成别的修饰键 |
| | `CTRL+CTRL` / `DOUBLE:CTRL` | 等价写法 |
| 组合键 | `WIN+SHIFT+OEM3` | Win+~（OEM3 就是 ` 和 ~ 那个键） |
| | `CTRL+ALT+SPACE` | |
| | `CTRL+SHIFT+A`、`ALT+F1` | 字母 / 数字 / F1-F24 |

组合键必须**至少一个修饰键**（`WIN`/`CTRL`/`ALT`/`SHIFT`）+ **一个主键**。

主键可用：`A-Z`、`0-9`、`F1-F24`、`OEM3`(反引号)、`SPACE`、`COMMA`、`PERIOD`、`SLASH`、
`SEMICOLON`、`QUOTE`、`MINUS`、`EQUALS`、`LBRACKET`、`RBRACKET`、`BACKSLASH`、
`LEFT`、`RIGHT`、`UP`、`DOWN`、`HOME`、`END`、`PAGEUP`、`PAGEDOWN`、`INSERT`、`DELETE`。

主热键被占用时，小工具会按 `hotkeyfallback` → `WIN+SHIFT+D` → `CTRL+ALT+OEM3` 的顺序自动降级，
**实际生效的键永远显示在托盘提示和托盘菜单第一项里**，不会让你猜。

---

## 右键粘贴

| 操作 | 效果 |
|---|---|
| 窗口里点右键 | 直接粘贴剪贴板，不弹菜单 |
| Shift+右键 | 保留浏览器原生菜单（复制、检查元素等） |
| 在其它程序里点右键 | 完全不受影响 |

关掉这个功能：设置窗口里取消勾选，或 `hotkey.conf` 里 `rightclickpaste=0`。

实现：底层鼠标钩子 `WH_MOUSE_LL` 只在 DeepSeek 窗口处于前台时才安装，失去前台立刻卸载；
右键**按下**放行（让页面聚焦、光标落到点击处），右键**抬起**吞掉并注入 `Ctrl+V`。
钩子回调里不做重活，只投递消息回消息循环，避免拖慢系统输入。

---

## 自检与诊断

托盘右键 →「**自检与诊断…**」，一眼看到卡在哪一环：

```
──── 1. 浏览器外壳 ────     找到哪些浏览器、当前用哪个、上次启动失败的原因
──── 2. 目标窗口 ────       有没有锁定窗口、可见性、是否已排除任务栏
──── 3. 热键 ────           配置值 / 实际生效值 / 组合键注册成功没 / 键盘钩子装上没
──── 4. 右键粘贴 ────       钩子装没装、右键按了几次、真正注入 Ctrl+V 几次
──── 5. 内部状态 ────       实时状态串
──── 6. 事件日志 ────       最近 60 条内部事件
```

- **「测试启动浏览器」**：手动跑一遍启动流程，直接告诉你成功还是失败、失败在哪一步；
- **「复制全部」**：整份报告复制到剪贴板，贴给谁都行；
- 命令行一键验证（小工具运行时）：`pwsh -File verify-quake.ps1`。

小工具**不写日志文件**（见下方「为什么不写文件」），所以诊断信息都在这个窗口里。

---

## hotkey.conf

| 键 | 默认值 | 说明 |
|---|---|---|
| `hotkey` | `DOUBLECTRL` | 呼出 / 收起热键，见上表 |
| `doubletapms` | `420` | 双击判定时间窗（毫秒）。太灵敏调小，不好按调大 |
| `hotkeyfallback` | `WIN+SHIFT+OEM3` | 主热键被占用时的备用组合键 |
| `browser` | `auto` | `auto` / `chrome` / `edge` / 浏览器 exe 完整路径 |
| `url` | `https://chat.deepseek.com` | 要打开的网址 |
| `profile` | `chrome-profile` | 浏览器 profile 目录（相对本目录）= 一份登录态 |
| `width` / `height` | `980` / `820` | 窗口尺寸 |
| `rightclickpaste` | `1` | 1 = 右键直接粘贴；0 = 关闭 |
| `restorefocus` | `1` | 1 = 收起后把焦点还给之前的窗口 |
| `startHidden` | `0` | 0 = 找到窗口就显示；1 = 启动后先收起 |
| `sandbox` | `auto` | `auto` / `sandbox` / `nosandbox`，见「沙箱与秒退」 |

所有配置都能用命令行临时覆盖：

    DeepSeekQuake.exe --url=https://chatgpt.com --browser=edge --rightclickpaste=0

---

## 浏览器外壳：Chrome 还是 Edge

两个都是 Chromium，行为一致，`browser=auto` 的选择规则**故意做得简单可预期**：

1. 默认用 **Chrome**——本项目一开始就是 Chrome 方案，而且你原来的 DeepSeek 登录态
   就在 `chrome-profile\` 里；
2. 如果 `hotkey.conf` 里写了 `browser=edge` 或直接写了 exe 路径，就用你指定的；
3. 切换方式：托盘菜单「浏览器外壳」勾一个，或设置窗口里的下拉框。

> 为什么不用「哪个浏览器在运行就用哪个」：本机上 Edge 常年常驻，那条规则会让自动选择
> 永远倒向 Edge，结果就是你得**重新登录一次**。用错浏览器的代价比启动稳定性大，
> 所以这里宁可确定也不要聪明。

**重要**：不同品牌的浏览器**不共用同一个 profile 目录**。Chromium 有单实例锁，
拿同一个目录去启动第二个品牌时，命令行会被转发给已在运行的那个进程然后自己退出——
表现出来就是「按了热键没反应」。所以：

| 情况 | 用的目录 |
|---|---|
| Chrome | `chrome-profile\`（你原来的登录态在这里，小工具不动它） |
| Edge（`browser=edge`，且 `profile=` 没写绝对路径） | `chrome-profile-edge\`（独立目录，**需要重新登录一次**） |
| `profile=` 写了绝对路径 | 完全按你写的来，不加任何后缀 |

实际用的是哪个目录，「自检与诊断…」第 1 节和 `--diagnose` 输出里都写着。

---

## 浏览器秒退与沙箱（本机历史问题）

`chrome-profile\Crashpad\reports` 里曾经积累了 9 个崩溃 dump，**全部**是同一个签名：
异常码 `0x80000003`（Chromium 的 `STATUS_BREAKPOINT` / `IMMEDIATE_CRASH`）、
同一地址 `chrome.dll+0x1135D76`、`ptype=browser`、模块列表里没有任何第三方 DLL。

这个项目**旧版 Electron 客户端**当年把同样的 `0x80000003` 归因于
深信服 SSL VPN 注入 `SangforNspX64.dll`（见 `legacy-electron\start-deepseek.vbs`）。
但 2026-09-30 的复查**没有支持这条结论**：

- `AppInit_DLLs` 为空、`LoadAppInit_DLLs=0`，Winsock LSP 目录里没有深信服项；
- 9 个 dump 全文件搜 `Sangfor` / `EasyConnect` / `SangforNspX64`（ASCII + UTF-16）**零命中**；
- 深信服客户端日志最后写入是 09-26，崩溃当天没有记录；
- 同一个 profile 里有 **13.5 分钟**和 **33 分钟**的完整健康会话记录，
  而第一个 dump 出现在 09-08:45 之后——说明**正常双击启动时 Chrome 是健康的**。

所以小工具的做法是：

- 先按**正常沙箱**启动；
- 只有在浏览器**秒退**（且退出码非 0）时才自动加 `--no-sandbox --test-type` 重试**一次**，
  并在诊断日志里写明「这只是兜底手段，确切原因尚未定论」；
- 退出码 0 的「秒退」不当作崩溃——那通常是 Chromium 单实例机制把命令行转发给已运行实例后正常退出；
- `sandbox=sandbox` 强制只用沙箱不降级；`sandbox=nosandbox` 直接不开沙箱。

⚠️ **安全提醒**：走 `--no-sandbox` 时页面沙箱是关闭的，不要在这个窗口里登录重要账号。
如果小工具报告它用了 `--no-sandbox` 才起来，请把这条信息反馈出来——那说明这台机器上
确实存在一个能让 Chromium 沙箱初始化失败的东西（候选：启动方进程环境、显卡驱动、
安全软件的 HIPS），值得继续查。

---

## 浏览器 profile 与登录态

见上面「浏览器外壳：Chrome 还是 Edge」一节里的表格。一句话：
**Chrome 用 `chrome-profile\`（登录态在里面），Edge 用独立的 `chrome-profile-edge\`
（要重新登录一次），你写了绝对路径就完全听你的。**

---

## 为什么不写文件

本机火绒会按程序拦截「**未签名程序写文件**」。所以 `DeepSeekQuake.exe` 自己不写任何文件：

- 不写日志文件 → 诊断信息在「自检与诊断…」窗口里；
- 不写注册表；
- 改配置时，调用 `src\config-write.ps1`（PowerShell 是签名的）去写 `hotkey.conf`，
  该脚本**保留注释和原有顺序**、UTF-8 无 BOM、原子写入（临时文件 + 覆盖式改名）。

唯一例外：程序会确保 profile 目录存在（`Directory.CreateDirectory`），失败会明确报错。

---

## 文件说明

| 文件 / 目录 | 作用 |
|---|---|
| `DeepSeek Quake.lnk` | 一键启动：直接拉起小工具，浏览器由小工具自己开 |
| `DeepSeekChrome.lnk` | 备用：手动只开浏览器 app 窗口 |
| `start-deepseek.cmd` | `DeepSeek Quake.lnk` 实际运行的脚本（只负责启动小工具） |
| `DeepSeekQuake.exe` | 小工具本体（.NET Framework 4.8，无需额外运行时） |
| `hotkey.conf` | 全部配置 |
| `chrome-profile\` | Chrome 的登录态与缓存（与日常 Chrome 完全隔离） |
| `src\` | 全部源码 + 编译脚本 + 配置回写脚本 |
| `verify-quake.ps1` | 一键验证：热键 / 切换 / 任务栏排除 / 右键粘贴状态 |
| `recon\` | 浏览器与环境侦察记录（可删） |
| `diag\` | 诊断探针与测试产物（可删） |
| `backup\` | 旧版 exe 与快捷方式（确认新版没问题后可整个删） |
| `legacy-electron\` | 早先的 Electron 版（不用可整个删，省约 380 MB） |

---

## 源码与重新编译

| 文件 | 作用 |
|---|---|
| `src\Native.cs` | 所有 Win32 / GDI+ 互操作声明 |
| `src\HotkeySpec.cs` | 热键写法解析（组合键 / 双击），纯逻辑、可单测 |
| `src\HotkeyManager.cs` | 热键引擎：`RegisterHotKey` + 低级键盘钩子双击识别 + 降级链 |
| `src\BrowserHost.cs` | 浏览器定位 / app 窗口识别 / 显示收起 / 右键粘贴钩子 |
| `src\SettingsForm.cs` | 设置窗口（按键即录） |
| `src\StatusForm.cs` | 启动状态窗口（看得见的启动过程 / 失败原因） |
| `src\DiagnosticsForm.cs` | 自检与诊断窗口 |
| `src\SelfTest.cs` | 内置自测（`--selftest`） |
| `src\config-write.ps1` | 配置回写脚本 |
| `src\build.ps1` | 一键编译 + 跑自测 |

一键编译（会自动先退出正在跑的小工具）：

    pwsh -File src\build.ps1

手工编译（脚本不适用时）：

    & 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:winexe /optimize+ /platform:anycpu `
      "/out:D:\Desktop\DeepSeek Quake\DeepSeekQuake.exe" `
      /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll `
      src\DeepSeekQuake.cs src\Native.cs src\HotkeySpec.cs src\HotkeyManager.cs src\BrowserHost.cs `
      src\SettingsForm.cs src\DiagnosticsForm.cs src\StatusForm.cs src\SelfTest.cs

内置自测（不建窗口、不碰托盘）：

    DeepSeekQuake.exe --selftest

启动诊断（不用看界面，直接打印「浏览器能不能拉起来」）：

    DeepSeekQuake.exe --diagnose

它会依次打印：找到哪些浏览器 → auto 选中哪个 → 实际 profile → 有没有现成窗口 →
启动尝试与退出码 → 20 秒内有没有等到窗口 → 内部事件日志。排查环境问题先跑这个。

---

## 常见问题

**连按两下 Ctrl 没反应？**

- 先看屏幕上有没有**启动状态窗口**，或者托盘图标的提示——那里写着当前卡在哪一步；
- 打开「自检与诊断…」看第 3 节：如果「组合键已注册」和「键盘钩子已安装」都是否，
  说明两种方式都没生效 → 到「设置…」里换一个组合键；
- 如果第 2 节显示「还没有锁定目标窗口」，说明浏览器没起来，问题不在热键，
  按第 1 节里的「测试启动浏览器」看具体原因。

**双击快捷方式后 "啥都没有"？**

现在不会了：启动时会弹一个状态窗口，失败就停在那里报原因。
如果连状态窗口都没看到，说明 `DeepSeekQuake.exe` 根本没跑起来（被安全软件拦了），
试试点 `DeepSeekChrome.lnk` 看浏览器能不能开，或者把项目目录加进火绒信任区。

**浏览器窗口没出来？**

- 「自检与诊断…」→「测试启动浏览器」，会直接给出失败原因（含 Win32 错误码和秒退退出码）；
- 手动双击 `DeepSeekChrome.lnk` 能开出来的话，说明浏览器本身没问题，是启动路径被拦了；
- 把项目目录加进火绒信任区再试。

**右键不能粘贴？**

- 「自检与诊断…」第 4 节：必须先让窗口处于前台，鼠标钩子才会装；
- 确认没在设置里关掉「右键直接粘贴」；
- Shift+右键是原生菜单，不会触发粘贴（这是故意的）。

**鼠标卡住过？**

- 现在收起前会先卸载鼠标钩子，再做跨进程窗口操作，并给 Chrome 发带超时的 `WM_CANCELMODE`
  解除鼠标捕获，最后把焦点还给原窗口；
- 真卡住了就托盘右键退出小工具，然后在设置里临时关掉右键粘贴并反馈复现步骤。

**怎么彻底退出？**

- 托盘图标右键 →「退出」。小工具常驻托盘、不写注册表，也可以在任务管理器结束 `DeepSeekQuake.exe`。

---

## 卸载

1. 托盘图标右键 →「退出」；
2. 删掉整个 `D:\Desktop\DeepSeek Quake` 文件夹（登录态在 `chrome-profile\` 里，会一起删掉）；
3. 不动注册表、不写 C 盘、不动日常使用的浏览器。
#   D e e p S e e k - Q u a k e  
 