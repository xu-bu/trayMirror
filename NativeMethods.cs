using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TrayMirror
{
    internal static class NativeMethods
    {
        // ── 窗口查找 ────────────────────────────────────────────────
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter,
            string? lpszClass, string? lpszWindow);

        // ── 窗口信息 ────────────────────────────────────────────────
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        // ── 消息发送 ────────────────────────────────────────────────
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        // ── 跨进程内存读取（用于 TB_GETBUTTON）─────────────────────
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress,
            uint dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress,
            uint dwSize, uint dwFreeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
            byte[] lpBuffer, uint nSize, out IntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
            byte[] lpBuffer, uint nSize, out IntPtr lpNumberOfBytesRead);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        // ── WinEvent 钩子 ───────────────────────────────────────────
        public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType,
            IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
            IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc,
            uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        // ── 图标 / 图像 ─────────────────────────────────────────────
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop,
            IntPtr hIcon, int cxWidth, int cyWidth, uint istepIfAniCur,
            IntPtr hbrFlickerFreeDraw, uint diFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        public static extern IntPtr CopyIcon(IntPtr hIcon);

        // ── 鼠标 ────────────────────────────────────────────────────
        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int X, int Y);

        // ── 常量 ────────────────────────────────────────────────────
        public const uint PROCESS_ALL_ACCESS = 0x1F0FFF;
        public const uint MEM_COMMIT = 0x1000;
        public const uint MEM_RELEASE = 0x8000;
        public const uint PAGE_READWRITE = 0x04;

        public const uint TB_BUTTONCOUNT = 0x0418;
        public const uint TB_GETBUTTON = 0x0417;

        public const uint WM_LBUTTONDOWN = 0x0201;
        public const uint WM_LBUTTONUP = 0x0202;
        public const uint WM_RBUTTONDOWN = 0x0204;
        public const uint WM_RBUTTONUP = 0x0205;

        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        public const uint EVENT_OBJECT_CREATE = 0x8000;
        public const uint EVENT_OBJECT_DESTROY = 0x8001;
        public const uint EVENT_OBJECT_SHOW = 0x8002;
        public const uint EVENT_OBJECT_HIDE = 0x8003;
        public const uint EVENT_OBJECT_REORDER = 0x8004;

        public const uint DI_NORMAL = 0x0003;

        // ── 结构体 ──────────────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X, Y;
        }

        // TBBUTTON 结构（32/64 bit 通用布局）
        [StructLayout(LayoutKind.Sequential)]
        public struct TBBUTTON
        {
            public int iBitmap;
            public int idCommand;
            public byte fsState;
            public byte fsStyle;
            public byte bReserved1;
            public byte bReserved2;
            public IntPtr dwData;   // 存放图标句柄
            public IntPtr iString;
        }
    }
}