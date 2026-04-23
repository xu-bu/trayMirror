using System;
using System.Collections.Generic;
using System.Drawing;

namespace TrayMirror
{
    /// <summary>
    /// Windows 11 22H2+ 的 UI Automation 兜底方案。
    /// 通过 UIA 枚举系统通知区域中的可访问元素来获取托盘信息。
    /// 注意：图标图像无法通过 UIA 直接获取，只能拿到名称/工具提示。
    /// </summary>
    internal class UiAutomationTrayReader
    {
        public List<TrayIconInfo> Read()
        {
            var result = new List<TrayIconInfo>();

            // 动态加载 UIAutomationClient，避免在不需要时引入 COM 开销
            try
            {
                // 使用反射懒加载，防止 Win10 下 assembly 不一样导致崩溃
                var uiaType = Type.GetTypeFromProgID("CUIAutomation8")
                           ?? Type.GetTypeFromProgID("CUIAutomation");

                if (uiaType == null) return result;

                dynamic uia = Activator.CreateInstance(uiaType)!;

                // 查找 Shell_TrayWnd 对应的 UIA 元素
                dynamic rootEl = uia.GetRootElement();
                dynamic condition = uia.CreatePropertyCondition(
                    30005,  // UIA_ClassNamePropertyId
                    "Shell_TrayWnd");

                dynamic trayEl = rootEl.FindFirst(4 /*TreeScope_Descendants*/, condition);
                if (trayEl == null) return result;

                // 枚举所有 Button 类型的子元素（托盘图标是 Button）
                dynamic btnCondition = uia.CreatePropertyCondition(
                    30003,  // UIA_ControlTypePropertyId
                    50000); // UIA_ButtonControlTypeId

                dynamic buttons = trayEl.FindAll(4, btnCondition);
                int count = buttons.Length;

                for (int i = 0; i < count; i++)
                {
                    dynamic btn = buttons.GetElement(i);
                    string name = btn.CurrentName ?? "";
                    // UIA 无法直接给我们图标位图，只能存名称作为 tooltip
                    result.Add(new TrayIconInfo
                    {
                        Index = i,
                        IsVisible = true,
                        Tooltip = name,
                        // Icon 留 null，OverlayForm 会用默认占位符渲染
                    });
                }
            }
            catch
            {
                // 静默失败，调用方负责处理空列表
            }

            return result;
        }
    }
}