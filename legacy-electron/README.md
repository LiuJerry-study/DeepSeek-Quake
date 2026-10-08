# legacy-electron —— 早先的 Electron 版实现（仅留档）

这是本项目**第一版**的实现：用 Electron 把 DeepSeek 网页包成一个桌面窗口。
现在的主线是 `../src/` 里的 C# / WinForms 单文件版（编译出来只有一个
`DeepSeekQuake.exe`，约 115 KB，不需要任何运行时），**这个目录不再维护、不再更新**。

留在这里只为了两件事：

1. 查历史行为（例如当年把 Chrome 的 `0x80000003` 崩溃归因于深信服 SSL VPN 注入，
   见 `start-deepseek.vbs`）——主 README 的
   [环境备忘](../docs/05-环境备忘.md) 里有这条结论的复查记录；
2. 想跑起来对比时有个参照。

> `node_modules/` 已被移出版本控制（当时提交上去约 **145 MB / 568 个文件**，
> 占整个仓库 99% 的体积）。需要运行请自行安装依赖。

## 文件

| 文件 | 作用 |
|---|---|
| `main.js` | Electron 主进程：全局热键呼出/收起、`skipTaskbar`、托盘菜单、窗口位置记忆 |
| `config.json` | 配置（url / hotkey / dataDir 等） |
| `start-deepseek.vbs` | 用 VBScript 无黑窗启动，并在启动前做环境检查 |
| `diagnose.cmd` | 启动诊断 |

## 运行

```powershell
cd legacy-electron
npm install     # 会下载一整个 Electron（约 145 MB）
npm start
```

## 和现在这版的区别

| | legacy-electron | 现在的 C# 版 |
|---|---|---|
| 体积 | 一个 Electron 运行时（~145 MB） | 单个 exe（~115 KB） |
| 运行时 | 需要自带 Chromium | .NET Framework 4.x（Windows 自带） |
| 热键 | `globalShortcut`，固定组合键 | `RegisterHotKey` + 低级键盘钩子双击识别 + 降级链 |
| 窗口 | Electron `BrowserWindow` | 直接接管系统里真实的 Chrome app 窗口 |
| 右键粘贴 | 无 | 有（低级鼠标钩子） |
| 登录态 | `%APPDATA%\DeepSeek Quake` | 程序目录下的 `chrome-profile\` |
