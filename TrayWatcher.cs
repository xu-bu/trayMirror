using System;

namespace TrayMirror
{
    /// <summary>
    /// 通过 SetWinEventHook 监听 Shell 托盘区的窗口变化事件。
    /// 当托盘图标增减时触发 TrayChanged 事件通知主程序刷新。
    /// </summary>
    public class TrayWatcher : IDisposable
    {
        private IntPtr _hookHandle = IntPtr.Zero;
        private NativeMethods.WinEventDelegate? _hookDelegate;
        private bool _disposed;

        public event EventHandler? TrayChanged;

        public void Start()
        {
            if (_hookHandle != IntPtr.Zero) return;

            // 持有委托引用，防止 GC 回收
            _hookDelegate = OnWinEvent;

            _hookHandle = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_OBJECT_CREATE,
                NativeMethods.EVENT_OBJECT_REORDER,
                IntPtr.Zero,
                _hookDelegate,
                0,  // 所有进程
                0,  // 所有线程
                NativeMethods.WINEVENT_OUTOFCONTEXT);
        }

        public void Stop()
        {
            if (_hookHandle != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_hookHandle);
                _hookHandle = IntPtr.Zero;
            }
        }

        private void OnWinEvent(IntPtr hWinEventHook, uint eventType,
            IntPtr hwnd, int idObject, int idChild,
            uint dwEventThread, uint dwmsEventTime)
        {
            // idObject == OBJID_SYSMENU(-1) 或 OBJID_CLIENT(0) 时与托盘相关
            // 简单策略：任何窗口对象创建/销毁/重排都触发一次刷新
            // 节流交给 OverlayForm 侧的 Timer 来做
            if (eventType >= NativeMethods.EVENT_OBJECT_CREATE &&
                eventType <= NativeMethods.EVENT_OBJECT_REORDER)
            {
                TrayChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Stop();
                _disposed = true;
            }
        }
    }
}