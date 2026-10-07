using System;
using System.Runtime.InteropServices;

namespace BBModMenu
{
    static class Native
    {
        public const uint PROCESS_VM_READ = 0x0010, PROCESS_VM_WRITE = 0x0020, PROCESS_VM_OPERATION = 0x0008;
        public const uint PROCESS_QUERY_INFORMATION = 0x0400, PROCESS_SUSPEND_RESUME = 0x0800;
        public const uint THREAD_GET_CONTEXT = 0x0008, THREAD_QUERY_INFORMATION = 0x0040;
        public const uint STILL_ACTIVE = 259;
        public const uint MEM_COMMIT = 0x1000;
        public const uint PAGE_READWRITE = 0x04, PAGE_EXECUTE = 0x10, PAGE_EXECUTE_READ = 0x20;
        public const uint PAGE_EXECUTE_READWRITE = 0x40, PAGE_EXECUTE_WRITECOPY = 0x80;

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress, AllocationBase;
            public uint AllocationProtect;
            public IntPtr RegionSize;
            public uint State, Protect, Type;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenThread(uint access, bool inherit, int tid);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(IntPtr h, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, [Out] byte[] buf, IntPtr size, out IntPtr read);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool VirtualProtectEx(IntPtr h, IntPtr addr, IntPtr size, uint prot, out uint old);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MEMORY_BASIC_INFORMATION mbi, IntPtr len);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FlushInstructionCache(IntPtr h, IntPtr addr, IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetThreadContext(IntPtr thread, IntPtr ctx);
        [DllImport("ntdll.dll")]
        public static extern int NtSuspendProcess(IntPtr h);
        [DllImport("ntdll.dll")]
        public static extern int NtResumeProcess(IntPtr h);

        // x64 CONTEXT: ContextFlags at 0x30, Rip at 0xF8, size 0x4D0, 16-byte aligned.
        public static long GetThreadRip(int tid)
        {
            IntPtr th = OpenThread(THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION, false, tid);
            if (th == IntPtr.Zero) return -1;
            IntPtr raw = Marshal.AllocHGlobal(0x4D0 + 16);
            try
            {
                IntPtr ctx = new IntPtr((raw.ToInt64() + 15) & ~15L);
                for (int i = 0; i < 0x4D0; i += 8) Marshal.WriteInt64(ctx, i, 0);
                Marshal.WriteInt32(ctx, 0x30, 0x00100001); // CONTEXT_AMD64 | CONTEXT_CONTROL
                if (!GetThreadContext(th, ctx)) return -1;
                return Marshal.ReadInt64(ctx, 0xF8);
            }
            finally
            {
                Marshal.FreeHGlobal(raw);
                CloseHandle(th);
            }
        }

        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hwnd, out RECT rc);
        [DllImport("user32.dll")]
        public static extern bool ClientToScreen(IntPtr hwnd, ref POINT pt);
        [DllImport("user32.dll")]
        public static extern int GetWindowTextLength(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")]
        public static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll")]
        public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        public const uint KEYEVENTF_KEYUP = 0x0002;
    }
}
