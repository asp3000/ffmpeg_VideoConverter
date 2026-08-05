// ============================================================================
//  ProcessSuspender.cs — 暂停/恢复外部进程的线程（用于 ffmpeg 任务暂停/恢复）。
//  通过 NtSuspendProcess/NtResumeProcess 原生 API 实现。
// ============================================================================

using System;
using System.Runtime.InteropServices;

namespace VideoConverter
{
    public static class ProcessSuspender
    {
        [DllImport("ntdll.dll")]
        private static extern uint NtSuspendProcess(IntPtr processHandle);

        [DllImport("ntdll.dll")]
        private static extern uint NtResumeProcess(IntPtr processHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private const uint PROCESS_SUSPEND_RESUME = 0x0800;

        /// <summary>暂停指定进程的所有线程。</summary>
        public static bool Suspend(int pid)
        {
            IntPtr handle = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
            if (handle == IntPtr.Zero) return false;
            try
            {
                uint status = NtSuspendProcess(handle);
                return status == 0; // STATUS_SUCCESS
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        /// <summary>恢复指定进程的所有线程。</summary>
        public static bool Resume(int pid)
        {
            IntPtr handle = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
            if (handle == IntPtr.Zero) return false;
            try
            {
                uint status = NtResumeProcess(handle);
                return status == 0;
            }
            finally
            {
                CloseHandle(handle);
            }
        }
    }
}
