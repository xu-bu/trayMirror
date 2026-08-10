# TrayMirror — 副屏托盘镜像工具

在副屏右下角显示模拟托盘条，镜像主屏托盘应用的图标；点击图标 = 激活/隐藏对应应用窗口。

## 关键经验（改代码前必读）

### 1. Tauri 应用的窗口显示（clash 等）

跨进程 Win32 ShowWindow/SetWindowPos 对 Tauri 应用**无效**（窗口显示逻辑在应用内部）。
正解：向该进程的 `tray_icon_app` 窗口发 `SendMessage(hwnd, 6002, 0, 0x201)`（6002=WM_USER_TRAYICON，0x201=WM_LBUTTONDOWN，lParam 低字，必须是 Down 不是 Up）。已集成于 `MirroredAppTracker.TryShowTauriAppViaTrayClick`。详见 [docs/tauri-window-show.md](docs/tauri-window-show.md)。

### 2. 禁止 SendInput 注入修饰键（右键卡住教训）

2026-08-14 定位的严重 bug：`SendInput` 注入 Ctrl/Alt/Shift/Win 的 KEYUP 会触发系统输入钩子，把右键状态卡在 DOWN，导致任务栏点击/右键全部失灵。**永远不要用 SendInput 注入修饰键**（b9c846a 曾引入此代码，已移除）。

## 架构要点

- `OverlayForm`：透明置顶 overlay，1 秒 timer → `SyncIcons()`（从注册表重新发现图标）+ `UpdateIconStates()`（刷新运行态）
- 图标来源：Win11 托盘注册表读取（`TrayIconReader`），非 ToolbarWindow32（Win11 24H2 无此窗口）
- 点击激活：`MirroredAppTracker.Activate` → 普通应用走 ToggleAppWindow（ShowWindow 切换），Tauri 应用走 tray 点击消息
- 日志：`Utils.TraceLog` → `%LOCALAPPDATA%\TrayMirror\traymirror.log`
