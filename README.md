# TrayMirror — 副屏托盘镜像工具

在副屏右下角显示一个模拟托盘条，镜像主屏系统托盘的图标，并显示时间、音量、网络状态。

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
| 副屏右下角悬浮托盘条                | ✅             |
| 读取主屏托盘图标（ToolbarWindow32） | ✅             |
| Win11 22H2+ UI Automation 兜底      | ✅             |
| WinEvent 钩子监听托盘变化           | ✅             |
| 系统时钟 / 音量 / 网络状态          | ✅             |
| 点击图标转发到主屏                  | ✅（基础实现） |
| 多副屏支持                          | ✅             |
| 右键菜单气泡通知                    | ❌ 暂不支持    |

## 架构

```
Program.cs              入口，多副屏实例化
OverlayForm.cs          副屏覆盖窗口（绘制 + 事件）
TrayIconReader.cs       托盘图标读取（ToolbarWindow32 + UIA 兜底）
UiAutomationTrayReader  Win11 兼容层
TrayWatcher.cs          WinEvent 钩子监听变化
SystemInfoProvider.cs   时钟 / 音量 / 网络
NativeMethods.cs        Win32 P/Invoke 封装
```

## 已知局限

1. **图标获取不完整** ：Windows 系统托盘对第三方进程访问图标有限制，
   部分图标无法读取到位图，会显示灰色占位符。
   完整方案需要注入 explorer.exe 进程（涉及安全风险，不推荐）。
2. **点击转发精度** ：通过 PostMessage 模拟点击有时不够精确，
   特别是对使用自定义消息协议的第三方托盘程序。
3. **Win11 动效版本** ：2024 H2 之后微软重构了托盘区（引入 WinUI3），
   如果 ToolbarWindow32 路径和 UIA 都失效，需要改用新的窗口类名。

## 后续可改进点

* [ ] 使用 `Shell_NotifyIconGetRect` API 精确定位每个图标
* [ ] 增加透明背景 + 毛玻璃效果（需要 DWM API）
* [ ] 托盘气泡通知镜像
* [ ] 自定义位置（不限于右下角）
* [ ] 开机自启（写入注册表 Run 键）
