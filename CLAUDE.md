# TrayMirror — 副屏托盘镜像工具

在副屏右下角显示模拟托盘条，镜像主屏托盘应用的图标；点击图标 = 激活/隐藏对应应用窗口。

## 关键经验（改代码前必读）

### 1. Tauri 应用的窗口显示（clash 等）

跨进程 Win32 ShowWindow/SetWindowPos 对 Tauri 应用**无效**（窗口显示逻辑在应用内部）。
正解：向该进程的 `tray_icon_app` 窗口发 `SendMessage(hwnd, 6002, 0, 0x201)`（6002=WM_USER_TRAYICON，0x201=WM_LBUTTONDOWN，lParam 低字，必须是 Down 不是 Up）。已集成于 `MirroredAppTracker.TryShowTauriAppViaTrayClick`。详见 [docs/tauri-window-show.md](docs/tauri-window-show.md)。

### 2. 弹出 UI（右键菜单 / 图标抽屉）打开时必须暂停刷新

进程枚举 + 图标提取单次可达 1~2 秒，现在通过 `Task.Run` 放到线程池执行，UI 线程只合并结果并重画。但结果合并、抽屉内容更新仍在 UI 线程，右键菜单或托盘抽屉打开期间应暂停刷新，避免内容跳动或偶发卡顿。

由 `Overlay.SetRefreshPaused()`（菜单）和 `SetDrawerPaused()`（抽屉）分别置位，二者任一为真就停掉 timer；扫描中途被打断的结果直接释放丢弃。`Refresh: menu/drawer paused|resumed` 会写进日志，方便排查卡顿。

### 3. 禁止 SendInput 注入修饰键（右键卡住教训）

2026-08-14 定位的严重 bug：`SendInput` 注入 Ctrl/Alt/Shift/Win 的 KEYUP 会触发系统输入钩子，把右键状态卡在 DOWN，导致任务栏点击/右键全部失灵。**永远不要用 SendInput 注入修饰键**（b9c846a 曾引入此代码，已移除）。

## 架构要点

- `OverlayForm`：透明置顶 overlay，3 秒 timer → 后台线程 `TrayIconReader.ReadAll()` 扫描，UI 线程只做 `ApplySnapshot` 合并（顺带刷新进程 ID / 图标路径）和重画；同一时间只允许一个扫描任务
- 图标来源：Win11 托盘注册表读取（`TrayIconReader`），非 ToolbarWindow32（Win11 24H2 无此窗口）
- 点击激活：`MirroredAppTracker.Activate` → 普通应用走 ToggleAppWindow（ShowWindow 切换），Tauri 应用走 tray 点击消息
- 日志：`Utils.TraceLog` → `%LOCALAPPDATA%\TrayMirror\traymirror.log`
