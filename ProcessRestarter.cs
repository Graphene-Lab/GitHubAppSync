using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace GitHubAppSync
{
    /// <summary>
    /// Restarts the running application after an update has been applied.
    /// </summary>
    internal static class ProcessRestarter
    {
        internal static void Restart()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var executable = CurrentExecutablePath();
                if (executable != null && File.Exists(executable))
                {
                    Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
                    return;
                }

                // Without a path to relaunch there is nothing better to do than exit; the
                // supervisor or the user brings the application back on the new build.
                Environment.Exit(0);
                return;
            }

            // On Linux and macOS the application normally runs under a supervisor (systemd unit or
            // launchd daemon) that restarts it. Exiting cleanly hands control back to it; spawning a
            // child from a daemon would detach it from that supervision.
            Environment.Exit(0);
        }

        private static string CurrentExecutablePath()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                return process.MainModule?.FileName;
            }
            catch
            {
                return null;
            }
        }
    }
}
