using System.Text;

namespace InfoPanel.Services;

/// <summary>Injectable /sys access. Metadata operations are used only by discovery.</summary>
public class SysfsAccess
{
    public string Root { get; }
    public SysfsAccess(string root = "/sys") => Root = Path.GetFullPath(root).TrimEnd('/');
    public string PathUnderRoot(params string[] parts) => Path.Combine([Root, .. parts]);
    public virtual string? ReadMetadata(string path) => ReadSmallFile(path);
    public virtual string? ReadValue(string path) => ReadSmallFile(path);
    public virtual bool FileExists(string path) => File.Exists(path);
    public virtual string[] GetDirectories(string path, string pattern = "*")
    {
        try { return Directory.GetDirectories(path, pattern); }
        catch (DirectoryNotFoundException) { return []; }
    }
    public virtual string[] GetFiles(string path, string pattern = "*")
    {
        try { return Directory.GetFiles(path, pattern); }
        catch (DirectoryNotFoundException) { return []; }
    }

    /// <summary>Resolve every path component (including symlinked parents), with a bounded link walk.</summary>
    public virtual string? ResolvePath(string path)
    {
        try
        {
            var pending = new Queue<string>(Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries));
            var current = "/";
            var links = 0;
            while (pending.TryDequeue(out var part))
            {
                if (part == ".") continue;
                if (part == "..") { current = Path.GetDirectoryName(current) ?? "/"; continue; }
                current = Path.Combine(current, part);
                var target = new FileInfo(current).LinkTarget;
                if (target == null) continue;
                if (++links > 40) return null;
                var remaining = pending.ToArray();
                pending = new Queue<string>(target.Split('/', StringSplitOptions.RemoveEmptyEntries).Concat(remaining));
                current = Path.IsPathRooted(target) ? "/" : Path.GetDirectoryName(current)!;
            }
            if (!current.StartsWith(Root + "/", StringComparison.Ordinal) && current != Root) return null;
            return Directory.Exists(current) || File.Exists(current) ? current : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? ReadSmallFile(string path)
    {
        const int limit = 4096;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[limit + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = stream.Read(bytes, count, bytes.Length - count);
                if (read == 0) break;
                count += read;
            }
            return count > limit ? null : new UTF8Encoding(false, true).GetString(bytes, 0, count).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException) { return null; }
    }
}
