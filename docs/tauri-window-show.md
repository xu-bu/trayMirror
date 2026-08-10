# Tauri 应用窗口显示方案（tray_icon_app 托盘点击消息）

## 背景

Tauri 应用（如 Clash Verge Rev）用 `window.hide()` 隐藏窗口后，**跨进程 Win32 `ShowWindow` / `SetWindowPos` 全部无效**（2026-08-14 实测：SW_RESTORE/SW_SHOW + 等待 2 秒仍不可见）。原因是窗口的显示逻辑在应用内部，外部只能走应用自己接受的入口。

对 Tauri 应用，这个入口是**托盘点击事件**。

## 方案

向该进程的托盘回调窗口发送托盘点击消息：

```csharp
// tray_icon_app = tray-icon crate 注册到 Shell 通知区域的回调窗口（类名固定）
SendMessage(tray_icon_app_hwnd, 6002, (IntPtr)0, (IntPtr)0x201);
```

| 参数 | 值 | 说明 |
|---|---|---|
| 消息号 | `6002` | `WM_USER_TRAYICON`，tray-icon crate（0.21.x）的固定消息号，**不是** `WM_USER + n` |
| wParam | `0` | tray_proc 不检查 wParam（图标 id 在 GWL_USERDATA 的 internal_id 里） |
| lParam | `0x201` | `WM_LBUTTONDOWN`（低字）—— 必须是**按下**；clash-verge-rev 匹配 `Click { Left, Down }`，发 UP(`0x202`) 会被忽略 |

## 事件链路

```
SendMessage(tray_icon_app, 6002, 0, 0x201)
  └─ tray_proc（tray-icon wnd_proc）
       ├─ GetCursorPos() 失败 → 事件不发（极少发生）
       ├─ get_tray_rect(internal_id, hwnd)（Shell_NotifyIconGetRect）失败 → 事件不发
       │    └─ 图标未注册 / 任务栏不可见时可能失败（全屏游戏场景待验证）
       └─ 构造 TrayIconEvent::Click { button: Left, button_state: Down }
            └─ 应用内 on_tray_icon_event → show_main_window()（异步，~0.5-1s 生效）
```

## 已踩的坑

1. **消息号不是 WM_USER+1**（0x401）—— tray-icon 用的是固定值 6002（`WM_USER_TRAYICON`）。查版本：`crates.io/api/v1/crates/tray-icon/{version}/download` 下载源码，看 `src/platform_impl/windows/mod.rs` 顶部常量。
2. **lParam 必须是 DOWN(0x201) 不是 UP(0x202)** —— clash 源码 `on_tray_icon_event` 只匹配 `button_state: Down`。
3. **clash 有 625ms 托盘点击防抖**（`WINDOW_OPERATION_DEBOUNCE_MS`）—— 快速连点第二次会被拒绝。
4. 事件投递 + 应用内显示是异步的，发送后需轮询 `IsWindowVisible`（TrayMirror 实现里 6×150ms = 900ms 上限）。

## 集成位置

`MirroredAppTracker.TryShowTauriAppViaTrayClick`（Tauri 窗口隐藏时优先走此路径，失败再 fallback 到 singleton HTTP / relaunch）。

## 复用于其他 Tauri 应用

若其他 Tauri 应用跨进程 ShowWindow 无效：

1. 枚举该进程窗口，找类名 `tray_icon_app` 的窗口（`EnumWindows` + `GetWindowThreadProcessId` 匹配）。
2. 发 `6002 + 0x201`，轮询主窗口可见性。
3. 无效时用 `Shell_NotifyIconGetRect`（遍历 uID）验证图标是否注册可查 —— 查不到则事件链路会断。
4. 消息号 6002 是 tray-icon 0.21.3 的约定，其他版本可能不同，先确认 clash 同类应用使用的 tray-icon 版本。

## 为什么不走这些路线

| 方案 | 结果 |
|---|---|
| 跨进程 ShowWindow / SetWindowPos | 无效（Tauri 内部状态控制） |
| 二次启动 exe | 无效（单实例不显示窗口） |
| singleton-instance.json HTTP | 此版本 clash 无此文件 |
| UI Automation 点击托盘图标 | 全屏游戏时 UI 不可访问（用户明确排除） |
| deep link（`clash://`） | 只处理订阅导入，不显示窗口 |
| 模拟鼠标点击托盘坐标 | 输入注入有钩子卡键风险；任务栏隐藏时坐标失效 |
