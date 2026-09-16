namespace InfoPanel.Utils;

/// <summary>
/// Shared as source by Core (net10) and Extras (net8), without adding a host
/// assembly dependency to the isolated plugin. Never depends on a directory
/// already existing, unlike GetFolderPath(LocalApplicationData).
/// </summary>
internal static class DataDirectory
{
    private static readonly object CacheLock = new();
    private static BaseFolderInputs _cachedInputs;
    private static string? _cachedBaseFolder;

    private readonly record struct BaseFolderInputs(
        string? Override, string? DataDirectory, string? XdgDataHome, string? Home, string? WorkingDirectory);

    public static string GetXdgDataDirectory()
    {
        return EnsureDirectory(ResolveXdgDataDirectory(
            Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
            Environment.GetEnvironmentVariable("HOME")));
    }

    private static string ResolveXdgDataDirectory(string? directory, string? home)
    {
        if (string.IsNullOrEmpty(directory) || !Path.IsPathFullyQualified(directory))
        {
            if (string.IsNullOrEmpty(home) || !Path.IsPathFullyQualified(home))
            {
                home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile,
                    Environment.SpecialFolderOption.DoNotVerify);
            }

            if (string.IsNullOrEmpty(home) || !Path.IsPathFullyQualified(home))
            {
                throw new InvalidOperationException("Cannot resolve an absolute home directory for InfoPanel data.");
            }

            directory = Path.Combine(home, ".local", "share");
        }

        return directory;
    }

    // Only a cache miss normalizes/creates the directory. Writers must create
    // their own destination directory, since it can be deleted after resolution.
    public static string GetBaseFolder(string? baseFolderOverride = null)
    {
        lock (CacheLock)
        {
            var dataDirectory = Environment.GetEnvironmentVariable("INFOPANEL_DATA_DIR");
            var directory = baseFolderOverride ?? dataDirectory;
            // Relative explicit overrides also depend on the process CWD. Avoid
            // querying it (getcwd on Unix) for the normal absolute/XDG paths.
            var workingDirectory = !string.IsNullOrEmpty(directory) && !Path.IsPathFullyQualified(directory)
                ? Environment.CurrentDirectory : null;
            var inputs = new BaseFolderInputs(baseFolderOverride, dataDirectory,
                Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
                Environment.GetEnvironmentVariable("HOME"), workingDirectory);
            if (_cachedBaseFolder != null && inputs == _cachedInputs)
            {
                return _cachedBaseFolder;
            }

            if (string.IsNullOrEmpty(directory))
            {
                directory = Path.Combine(ResolveXdgDataDirectory(inputs.XdgDataHome, inputs.Home), "InfoPanel");
            }

            // Resolve relative paths against the same CWD captured in the key.
            // Publish the key and path together, and only after creation succeeds.
            var resolved = EnsureDirectory(workingDirectory == null
                ? directory : Path.Combine(workingDirectory, directory));
            _cachedInputs = inputs;
            _cachedBaseFolder = resolved;
            return resolved;
        }
    }

    private static string EnsureDirectory(string directory)
    {
        var absolutePath = Path.GetFullPath(directory);
        Directory.CreateDirectory(absolutePath);
        return absolutePath;
    }
}
