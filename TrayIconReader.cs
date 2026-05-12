using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
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

        public int LastToolbarCount { get; private set; }
        public int LastToolbarVisibleCount { get; private set; }
        public int LastUiaCount { get; private set; }
        public string? LastToolbarError { get; private set; }
        public string? LastUiaError { get; private set; }

        public List<TrayIconInfo> ReadIcons()
        {
            LastToolbarCount = 0;
            LastToolbarVisibleCount = 0;
            LastUiaCount = 0;
            LastToolbarError = null;
            LastUiaError = null;

            var toolbarIcons = TryReadViaToolbar();
            LastToolbarCount = toolbarIcons.Count;
            LastToolbarVisibleCount = toolbarIcons.Count(i => i.IsVisible);

            var uiaIcons = TryReadViaUiAutomation();
            LastUiaCount = uiaIcons.Count;

            return MergeResults(toolbarIcons, uiaIcons);
        }

        private static List<TrayIconInfo> MergeResults(List<TrayIconInfo> toolbarIcons, List<TrayIconInfo> uiaIcons)
        {
            if (toolbarIcons.Count == 0)
                return uiaIcons;

            if (uiaIcons.Count == 0)
                return toolbarIcons;

            // If the toolbar path "found" buttons but none are actually visible,
            // prefer UIA so the overlay doesn't render empty.
            if (toolbarIcons.All(i => !i.IsVisible))
                return uiaIcons;

            var merged = new List<TrayIconInfo>(toolbarIcons);
            var visibleToolbarIcons = merged.Where(icon => icon.IsVisible).ToList();
            int matchedCount = Math.Min(visibleToolbarIcons.Count, uiaIcons.Count);

            for (int i = 0; i < matchedCount; i++)
            {
                if (string.IsNullOrWhiteSpace(visibleToolbarIcons[i].Tooltip))
                    visibleToolbarIcons[i].Tooltip = uiaIcons[i].Tooltip;
            }

            for (int i = matchedCount; i < uiaIcons.Count; i++)
            {
                merged.Add(new TrayIconInfo
                {
                    Index = -1,
                    CommandId = 0,
                    IsVisible = true,
                    Tooltip = uiaIcons[i].Tooltip,
                    Icon = null,
                });
            }

            return merged;
        }

        // ── 方式一：ToolbarWindow32 + 跨进程内存读取 ────────────────
        private List<TrayIconInfo> TryReadViaToolbar()
        {
            var icons = new List<TrayIconInfo>();

            try
            {
                IntPtr trayWnd = NativeMethods.FindWindow("Shell_TrayWnd", null);
                if (trayWnd == IntPtr.Zero) return icons;

                IntPtr toolbar = FindToolbar(trayWnd);
                if (toolbar == IntPtr.Zero) return icons;

                NativeMethods.GetWindowThreadProcessId(toolbar, out uint pid);
                IntPtr hProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_TRAY_READ, false, pid);
                if (hProcess == IntPtr.Zero) return icons;

                int btnCount = (int)NativeMethods.SendMessage(toolbar,
                    NativeMethods.TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero);
                IntPtr imageList = NativeMethods.SendMessage(toolbar,
                    NativeMethods.TB_GETIMAGELIST, IntPtr.Zero, IntPtr.Zero);

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
                        var info = new TrayIconInfo
                        {
                            Index = i,
                            CommandId = btn.idCommand,
                            IsVisible = (btn.fsState & 0x08) == 0,
                        };

                        info.Icon = TryExtractToolbarIcon(imageList, btn)
                            ?? TryExtractTrayDataIcon(hProcess, btn.dwData);



                        icons.Add(info);
                    }
                }
                finally
                {
                    NativeMethods.VirtualFreeEx(hProcess, remotePtr, 0, NativeMethods.MEM_RELEASE);
                }
                NativeMethods.CloseHandle(hProcess);
            }
            catch (Exception ex)
            {
                LastToolbarError = ex.GetType().Name + ": " + ex.Message;
            }

            return icons;
        }

        // ── 方式二：UI Automation 兜底（Win11 22H2+）───────────────
        private List<TrayIconInfo> TryReadViaUiAutomation()
        {
            var icons = new List<TrayIconInfo>();
            try
            {
                var uia = new UiAutomationTrayReader();
                icons = uia.Read();
            }
            catch (Exception ex)
            {
                LastUiaError = ex.GetType().Name + ": " + ex.Message;
            }
            return icons;
        }

        private static IntPtr FindToolbar(IntPtr trayWnd)
        {
            // Win11 variants have changed the tray window tree multiple times.
            // Prefer a robust recursive search for ToolbarWindow32.
            var tb = FindFirstDescendantByClass(trayWnd, "ToolbarWindow32");
            if (tb != IntPtr.Zero) return tb;

            // Hidden icons live in the overflow window.
            IntPtr overflow = NativeMethods.FindWindow("NotifyIconOverflowWindow", null);
            if (overflow != IntPtr.Zero)
            {
                tb = FindFirstDescendantByClass(overflow, "ToolbarWindow32");
                if (tb != IntPtr.Zero) return tb;
            }

            return IntPtr.Zero;
        }

        private static IntPtr FindFirstDescendantByClass(IntPtr root, string wantedClass)
        {
            if (root == IntPtr.Zero) return IntPtr.Zero;

            IntPtr found = IntPtr.Zero;
            var sb = new System.Text.StringBuilder(256);

            bool Callback(IntPtr hwnd, IntPtr _)
            {
                if (found != IntPtr.Zero) return false;
                sb.Clear();
                NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
                if (string.Equals(sb.ToString(), wantedClass, StringComparison.Ordinal))
                {
                    found = hwnd;
                    return false;
                }

                // Recurse
                NativeMethods.EnumChildWindows(hwnd, Callback, IntPtr.Zero);
                return found == IntPtr.Zero;
            }

            NativeMethods.EnumChildWindows(root, Callback, IntPtr.Zero);
            return found;
        }

        private static Icon? TryExtractToolbarIcon(IntPtr imageList, NativeMethods.TBBUTTON button)
        {
            if (imageList == IntPtr.Zero || button.iBitmap < 0)
                return null;

            IntPtr rawIcon = NativeMethods.ImageList_GetIcon(imageList, button.iBitmap, NativeMethods.ILD_TRANSPARENT);
            if (rawIcon == IntPtr.Zero)
                return null;

            try
            {
                IntPtr copied = NativeMethods.CopyIcon(rawIcon);
                return copied == IntPtr.Zero ? null : (Icon)Icon.FromHandle(copied).Clone();

            }
            finally
            {
                NativeMethods.DestroyIcon(rawIcon);
            }
        }

        private static Icon? TryExtractTrayDataIcon(IntPtr hProcess, IntPtr trayDataPtr)
        {
            if (trayDataPtr == IntPtr.Zero)
                return null;

            var iconHandleBuf = new byte[IntPtr.Size];
            NativeMethods.ReadProcessMemory(hProcess, new IntPtr(trayDataPtr.ToInt64() + GetTrayDataIconOffset()),
                iconHandleBuf, (uint)IntPtr.Size, out _);

            IntPtr iconPtr = new IntPtr(IntPtr.Size == 8
                ? BitConverter.ToInt64(iconHandleBuf, 0)
                : BitConverter.ToInt32(iconHandleBuf, 0));

            if (iconPtr == IntPtr.Zero)
                return null;

            IntPtr copied = NativeMethods.CopyIcon(iconPtr);
            return copied == IntPtr.Zero ? null : (Icon)Icon.FromHandle(copied).Clone();
        }


        private static T BytesToStruct<T>(byte[] bytes) where T : struct
        {
            GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try { return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject()); }
            finally { handle.Free(); }
        }


        private static int GetFieldOffset_dwData()
        {
            return Marshal.OffsetOf<NativeMethods.TBBUTTON>("dwData").ToInt32();
        }



        private static int GetTrayDataIconOffset()
        {
            return IntPtr.Size == 8 ? 24 : 20;
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
