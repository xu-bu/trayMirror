using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace TrayMirror
{
    /// <summary>
    /// 从 Shell_TrayWnd 的 ToolbarWindow32 读取托盘图标列表。
    /// Windows 11 22H2+ 若旧方式失败，自动切换 UI Automation 兜底。
    /// </summary>
    public class TrayIconReader : IDisposable
    {
        private bool _disposed;

        public List<TrayIconInfo> ReadIcons()
        {
            var result = TryReadViaToolbar();
            if (result.Count == 0)
                result = TryReadViaUiAutomation();
            return result;
        }

        // ── 方式一：ToolbarWindow32 + 跨进程内存读取 ────────────────
        private List<TrayIconInfo> TryReadViaToolbar()
        {
            var icons = new List<TrayIconInfo>();

            IntPtr trayWnd = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (trayWnd == IntPtr.Zero) return icons;

            // 尝试两个常见路径（Win10 / Win11 结构略有不同）
            IntPtr toolbar = FindToolbar(trayWnd);
            if (toolbar == IntPtr.Zero) return icons;

            NativeMethods.GetWindowThreadProcessId(toolbar, out uint pid);
            IntPtr hProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_ALL_ACCESS, false, pid);
            if (hProcess == IntPtr.Zero) return icons;

            try
            {
                int btnCount = (int)NativeMethods.SendMessage(toolbar,
                    NativeMethods.TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero);

                int btnSize = Marshal.SizeOf<NativeMethods.TBBUTTON>();
                IntPtr remotePtr = NativeMethods.VirtualAllocEx(hProcess, IntPtr.Zero,
                    (uint)btnSize, NativeMethods.MEM_COMMIT, NativeMethods.PAGE_READWRITE);

                if (remotePtr == IntPtr.Zero) return icons;

                try
                {
                    for (int i = 0; i < btnCount; i++)
                    {
                        NativeMethods.SendMessage(toolbar, NativeMethods.TB_GETBUTTON,
                            new IntPtr(i), remotePtr);

                        var localBuf = new byte[btnSize];
                        NativeMethods.ReadProcessMemory(hProcess, remotePtr,
                            localBuf, (uint)btnSize, out _);

                        var btn = BytesToStruct<NativeMethods.TBBUTTON>(localBuf);

                        // dwData 在系统托盘场景下存的是 NOTIFYICONDATA 指针，
                        // 我们只能读到图标句柄 iBitmap（实际是图标索引）
                        // 真正的图标要通过 NOTIFYICONIDENTIFIER + Shell_NotifyIconGetRect 拿
                        var info = new TrayIconInfo
                        {
                            Index = i,
                            CommandId = btn.idCommand,
                            IsVisible = (btn.fsState & 0x08) == 0,   // TBSTATE_HIDDEN = 0x08
                        };

                        // 尝试从进程内读取图标句柄（dwData 字段）
                        var iconHandleBuf = new byte[IntPtr.Size];
                        IntPtr dwDataRemote = new IntPtr(remotePtr.ToInt64() +
                            GetFieldOffset_dwData());
                        NativeMethods.ReadProcessMemory(hProcess, new IntPtr(remotePtr.ToInt64()
                            + GetFieldOffset_dwData()), iconHandleBuf, (uint)IntPtr.Size, out _);

                        IntPtr iconPtr = new IntPtr(IntPtr.Size == 8
                            ? BitConverter.ToInt64(iconHandleBuf, 0)
                            : BitConverter.ToInt32(iconHandleBuf, 0));

                        if (iconPtr != IntPtr.Zero)
                        {
                            IntPtr copied = NativeMethods.CopyIcon(iconPtr);
                            if (copied != IntPtr.Zero)
                                info.Icon = Icon.FromHandle(copied);
                        }

                        icons.Add(info);
                    }
                }
                finally
                {
                    NativeMethods.VirtualFreeEx(hProcess, remotePtr, 0, NativeMethods.MEM_RELEASE);
                }
            }
            finally
            {
                NativeMethods.CloseHandle(hProcess);
            }

            return icons;
        }

        // ── 方式二：UI Automation 兜底（Win11 22H2+）───────────────
        private List<TrayIconInfo> TryReadViaUiAutomation()
        {
            var icons = new List<TrayIconInfo>();
            try
            {
                // 懒加载 UIAutomationClient，避免在支持 ToolbarWindow32 的机器上引入依赖
                var uia = new UiAutomationTrayReader();
                icons = uia.Read();
            }
            catch
            {
                // UI Automation 也失败时静默返回空列表
            }
            return icons;
        }

        private static IntPtr FindToolbar(IntPtr trayWnd)
        {
            // 路径 1: Shell_TrayWnd → TrayNotifyWnd → SysPager → ToolbarWindow32
            IntPtr notify = NativeMethods.FindWindowEx(trayWnd, IntPtr.Zero, "TrayNotifyWnd", null);
            if (notify != IntPtr.Zero)
            {
                IntPtr sysPager = NativeMethods.FindWindowEx(notify, IntPtr.Zero, "SysPager", null);
                if (sysPager != IntPtr.Zero)
                {
                    IntPtr tb = NativeMethods.FindWindowEx(sysPager, IntPtr.Zero, "ToolbarWindow32", null);
                    if (tb != IntPtr.Zero) return tb;
                }
            }

            // 路径 2: 直接子窗口中查找（Win11 部分版本）
            IntPtr child = NativeMethods.FindWindowEx(trayWnd, IntPtr.Zero, "ToolbarWindow32", null);
            return child;
        }

        private static T BytesToStruct<T>(byte[] bytes) where T : struct
        {
            GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try { return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject()); }
            finally { handle.Free(); }
        }

        // dwData 字段的偏移量（iBitmap:4 + idCommand:4 + fsState:1 + fsStyle:1 + reserved:2 = 12）
        private static int GetFieldOffset_dwData()
        {
            // 64 位下有对齐 padding，使用 Marshal 自动计算更安全
            return Marshal.OffsetOf<NativeMethods.TBBUTTON>("dwData").ToInt32();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
            }
        }
    }

    public class TrayIconInfo
    {
        public int Index { get; set; }
        public int CommandId { get; set; }
        public bool IsVisible { get; set; }
        public Icon? Icon { get; set; }
        public string Tooltip { get; set; } = string.Empty;

        public void Dispose() => Icon?.Dispose();
    }
}