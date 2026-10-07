'use strict';
/**
 * DeepSeek Quake —— 用一个全局热键呼出 / 收起 DeepSeek 网页版
 *
 * 设计目标（对应原始需求）：
 *   1. 全局热键（默认 Win+`）任何窗口聚焦时都能呼出 / 收起 —— 同 Windows Terminal quake 模式
 *   2. 收起 = 隐藏窗口，进程不退出 → 登录态、页面内容、正在生成的回答全部保留
 *   3. skipTaskbar=true 让 Electron 打上 WS_EX_TOOLWINDOW → 同时不在任务栏、不在 Alt+Tab
 *   4. 登录态 / 缓存 / 窗口位置默认落在 %APPDATA%\DeepSeek Quake（可用 config.json 的
 *      dataDir 改回程序目录下的 data\），删程序目录 + 删该数据目录即彻底卸载
 */

const { app, BrowserWindow, globalShortcut, Tray, Menu, nativeImage, shell, dialog, screen } = require('electron');
const path = require('node:path');
const fs = require('node:fs');

const ROOT = __dirname;
const CONFIG_PATH = path.join(ROOT, 'config.json');
const APP_ID = 'local.dsh.deepseek-quake';

const DEFAULTS = {
  url: 'https://chat.deepseek.com',
  hotkey: 'Super+Shift+D',
  fallbackHotkeys: ['Super+Shift+`', 'Alt+Shift+D'],
  quitHotkey: 'Control+Alt+Q',
  width: 940,
  height: 760,
  alwaysOnTop: true,
  hideOnBlur: false,
  hideOnEscape: true,
  frame: true,
  zoomFactor: 1.0,
  tray: true,
  startHidden: true,
  useHiddenOwner: true,
  dataDir: 'local'
};

let cfg = loadConfig();
// 数据目录：'local'（默认，= 程序目录下 data\，跟程序同盘）| 'appdata'（= %APPDATA%\DeepSeek Quake）
// | 其它相对/绝对路径（如 "D:\\DeepSeekQuakeData"）。交给 pickDataDir 探测出第一个真正可写的位置
const DATA_DIR = pickDataDir(cfg);
// 找不到可写目录时，pickDataDir 已安排在 ready 后弹框并退出；这里必须结束模块加载，
// 否则后面的 mkdirSync(null) 会抛 TypeError，用户只看到一个没头没尾的 "Error" 框。
if (!DATA_DIR) return;
const BOUNDS_PATH = path.join(DATA_DIR, 'window-bounds.json');
let win = null;
let anchor = null;
let tray = null;
let quitting = false;
let registeredHotkey = null;

// 日志：数据目录 → 程序目录，都在程序所在盘（不碰 C 盘）
const LOG_FILE = pickLogFile();
function pickLogFile() {
  const candidates = [
    DATA_DIR ? path.join(DATA_DIR, 'deepseek-quake.log') : null,
    path.join(ROOT, 'deepseek-quake.log')
  ];
  for (const f of candidates) {
    if (!f) continue;
    try { fs.mkdirSync(path.dirname(f), { recursive: true }); fs.appendFileSync(f, ''); return f; }
    catch { /* 试下一个 */ }
  }
  return null;
}
function logLine(msg) {
  console.log(msg);
  if (!LOG_FILE) return;
  try { fs.appendFileSync(LOG_FILE, `[${new Date().toISOString()}] ${msg}\r\n`); } catch { /* 忽略 */ }
}

function resolveDataDir(c) {
  const raw = String(c.dataDir ?? 'appdata').trim();
  const low = raw.toLowerCase();
  if (raw === '' || low === 'appdata') {
    const appData = process.env.APPDATA || app.getPath('appData');
    return path.join(appData, 'DeepSeek Quake');
  }
  if (low === 'local') return path.join(ROOT, 'data');
  return path.resolve(ROOT, raw);
}

// 数据目录按优先级探测第一个真正可写的：配置值 → 程序目录\data → 程序目录\.data-fallback
// 刻意**不把默认值放到 C 盘**（%APPDATA%/%TEMP%）：一是 C 盘紧张，二是本机对未签名程序
// 写 C 盘存在拦截。Chromium 的共享内存仍会短暂用 %TEMP%（几十 KB，无法重定向）。
function pickDataDir(c) {
  const localDir = path.join(ROOT, 'data');
  const altLocalDir = path.join(ROOT, '.data-fallback');
  const configured = resolveDataDir(c);
  const candidates = [...new Set([configured, localDir, altLocalDir])];

  const failures = [];
  for (const dir of candidates) {
    try {
      fs.mkdirSync(dir, { recursive: true });
      const probe = path.join(dir, '.write-probe');
      fs.writeFileSync(probe, 'ok');
      fs.unlinkSync(probe);
      if (dir !== configured) console.warn(`[data] ${configured} 不可写（${failures.map((f) => f.code).join(', ')}），改用 ${dir}`);
      return dir;
    } catch (err) {
      failures.push({ dir, code: err.code || err.message });
      console.warn(`[data] 不可写：${dir} → ${err.code || err.message}`);
    }
  }

  const detail = failures.map((f) => `· ${f.dir}  (${f.code})`).join('\n');
  console.error('[data] 全部不可写：\n' + detail);
  // ready 之前 showErrorBox 可能根本不显示，所以推迟到 ready 后再弹，确保看得见
  app.whenReady().then(() => {
    dialog.showErrorBox('DeepSeek Quake：找不到可写的数据目录',
      '下列位置都无法写入：\n\n' + detail
      + '\n\n请在 config.json 的 dataDir 里指定一个可写路径后重试。');
    app.exit(1);
  });
  return null;
}

// ── 配置与窗口位置持久化 ────────────────────────────────────────────────
function loadConfig() {
  try {
    return { ...DEFAULTS, ...JSON.parse(fs.readFileSync(CONFIG_PATH, 'utf8')) };
  } catch (err) {
    console.warn('[config] config.json 读取失败，使用默认值：', err.message);
    return { ...DEFAULTS };
  }
}

function readBounds() {
  let saved = {};
  try { saved = JSON.parse(fs.readFileSync(BOUNDS_PATH, 'utf8')); } catch { /* 首次运行 */ }

  const bounds = { width: cfg.width, height: cfg.height };
  if (Number.isFinite(saved.width)) bounds.width = saved.width;
  if (Number.isFinite(saved.height)) bounds.height = saved.height;

  // 只在记录的位置仍落在某块屏幕内时才恢复，避免拔掉外接显示器后窗口跑到看不见的地方
  if (Number.isFinite(saved.x) && Number.isFinite(saved.y)) {
    const area = screen.getDisplayMatching({ x: saved.x, y: saved.y, width: bounds.width, height: bounds.height }).workArea;
    const onScreen = saved.x >= area.x - 40 && saved.x < area.x + area.width - 80
      && saved.y >= area.y - 40 && saved.y < area.y + area.height - 40;
    if (onScreen) { bounds.x = saved.x; bounds.y = saved.y; }
  }
  return bounds;
}

function saveBounds() {
  try {
    if (!win || win.isDestroyed()) return;
    fs.writeFileSync(BOUNDS_PATH, JSON.stringify(win.getNormalBounds()));
  } catch (err) {
    console.warn('[bounds] 保存失败：', err.message);
  }
}

// ── 窗口 ────────────────────────────────────────────────────────────────

// Windows 组 Alt+Tab 列表的规则：可见 + 无 owner + 无 WS_EX_TOOLWINDOW 才进列表。
// Electron 的 skipTaskbar 只走 Shell 的 ITaskbarList（去掉任务栏按钮），实测并不设
// WS_EX_TOOLWINDOW，所以窗口照样出现在 Alt+Tab。对策：给它一个隐藏的属主窗口 ——
// Windows 不会把「有 owner」的窗口放进 Alt+Tab，也不需要额外原生模块。
function ensureAnchor() {
  if (anchor && !anchor.isDestroyed()) return anchor;
  anchor = new BrowserWindow({
    show: false,
    width: 1,
    height: 1,
    frame: false,
    skipTaskbar: true,
    focusable: false
  });
  return anchor;
}

function createWindow() {
  win = new BrowserWindow({
    ...readBounds(),
    show: false,
    parent: cfg.useHiddenOwner !== false ? ensureAnchor() : undefined,
    frame: cfg.frame !== false,
    title: 'DeepSeek',
    backgroundColor: '#101522',
    skipTaskbar: true,               // 去掉任务栏按钮；Alt+Tab 由上面的 owner 排除
    alwaysOnTop: !!cfg.alwaysOnTop,
    autoHideMenuBar: true,
    webPreferences: {
      partition: 'persist:deepseek',  // 独立会话，与日常 Chrome 完全隔离
      contextIsolation: true,
      nodeIntegration: false,
      spellcheck: true,
      backgroundThrottling: false      // 隐藏期间也不要节流页面
    }
  });

  win.setMenuBarVisibility(false);
  win.loadURL(cfg.url);

  if (typeof cfg.zoomFactor === 'number' && cfg.zoomFactor !== 1) {
    win.webContents.on('did-finish-load', () => win.webContents.setZoomFactor(cfg.zoomFactor));
  }

  // 页面里的外链一律交给系统浏览器，不在这个「弹层」里开新窗口
  win.webContents.setWindowOpenHandler(({ url }) => {
    if (/^https?:/i.test(url)) shell.openExternal(url);
    return { action: 'deny' };
  });

  // 点 X 不退出进程，只收起（保持登录态）
  win.on('close', (e) => {
    if (!quitting) { e.preventDefault(); saveBounds(); win.hide(); }
  });
  win.on('hide', saveBounds);
  win.on('blur', () => {
    if (cfg.hideOnBlur && !win.webContents.isDevToolsOpened()) hideWin();
  });
  win.webContents.on('before-input-event', (e, input) => {
    if (cfg.hideOnEscape && input.type === 'keyDown' && input.key === 'Escape'
        && !win.webContents.isDevToolsOpened()) {
      hideWin();
    }
  });

  return win;
}

function showWin() {
  if (!win || win.isDestroyed()) createWindow();
  if (win.isMinimized()) win.restore();
  win.setSkipTaskbar(true);          // 保险：任何情况下都从任务栏 / Alt+Tab 里排除
  if (cfg.alwaysOnTop) win.setAlwaysOnTop(true);
  win.show();
  win.focus();

  // Windows 前台锁兜底：偶尔 SetForegroundWindow 会被拒（焦点留在别的窗口）。
  // 用一次置顶脉冲 + 重试把它拉回前台，不改变用户配置的 alwaysOnTop 语义。
  if (!win.isFocused()) {
    const wanted = !!cfg.alwaysOnTop;
    win.setAlwaysOnTop(true, 'screen-saver');
    win.show();
    win.focus();
    win.setAlwaysOnTop(wanted, wanted ? 'normal' : 'normal');
  }
  if (process.env.DSQ_TRACE) {
    console.log(`[show] visible=${win.isVisible()} focused=${win.isFocused()}`);
  }
  logLine(`[show] visible=${win.isVisible()} focused=${win.isFocused()}`);
}

function hideWin() {
  if (win && !win.isDestroyed()) { saveBounds(); win.hide(); logLine('[hide]'); }
}

function toggleWindow() {
  if (!win || win.isDestroyed()) return showWin();
  const visible = win.isVisible() && !win.isMinimized();
  return visible ? hideWin() : showWin();
}

// ── 全局热键 ────────────────────────────────────────────────────────────
function registerHotkeys() {
  const candidates = [cfg.hotkey, ...(cfg.fallbackHotkeys || [])].filter(Boolean);
  for (const accel of candidates) {
    try {
      if (globalShortcut.register(accel, toggleWindow)) { registeredHotkey = accel; break; }
    } catch (err) {
      console.warn(`[hotkey] "${accel}" 不是合法快捷键：${err.message}`);
    }
  }

  if (registeredHotkey) {
    cfg.hotkey = registeredHotkey;
    logLine(`[hotkey] 已注册：${registeredHotkey}`);
    if (process.env.DSQ_HOTKEY_PROBE) console.log(`DSQ_HOTKEY_OK ${registeredHotkey}`);
  } else {
    logLine(`[hotkey] 注册失败：${candidates.join(' / ')} 都被占用`);
    dialog.showErrorBox('DeepSeek Quake：热键注册失败',
      `无法注册 ${candidates.join(' / ')}，多半已被别的程序占用（例如 Windows Terminal 的 quake 模式）。\n\n`
      + '请编辑 config.json 里的 "hotkey"，换成别的组合后重启本程序。');
  }

  if (cfg.quitHotkey) {
    try { globalShortcut.register(cfg.quitHotkey, () => { quitting = true; app.quit(); }); } catch { /* 忽略 */ }
  }
}

// ── 托盘 ────────────────────────────────────────────────────────────────
function trayIcon() {
  // 直接画一个 16×16 的圆形图标（BGRA），省掉二进制资源文件
  const S = 16, buf = Buffer.alloc(S * S * 4);
  for (let y = 0; y < S; y++) {
    for (let x = 0; x < S; x++) {
      const i = (y * S + x) * 4;
      if (Math.hypot(x - 7.5, y - 7.5) <= 7.2) {
        buf[i] = 0xfe; buf[i + 1] = 0x6b; buf[i + 2] = 0x4d; buf[i + 3] = 255; // #4D6BFE, BGRA
      }
    }
  }
  return nativeImage.createFromBitmap(buf, { width: S, height: S });
}

function startupShortcutPath() {
  return path.join(app.getPath('appData'), 'Microsoft', 'Windows', 'Start Menu',
    'Programs', 'Startup', 'DeepSeek Quake.lnk');
}

function setAutoStart(on) {
  const lnk = startupShortcutPath();
  if (on) {
    return shell.writeShortcutLink(lnk, 'create', {
      target: process.execPath,
      args: '. --no-sandbox',
      cwd: ROOT,
      description: 'DeepSeek Quake —— 全局热键呼出 DeepSeek 网页版',
      icon: process.execPath,
      iconIndex: 0
    });
  }
  try { fs.unlinkSync(lnk); return true; } catch { return false; }
}

function createTray() {
  tray = new Tray(trayIcon());
  tray.setToolTip(`DeepSeek Quake（${registeredHotkey || cfg.hotkey}）`);

  const rebuild = () => {
    tray.setContextMenu(Menu.buildFromTemplate([
      { label: `呼出 / 收起（${registeredHotkey || cfg.hotkey}）`, click: toggleWindow },
      { type: 'separator' },
      { label: '重新加载页面', click: () => win && !win.isDestroyed() && win.webContents.reload() },
      { label: '打开 config.json', click: () => shell.openPath(CONFIG_PATH) },
      {
        label: '开机自启', type: 'checkbox', checked: fs.existsSync(startupShortcutPath()),
        click: (item) => {
          if (!setAutoStart(item.checked)) dialog.showErrorBox('设置失败', '无法写入「启动」文件夹的快捷方式。');
          rebuild();
        }
      },
      { type: 'separator' },
      { label: '退出', click: () => { quitting = true; app.quit(); } }
    ]));
  };
  rebuild();
  tray.on('double-click', toggleWindow);
}

// ── 生命周期 ────────────────────────────────────────────────────────────
try { fs.mkdirSync(DATA_DIR, { recursive: true }); } catch { /* 已在 pickDataDir 里验证可写 */ }
app.setPath('userData', DATA_DIR);          // 登录态 / 缓存落在探测出的可写目录
// 崩溃转储也放数据目录，避免 Electron 默认往 C 盘写
try {
  const crashDir = path.join(DATA_DIR, 'crashDumps');
  fs.mkdirSync(crashDir, { recursive: true });
  app.setPath('crashDumps', crashDir);
} catch { /* 保持默认 */ }
app.setAppUserModelId(APP_ID);

if (!app.requestSingleInstanceLock()) {
  app.quit();                                // 已在运行 → 交给已有实例处理
} else {
  app.on('second-instance', () => showWin());

  app.whenReady().then(() => {
    createWindow();
    registerHotkeys();
    if (cfg.tray !== false) createTray();

    // 首次运行（还没有 profile）直接把窗口显示出来，免得"双击了却什么都没发生"
    const freshProfile = !fs.existsSync(path.join(DATA_DIR, 'Local State'));
    if (cfg.startHidden === false || freshProfile) showWin();

    if (tray) {
      try {
        tray.displayBalloon({
          title: 'DeepSeek Quake 已就绪',
          content: `按 ${registeredHotkey || cfg.hotkey} 呼出 / 收起`
            + (freshProfile ? '\n首次运行：窗口已打开，请先登录 DeepSeek' : '')
            + `\n数据目录：${DATA_DIR}`
        });
      } catch (err) { logLine(`[tray] 气泡提示失败：${err.message}`); }
    }

    if (process.argv.includes('--dev')) win.webContents.openDevTools({ mode: 'detach' });
    logLine(`[ready] hotkey=${registeredHotkey || cfg.hotkey} data=${DATA_DIR} log=${LOG_FILE} fresh=${freshProfile}`);
  });

  app.on('window-all-closed', () => { /* 常驻托盘，不退出 */ });
  app.on('before-quit', () => { quitting = true; });
  app.on('will-quit', () => { saveBounds(); globalShortcut.unregisterAll(); });
}
