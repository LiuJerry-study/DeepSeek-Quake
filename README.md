# DeepSeek Quake

把 DeepSeek 网页版做成 **quake 式下拉窗口** 的 Windows 小工具：**连按两下 `Ctrl`**，
窗口从屏幕顶端掉下来／收回去，不占任务栏、不进 `Alt+Tab`，用完就藏起来。

> ### ⬇️ [**点此下载 `DeepSeekQuake.exe`（最新版）**](https://github.com/LiuJerry-study/DeepSeek-Quake/releases/latest)
>
> 单文件、免安装、不需要额外运行时（.NET Framework 4.x 是 Windows 自带的）。
> 下载后放到任意文件夹，**双击就能用**。

当前版本 **v2.1**（2026-10-07），变更见 [CHANGELOG](CHANGELOG.md)。

---

## 它能做什么

| 能力 | 说明 |
|---|---|
| **双击 Ctrl 呼出 / 收起** | 任何界面下都能用，不用记组合键；也可以改成任意组合键 |
| **不打扰** | 窗口不占任务栏、不出现在 `Alt+Tab`，就像一个下拉终端 |
| **右键直接粘贴** | 窗口里点右键 = 粘贴剪贴板；`Shift+右键` = 浏览器原生菜单 |
| **登录态独立** | 用程序目录下的 `chrome-profile\`，和你日常用的 Chrome 完全隔离，互不影响 |
| **不写日志、不写注册表** | 卸载 = 删掉文件夹，不留垃圾 |
| **自带自检** | `--selftest` 一键自测，外加图形化「自检与诊断」窗口 |

## 快速开始

1. 到 [**Releases**](https://github.com/LiuJerry-study/DeepSeek-Quake/releases/latest) 下载 `DeepSeekQuake.exe`；
2. 放进一个固定的文件夹（例如 `D:\Tools\DeepSeek Quake\`），**双击运行**（不需要管理员权限）；
3. 屏幕上会出现一个**启动状态窗口**，逐条显示进度。**等它自己关闭**——
   那代表已经锁定浏览器窗口、热键已就绪；
4. **连按两下 `Ctrl`** 呼出 / 收起；
5. 窗口里**点右键** = 粘贴剪贴板。

> 如果浏览器没起来，状态窗口**不会**一闪而过：它会停在那里写出失败原因，
> 并给出「重试启动 / 设置… / 自检与诊断…」三个按钮。

### 默认热键

| 热键 | 作用 |
|---|---|
| 连按两下 `Ctrl` | 呼出 / 收起 |
| `Ctrl+Alt+S` | 打开设置（**按键即录**，存了立刻生效，不用重启） |
| `Ctrl+Alt+D` | 打开自检与诊断 |
| `Ctrl+Alt+OEM3` 等 | 主热键被占用时自动降级到的备用键，实际生效值显示在托盘菜单第一项 |

有任务栏时，托盘图标：**双击 = 呼出/收起**，**右键 = 菜单**
（呼出/收起 · 设置… · 自检与诊断… · 浏览器外壳 · 打开 hotkey.conf · 重新查找窗口 · 退出）。

退出：托盘右键 →「退出」；没有托盘时用任务管理器结束 `DeepSeekQuake.exe`。

## 配置

配置文件是程序目录下的 `hotkey.conf`（首次运行自动生成，带完整中文注释）。常用项：

| 键 | 默认值 | 说明 |
|---|---|---|
| `hotkey` | `DOUBLECTRL` | 呼出 / 收起热键，也支持 `CTRL+ALT+SPACE`、`WIN+SHIFT+OEM3` 等组合键 |
| `doubletapms` | `420` | 双击判定时间窗（毫秒）。太灵敏调小，不好按调大 |
| `hotkeyfallback` | `WIN+SHIFT+OEM3` | 主热键被占用时依次尝试的备用键 |
| `settingshotkey` | `CTRL+ALT+S` | 打开设置 |
| `diagnosehotkey` | `CTRL+ALT+D` | 打开自检与诊断 |
| `browser` | `auto` | `auto` / `chrome` / `edge` / 浏览器 exe 完整路径 |
| `url` | `https://chat.deepseek.com` | 要打开的网址（换成别的站也行） |
| `profile` | `chrome-profile` | 浏览器 profile 目录 = 一份独立登录态 |
| `width` / `height` | `980` / `820` | 窗口尺寸 |
| `rightclickpaste` | `1` | `1` = 右键直接粘贴；`0` = 关闭 |
| `restorefocus` | `1` | 收起后把焦点还给之前的窗口 |
| `startHidden` | `0` | `1` = 启动后先收起，等热键呼出 |
| `sandbox` | `auto` | `auto` / `sandbox` / `nosandbox`，见[浏览器与沙箱](docs/04-浏览器与沙箱.md) |

改快捷键最快的办法：`Ctrl+Alt+S` → 点一下热键框 → **直接按下你想要的组合键** → 保存。
如果想用的组合键已被别人占用，保存时会直接告诉你，不会存下一个用不了的热键。

所有配置都能用命令行临时覆盖，例如：

```
DeepSeekQuake.exe --url=https://chatgpt.com --browser=edge --rightclickpaste=0
```

## 命令行

| 命令 | 用途 |
|---|---|
| `DeepSeekQuake.exe` | 正常启动 |
| `DeepSeekQuake.exe --selftest` | 内置自测（不建窗口、不碰托盘） |
| `DeepSeekQuake.exe --diagnose` | 启动诊断：找到哪些浏览器、能不能拉起窗口、退出码 |
| `DeepSeekQuake.exe --status` | 打印运行实例、目标窗口、桌面外壳等信息 |

## 常见问题

**双击 exe 之后「啥都没有」？**

现在不会了——启动时会弹状态窗口，失败就停在那里报原因。如果连状态窗口都没看到，
说明 exe 根本没跑起来，通常是安全软件拦了（把程序目录加进信任区）或运行环境异常。

**连按两下 Ctrl 没反应？**

按 `Ctrl+Alt+D` 打开「自检与诊断」，看第 3 节：如果「组合键已注册」和「键盘钩子已安装」
都为否，说明两种方式都没生效 → 到设置里换一个组合键。第 5 节状态串里 `khook=0` / `hth=0`
表示键盘钩子掉了；`hkl=` 一直涨说明它在被系统反复摘掉又装回来。

**双击 Ctrl 收不回去 / 收起后别的页面点不动？**

这是老版本的经典故障，根因已于 v2.1 修复（`ShowWindowAsync` 会「返回成功却什么都不做」）。
如果你在 v2.1 上仍然遇到，请带诊断串反馈：
见 [诊断与排错](docs/03-诊断与排错.md)。

**浏览器窗口没出来？**

「自检与诊断…」→「测试启动浏览器」会直接给出失败原因（含 Win32 错误码和秒退退出码）；
也可以跑 `DeepSeekQuake.exe --diagnose` 看完整链路。

**右键不能粘贴？**

必须先让窗口处于前台，鼠标钩子才会装上；再确认设置里没关掉「右键直接粘贴」。
`Shift+右键`是浏览器原生菜单，不会触发粘贴（这是故意的）。

**Windows 提示「未知发布者」/ SmartScreen 拦截？**

程序没有代码签名。点「更多信息 → 仍要运行」即可，也可以对照 Release 页面里的
SHA256 校验和确认文件没被动过。

**会被杀毒软件拦吗？**

全局热键 + 低级键鼠钩子是这类工具的常规做法，部分 HIPS／安全软件会提示。
把程序目录加进信任区即可。

**怎么彻底退出？**

托盘右键 →「退出」；没有托盘图标时用任务管理器结束 `DeepSeekQuake.exe`，
或命令行 `taskkill /IM DeepSeekQuake.exe`。

## 从源码编译

只需要 Windows 自带的 .NET Framework 编译器，**不用装 SDK**：

```powershell
pwsh -File src\build.ps1
```

它会编译 `src\*.cs` → `DeepSeekQuake.exe`，自动跑一遍内置自测，
并检查产物的完整性标签（带 `Low` 标签的 exe 会被 Windows 按低完整性启动，
全局热键在低完整性下不可能生效，所以脚本发现 `Low` 会直接报错而不是静默放过）。
细节见 [开发与编译](docs/02-开发与编译.md)。

## 项目结构

```
DeepSeekQuake.exe     成品（从 Releases 下载；二进制不入库）
hotkey.conf           配置（首次运行自动生成）
src/                  全部源码 + 编译脚本
  DeepSeekQuake.cs      主程序：命令行入口、托盘、切换逻辑、设置/诊断窗口的接线
  BrowserHost.cs        浏览器定位、app 窗口识别、显示与收起、右键粘贴钩子
  HotkeyManager.cs      热键引擎：RegisterHotKey + 低级键盘钩子双击识别 + 降级链
  HotkeySpec.cs         热键写法解析（纯逻辑，可单测）
  Native.cs             Win32 / GDI+ 互操作声明
  SettingsForm.cs       设置窗口（按键即录）
  StatusForm.cs         启动状态窗口（看得见的启动过程）
  DiagnosticsForm.cs    自检与诊断窗口
  SelfTest.cs           内置自测（--selftest）
  build.ps1             一键编译 + 自测 + 产物完整性标签自检
  config-write.ps1      配置回写（保留注释与顺序，UTF-8 无 BOM，原子写入）
  fix-integrity.ps1     修「低完整性」标签
tests/                一键验证脚本（verify-quake.ps1）
docs/                 技术文档（见下表）
legacy-electron/      早先的 Electron 版实现，仅留档，不再维护
```

## 文档

| 文档 | 内容 |
|---|---|
| [故障根因报告](docs/01-根因报告-右键卡死与收不回去.md) | 「右键之后收不回去」的完整证据链与修法 |
| [开发与编译](docs/02-开发与编译.md) | 源码结构、一键/手工编译、为什么不写文件 |
| [诊断与排错](docs/03-诊断与排错.md) | 诊断窗口、诊断串字段速查表 |
| [浏览器与沙箱](docs/04-浏览器与沙箱.md) | Chrome/Edge 选择规则、profile、秒退与 `--no-sandbox` |
| [环境备忘](docs/05-环境备忘.md) | 无托盘 / 低完整性 / 安全软件等特定机器上的坑 |

## 卸载

1. 退出程序（托盘右键 →「退出」，或任务管理器结束 `DeepSeekQuake.exe`）；
2. 删掉整个程序文件夹（登录态在 `chrome-profile\` 里，会一起删掉）。

不写注册表、不写 C 盘、不影响你日常使用的浏览器。
