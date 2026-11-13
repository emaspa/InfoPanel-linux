using System.Runtime.InteropServices;

namespace InfoPanel.Utils
{
    /// <summary>
    /// Keeps Avalonia from registering InfoPanel with the X11 session manager (XSMP).
    /// Avalonia opens that connection whenever SESSION_MANAGER is set, and it goes
    /// wrong both ways on logout: a cancelled ShutdownRequested is passed back to
    /// the session manager, so KDE aborts the logout ('Logout canceled by ""', #15),
    /// and when the shutdown is let through, Avalonia's Die handler closes the
    /// connection twice and the process dies with SIGBUS before the panel devices
    /// are stopped. InfoPanel needs neither session save nor restore; the session
    /// ends it with SIGTERM, which the signal handler in App turns into the normal
    /// device-stopping shutdown (#2).
    /// </summary>
    public static class SessionManager
    {
        /// <summary>
        /// Call in Main before the Avalonia platform starts. Only the C environ that
        /// libSM reads changes: programs started with Process.Start (ffmpeg, a browser)
        /// get .NET's own copy of the environment and stay in the session.
        /// </summary>
        public static void Detach()
        {
            if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("SESSION_MANAGER") == null)
            {
                return;
            }

            try
            {
                _ = unsetenv("SESSION_MANAGER");
            }
            catch (Exception)
            {
            }
        }

        [DllImport("libc", CharSet = CharSet.Ansi)]
        private static extern int unsetenv(string name);
    }
}
