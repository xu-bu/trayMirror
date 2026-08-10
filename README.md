# TrayMirror — 副屏托盘镜像工具

在副屏右下角显示一个模拟托盘条，镜像主屏上指定托盘应用的图标。

## 编译要求

* Windows 10 / 11
* .NET 10 SDK（含 Windows 桌面运行时）

```bash
dotnet build -c Release
dotnet run
```

## 文档

* [Tauri 应用窗口显示方案](docs/tauri-window-show.md) — 跨进程 ShowWindow 对 Tauri 无效时，用 tray_icon_app 托盘点击消息（6002 + WM_LBUTTONDOWN）触发应用内显示

## 后续可改进点

* [ ] DWM 亚克力毛玻璃背景（当前使用 color-key 透明度）
* [ ] 托盘气泡通知镜像
* [ ] 支持右键菜单