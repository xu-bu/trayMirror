# TrayMirror — 副屏托盘镜像工具

在副屏右下角显示一个模拟托盘条，镜像主屏上指定托盘应用的图标。

## 编译要求

* Windows 10 / 11
* .NET 6 SDK（含 Windows 桌面运行时）

```bash
dotnet build -c Release
dotnet run
```

## 功能说明

| 功能                                | 状态           |
| ----------------------------------- | -------------- |
| 副屏右下角透明悬浮图标              | ✅             |
| 读取主屏托盘图标（ToolbarWindow32） | ✅             |
| Win11 22H2+ UI Automation 兜底      | ✅             |
| ManagedShell 图标发现 + 点击转发    | ✅（可靠实现） |
| WeChat / Everything / v2rayN 镜像   | ✅             |
| 多副屏支持                          | ✅             |
| 右键菜单气泡通知                    | ❌ 暂不支持    |

## 架构

```
Program.cs                  入口，多副屏实例化
OverlayForm.cs              副屏透明悬浮窗（绘制 + 事件）
ManagedShellTrayService.cs  ManagedShell 封装 — 托盘图标发现 + 点击转发
MirroredAppTracker.cs       进程识别 / 图标提取 / 窗口激活（回退）
TrayIconReader.cs           托盘图标读取（ToolbarWindow32 + UIA 兜底）
UiAutomationTrayReader.cs   Win11 UIA 兼容层
TrayWatcher.cs              WinEvent 钩子监听变化
NativeMethods.cs            Win32 P/Invoke 封装
```

## 已知局限

1. **点击转发依赖 ManagedShell** — ManagedShell 会接管系统托盘（`Shell_NotifyIcon`），
   重启程序后主屏托盘自动恢复。如遇问题，重启 explorer 即可：
   ```
   taskkill /f /im explorer.exe && start explorer.exe
   ```

## 后续可改进点

* [ ] DWM 亚克力毛玻璃背景（当前使用 color-key 透明度）
* [ ] 托盘气泡通知镜像
* [ ] 自定义位置（不限于右下角）
* [ ] 开机自启（写入注册表 Run 键）
