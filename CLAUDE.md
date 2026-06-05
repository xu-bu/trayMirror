# TrayMirror — 副屏托盘镜像工具

Mirrors system tray icons for specific apps (WeChat, Everything, v2rayN) onto secondary monitors as a floating overlay bar.

## Architecture

| File | Responsibility |
|---|---|
| Program.cs | Entry point, multi-screen instantiation, system tray icon |
| OverlayForm.cs | Secondary screen overlay (drawing icons, mouse click handling, acrylic backdrop) |
| ManagedShellTrayService.cs | ManagedShell wrapper — tray icon discovery + click forwarding via `IconMouseDown/Up` |
| MirroredAppTracker.cs | Process detection, icon extraction, **window activation** (fallback) |
| TrayIconReader.cs | Tray icon reading (ToolbarWindow32 + UIA fallback for Win11) — **legacy, kept as fallback** |
| UiAutomationTrayReader.cs | Win11 compatibility layer |
| TrayWatcher.cs | WinEvent hooks for tray change detection |
| NativeMethods.cs | Win32 P/Invoke wrappers (DWM, SendInput, etc.) |

## Key design details

- Overlay uses `WS_EX_NOACTIVATE` + `WS_EX_TOPMOST` to avoid stealing focus, plus `TransparencyKey = Fuchsia` for a transparent background that shows only icons
- **Click forwarding**: ManagedShell's `NotifyIcon.IconMouseDown/Up` properly dispatches tray callbacks to the target app. Falls back to window activation if ManagedShell icon not found.
- **Background**: Color-key transparency (`TransparencyKey = Fuchsia`) — icons float on a completely transparent window. Dark pill-shaped backgrounds drawn behind each icon for visibility against any wallpaper.
- Tray icons are read via cross-process memory (ToolbarWindow32) with UIA fallback — **ManagedShell is the primary click path, old TrayIconReader is kept for display**
- Supports multiple secondary screens — one OverlayForm per screen
- .NET 6, Windows 10/11 only

## Build

```
dotnet build -c Release
```
