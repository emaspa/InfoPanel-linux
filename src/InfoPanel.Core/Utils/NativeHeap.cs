using System.Runtime.InteropServices;
using System.Text;

namespace InfoPanel.Utils
{
    /// <summary>
    /// glibc malloc settings for InfoPanel and the ffmpeg processes it starts.
    /// Skia, libturbojpeg and the runtime allocate natively from many threads, and
    /// glibc gives each contending thread its own arena, up to 8 per core. On a
    /// 32-thread CPU that grew to 32 arenas holding 374 MB, much of it free but
    /// fragmented. Two arenas cut the app from 644 to 518 MB after four minutes with
    /// no measurable CPU change; malloc_trim on top of that freed nothing.
    /// </summary>
    public static class NativeHeap
    {
        public const int ArenaMax = 2;

        private const string ArenaSetting = "glibc.malloc.arena_max";
        private const int M_ARENA_MAX = -8;

        /// <summary>Set to 0 to skip the re-exec in <see cref="LimitArenas"/>.</summary>
        private const string ReexecVariable = "INFOPANEL_MALLOC_REEXEC";

        /// <summary>Carries the user's GLIBC_TUNABLES across the re-exec.</summary>
        private const string OriginalVariable = "INFOPANEL_ORIGINAL_GLIBC_TUNABLES";

        /// <summary>
        /// GLIBC_TUNABLES value with the arena limit added; a limit the user already
        /// set wins.
        /// </summary>
        public static string ChildTunables(string? existing)
        {
            if (!string.IsNullOrEmpty(existing) && existing.Contains(ArenaSetting, StringComparison.Ordinal))
            {
                return existing;
            }
            var tunable = $"{ArenaSetting}={ArenaMax}";
            return string.IsNullOrEmpty(existing) ? tunable : $"{existing}:{tunable}";
        }

        /// <summary>
        /// Call first thing in Main. glibc reads GLIBC_TUNABLES only at process start,
        /// and by the time Main runs the runtime's own threads have already created
        /// about 7 arenas, which mallopt cannot take back (589 MB instead of 518).
        /// So when the limit is missing, this sets it and execs the same binary with
        /// the same arguments once: same PID, nothing opened yet, no measurable delay.
        /// The second run puts the environment back, so programs the app launches
        /// (a browser, plugin tools) keep their own malloc settings; ffmpeg gets the
        /// limit through <see cref="ChildTunables"/>. If the exec fails, mallopt at
        /// least stops further arenas. INFOPANEL_MALLOC_REEXEC=0 skips the exec.
        /// </summary>
        public static void LimitArenas()
        {
            if (!OperatingSystem.IsLinux())
            {
                return;
            }

            try
            {
                if (Environment.GetEnvironmentVariable(OriginalVariable) is { } original)
                {
                    Restore(original);   // started by Reexec with the limit in place
                    return;
                }

                var tunables = Environment.GetEnvironmentVariable("GLIBC_TUNABLES");
                if (tunables?.Contains(ArenaSetting, StringComparison.Ordinal) == true)
                {
                    return;   // the user's own limit
                }

                if (Environment.GetEnvironmentVariable(ReexecVariable) != "0")
                {
                    Reexec(tunables);
                }
                _ = mallopt(M_ARENA_MAX, ArenaMax);
            }
            catch (Exception)
            {
                // Not glibc (musl has no mallopt or tunables): nothing to tune.
            }
        }

        // Returns only when the exec failed, with the environment unchanged.
        private static void Reexec(string? tunables)
        {
            // The original argv, including "dotnet app.dll" when not run through the apphost.
            var args = Encoding.UTF8.GetString(File.ReadAllBytes("/proc/self/cmdline")).TrimEnd('\0').Split('\0');

            // The resolved path, not /proc/self/exe: the kernel names the process after
            // the file it executes, and pkill/ps would see "exe" instead of "infopanel".
            var self = new FileInfo("/proc/self/exe").LinkTarget;
            if (args[0].Length == 0 || string.IsNullOrEmpty(self))
            {
                return;
            }

            // Native setenv: execv passes the C environ, which Environment.SetEnvironmentVariable
            // does not touch. Empty marker = GLIBC_TUNABLES was unset.
            _ = setenv(OriginalVariable, tunables ?? "", 1);
            _ = setenv("GLIBC_TUNABLES", ChildTunables(tunables), 1);

            var argv = new string?[args.Length + 1];
            Array.Copy(args, argv, args.Length);
            _ = execv(self, argv);

            Restore(tunables ?? "");
        }

        private static void Restore(string original)
        {
            // Both the C environ (native children) and .NET's copy (Process.Start).
            if (original.Length == 0)
            {
                _ = unsetenv("GLIBC_TUNABLES");
                Environment.SetEnvironmentVariable("GLIBC_TUNABLES", null);
            }
            else
            {
                _ = setenv("GLIBC_TUNABLES", original, 1);
                Environment.SetEnvironmentVariable("GLIBC_TUNABLES", original);
            }
            _ = unsetenv(OriginalVariable);
            Environment.SetEnvironmentVariable(OriginalVariable, null);
        }

        [DllImport("libc")]
        private static extern int mallopt(int param, int value);

        [DllImport("libc", CharSet = CharSet.Ansi)]
        private static extern int setenv(string name, string value, int overwrite);

        [DllImport("libc", CharSet = CharSet.Ansi)]
        private static extern int unsetenv(string name);

        [DllImport("libc", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern int execv(string path, string?[] argv);
    }
}
