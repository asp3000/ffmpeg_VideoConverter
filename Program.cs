// ============================================================================
//  Program.cs — standalone entry point for the VideoConverter app.
//  No dependency on the FFBatch project.
// ============================================================================

using System;
using System.Windows.Forms;

namespace VideoConverter
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // Guarantee our ffmpeg/ffprobe children are killed on any exit.
            ProcessGuard.Initialize();
            Application.ApplicationExit += (s, e) => ProcessGuard.KillAll();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new VideoConverter());
            }
            finally
            {
                // Normal / forced close both land here.
                ProcessGuard.KillAll();
            }
        }
    }
}
