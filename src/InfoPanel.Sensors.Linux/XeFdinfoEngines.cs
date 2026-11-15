using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace InfoPanel.Services;

/// <summary>
/// Per-engine-class busy percentage for an xe card from DRM fdinfo
/// (Documentation/gpu/drm-usage-stats.rst). Every process holding one of the card's DRM
/// nodes open is a client; its fdinfo has drm-cycles-&lt;class&gt; (busy GPU ticks) and
/// drm-total-cycles-&lt;class&gt; (the GPU timestamp at the read). A class's utilization is
/// the sum over clients of Δcycles/Δtotal, divided by drm-engine-capacity-&lt;class&gt;.
///
/// Cost: xe takes GT forcewake for every fdinfo read, so each read briefly wakes the GPU
/// out of RC6. That is why this is opt-in and only runs while an engine sensor is wanted.
/// Only processes of the same user are readable, which covers games and the desktop.
/// </summary>
internal sealed class XeFdinfoEngines(string procRoot, string pdev, IReadOnlySet<string> nodePaths, int selfPid)
{
    /// <summary>
    /// New processes are looked for this often; known fds are read every sample. Walking every
    /// process's fds (readlink each one) took 50 ms of CPU for 13,600 fds on a desktop, so a
    /// process already walked is walked again only every <see cref="FullWalkMs"/>, in case it
    /// opened the GPU later.
    /// </summary>
    internal const long RescanMs = 5_000;
    internal const long FullWalkMs = 30_000;
    private const int MaxFdinfo = 16 * 1024;

    private readonly Dictionary<string, (ulong Cycles, ulong Total)> _previous = new(StringComparer.Ordinal);
    private readonly HashSet<string> _classes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _capacity = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long WalkedMs, List<string> Fds)> _pids = new(StringComparer.Ordinal);
    private List<string> _fdinfoPaths = [];
    private bool _primed;
    private long _scannedAtMs = long.MinValue / 2;

    /// <summary>
    /// Busy percent per engine class (rcs, bcs, vcs, vecs, ccs) seen so far. Empty on the first
    /// sample; afterwards a class with no client (nothing on the GPU) reads 0.
    /// </summary>
    public Dictionary<string, double> Sample(long nowMs)
    {
        if (nowMs - _scannedAtMs >= RescanMs)
        {
            _fdinfoPaths = FindClientFds(nowMs);
            _scannedAtMs = nowMs;
        }

        var busy = new Dictionary<string, double>(StringComparer.Ordinal);
        var current = new Dictionary<string, (ulong, ulong)>(StringComparer.Ordinal);
        var seenClients = new HashSet<string>(StringComparer.Ordinal);
        var live = new List<string>(_fdinfoPaths.Count);

        foreach (var path in _fdinfoPaths)
        {
            if (ReadFdinfo(path) is not { } keys) continue;   // process exited or fd closed
            if (!keys.TryGetValue("drm-driver", out var driver) || driver != "xe"
                || !keys.TryGetValue("drm-pdev", out var dev) || !string.Equals(dev, pdev, StringComparison.OrdinalIgnoreCase)
                || !keys.TryGetValue("drm-client-id", out var client)) continue;
            live.Add(path);
            // A dup'd or inherited fd shares its client; count it once.
            if (!seenClients.Add(client)) continue;

            foreach (var (key, value) in keys)
            {
                if (key.StartsWith("drm-engine-capacity-", StringComparison.Ordinal))
                {
                    if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var cap) && cap > 0)
                        _capacity[key["drm-engine-capacity-".Length..]] = cap;
                    continue;
                }
                if (!key.StartsWith("drm-cycles-", StringComparison.Ordinal)) continue;
                var cls = key["drm-cycles-".Length..];
                if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var cycles)
                    || !keys.TryGetValue("drm-total-cycles-" + cls, out var totalText)
                    || !ulong.TryParse(totalText, NumberStyles.None, CultureInfo.InvariantCulture, out var total)) continue;

                _classes.Add(cls);
                var id = client + "/" + cls;
                current[id] = (cycles, total);
                if (_previous.TryGetValue(id, out var prev) && total > prev.Total && cycles >= prev.Cycles)
                    busy[cls] = busy.GetValueOrDefault(cls) + (double)(cycles - prev.Cycles) / (total - prev.Total);
            }
        }

        _fdinfoPaths = live;
        _previous.Clear();
        foreach (var (id, value) in current) _previous[id] = value;

        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (!_primed)
        {
            _primed = true;
            return result;
        }
        foreach (var cls in _classes)
        {
            var share = busy.GetValueOrDefault(cls);
            result[cls] = Math.Clamp(Math.Round(share * 100 / _capacity.GetValueOrDefault(cls, 1), 1), 0, 100);
        }
        return result;
    }

    /// <summary>fdinfo paths of every fd, in readable processes other than ours, open on one of the card's nodes.</summary>
    private List<string> FindClientFds(long nowMs)
    {
        string[] pidDirs;
        try { pidDirs = Directory.GetDirectories(procRoot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }

        var alive = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pidDir in pidDirs)
        {
            var pid = Path.GetFileName(pidDir);
            if (pid.Length == 0 || !char.IsAsciiDigit(pid[0]) || !int.TryParse(pid, out var n) || n == selfPid) continue;
            alive.Add(pid);
            if (_pids.TryGetValue(pid, out var known) && nowMs - known.WalkedMs < FullWalkMs) continue;
            _pids[pid] = (nowMs, WalkFds(pidDir));
        }
        foreach (var pid in _pids.Keys.Where(p => !alive.Contains(p)).ToList()) _pids.Remove(pid);
        return _pids.Values.SelectMany(p => p.Fds).ToList();
    }

    private List<string> WalkFds(string pidDir)
    {
        var found = new List<string>();
        string[] fds;
        try { fds = Directory.GetFiles(Path.Combine(pidDir, "fd")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return found; }

        foreach (var fd in fds)
        {
            string? target;
            try { target = new FileInfo(fd).LinkTarget; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (target != null && nodePaths.Contains(target))
                found.Add(Path.Combine(pidDir, "fdinfo", Path.GetFileName(fd)));
        }
        return found;
    }

    private static Dictionary<string, string>? ReadFdinfo(string path)
    {
        string text;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var buffer = new char[MaxFdinfo];
            var count = reader.ReadBlock(buffer, 0, buffer.Length);
            text = new string(buffer, 0, count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            keys.TryAdd(key, value);
        }
        return keys;
    }
}
