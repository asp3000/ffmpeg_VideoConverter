// ============================================================================
//  ProcessGuard.cs — identifies and lifetime-manages every ffmpeg/ffprobe
//  process spawned by this application.
//
//  Why two mechanisms:
//   1. Managed tracking list (the "identifier"):
//      We keep a reference to each Process we start, so we can tell OUR
//      ffmpeg calls apart from any other program's ffmpeg, and we only ever
//      kill the ones we own. Every child is also tagged in its command line
//      (see MakeTag) so it is recognizable in Task Manager / Process Explorer.
//   2. Windows Job Object (guaranteed kill-on-exit):
//      Each child is placed into a job configured with
//      JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. When this program exits for ANY
//      reason — normal close, unhandled exception, or being killed via Task
//      Manager — the OS closes the job handle and terminates the children.
//      This covers the cases where managed cleanup cannot run.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace VideoConverter
{
    internal static class ProcessGuard
    {
        private static readonly object _lock = new object();
        private static readonly List<Process> _children = new List<Process>();
        // temp progress file (the command-line marker) per child, deleted on exit.
        private static readonly Dictionary<Process, string> _tempFiles =
            new Dictionary<Process, string>();

        private static readonly IntPtr _job = CreateJob();
        private static bool _handlersRegistered;

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        /// <summary>
        /// Register an already-started child process for lifetime management.
        /// </summary>
        public static void Register(Process proc, string tempFile = null)
        {
            if (proc == null) return;

            lock (_lock)
            {
                _children.Add(proc);
                if (!string.IsNullOrEmpty(tempFile))
                    _tempFiles[proc] = tempFile;
            }

            // Guaranteed cleanup even if the app crashes.
            AssignToJob(proc);

            // Remove from tracking + clean up the marker file once it exits.
            proc.EnableRaisingEvents = true;
            proc.Exited += (s, e) =>
            {
                string tf = null;
                lock (_lock)
                {
                    _children.Remove(proc);
                    if (_tempFiles.TryGetValue(proc, out tf))
                        _tempFiles.Remove(proc);
                }
                if (!string.IsNullOrEmpty(tf))
                {
                    try { if (File.Exists(tf)) File.Delete(tf); } catch { }
                }
            };
        }

        /// <summary>
        /// Build a command-line marker so this app's ffmpeg is distinguishable
        /// from other programs' ffmpeg in Task Manager (Command line column).
        /// The marker is a -progress file whose path contains "VideoConverter".
        /// ffmpeg writes progress there harmlessly; we still parse stderr.
        /// </summary>
        public static string MakeTag(out string tempFile)
        {
            tempFile = Path.Combine(
                Path.GetTempPath(),
                "VideoConverter_" + Guid.NewGuid().ToString("N") + ".progress");
            return "-progress \"" + tempFile + "\"";
        }

        /// <summary>
        /// Kill every ffmpeg/ffprobe process this app started.
        /// Safe to call multiple times.
        /// </summary>
        public static void KillAll()
        {
            List<Process> snapshot;
            lock (_lock)
            {
                snapshot = new List<Process>(_children);
                _children.Clear();
            }
            foreach (var proc in snapshot)
            {
                try
                {
                    if (!proc.HasExited)
                        proc.Kill();
                }
                catch { }
            }
        }

        /// <summary>
        /// Register process-exit handlers. Must be called from the UI thread
        /// (after Application.Run is possible) — typically in Program.Main.
        /// </summary>
        public static void Initialize()
        {
            if (_handlersRegistered) return;
            _handlersRegistered = true;

            AppDomain.CurrentDomain.ProcessExit += (s, e) => KillAll();
            AppDomain.CurrentDomain.UnhandledException += (s, e) => KillAll();
        }

        // ------------------------------------------------------------------
        // Windows Job Object
        // ------------------------------------------------------------------

        private static IntPtr CreateJob()
        {
            IntPtr hJob = CreateJobObject(IntPtr.Zero, null);
            if (hJob == IntPtr.Zero) return IntPtr.Zero;

            var limit = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = 0x2000 // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            };

            int len = Marshal.SizeOf(limit);
            IntPtr p = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.StructureToPtr(limit, p, false);
                if (!SetInformationJobObject(hJob,
                        JobObjectInfoClass.JobObjectBasicLimitInformation,
                        p, (uint)len))
                {
                    // Best effort; managed KillAll covers normal exits.
                    Marshal.FreeHGlobal(p);
                    return hJob;
                }
            }
            catch
            {
                Marshal.FreeHGlobal(p);
                return hJob;
            }
            Marshal.FreeHGlobal(p);
            return hJob;
        }

        private static void AssignToJob(Process proc)
        {
            if (_job == IntPtr.Zero) return;
            try
            {
                // proc.Handle carries PROCESS_SET_QUOTA | PROCESS_TERMINATE.
                if (!AssignProcessToJobObject(_job, proc.Handle))
                {
                    // ERROR_ACCESS_DENIED (5): child already in another job
                    // (e.g. launched under a debugger). Not fatal.
                    int err = Marshal.GetLastWin32Error();
                    _ = err;
                }
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // P/Invoke
        // ------------------------------------------------------------------

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            IntPtr hJob, JobObjectInfoClass infoType,
            IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        private enum JobObjectInfoClass : uint
        {
            JobObjectBasicLimitInformation = 2
        }
    }
}
