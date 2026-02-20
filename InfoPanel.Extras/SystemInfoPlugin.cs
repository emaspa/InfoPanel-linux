using InfoPanel.Plugins;
using System;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace InfoPanel.Extras
{
    public class SystemInfoPlugin : BasePlugin
    {
        private readonly PluginText _uptimeFormattedSensor = new("Formatted", "-");
        private readonly PluginText _uptimeDaysSensor = new("Days", "-");
        private readonly PluginText _uptimeHoursSensor = new("Hours", "-");
        private readonly PluginText _uptimeMinutesSensor = new("Minutes", "-");
        private readonly PluginText _uptimeSecondsSensor = new("Seconds", "-");

        private readonly PluginSensor _processCountSensor = new("Process Count", 0);
        private readonly PluginSensor _threadCountSensor = new("Thread Count", 0);
        private readonly PluginSensor _handleCountSensor = new("Handle Count", 0);

        private readonly PluginSensor _cpuUsage = new("CPU Usage", 0, "%");
        private readonly PluginSensor _memoryUsage = new("Memory Usage", 0, " MB");

        private static readonly string _defaultTopFormat = "0:200|1:60|2:70|3:100";
        private readonly PluginTable _topCpuUsage = new("Top CPU Usage", new DataTable(), _defaultTopFormat);
        private readonly PluginTable _topMemoryUsage = new("Top Memory Usage", new DataTable(), _defaultTopFormat);

        public override string? ConfigFilePath => Config.FilePath;
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

        private string[] blacklist = [];

        // For CPU usage calculation from /proc/stat
        private long _prevIdleTime;
        private long _prevTotalTime;

        public SystemInfoPlugin() : base("system-info-plugin", "System Info", "Misc system information and statistics.")
        {
        }

        public override void Initialize()
        {
            Config.Instance.Load();
            if(Config.Instance.TryGetValue(Config.SECTION_SYSTEM_INFO, "Blacklist", out var result))
            {
                blacklist = result.Split(',');
            }

            // Initialize CPU counters from /proc/stat
            ReadCpuTimes(out _prevIdleTime, out _prevTotalTime);
        }

        public override void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("Uptime");
            container.Entries.Add(_uptimeFormattedSensor);
            container.Entries.Add(_uptimeDaysSensor);
            container.Entries.Add(_uptimeHoursSensor);
            container.Entries.Add(_uptimeMinutesSensor);
            container.Entries.Add(_uptimeSecondsSensor);

            containers.Add(container);

            container = new PluginContainer("Processes");
            container.Entries.Add(_processCountSensor);
            container.Entries.Add(_threadCountSensor);
            container.Entries.Add(_handleCountSensor);
            container.Entries.Add(_cpuUsage);
            container.Entries.Add(_memoryUsage);
            container.Entries.Add(_topCpuUsage);
            container.Entries.Add(_topMemoryUsage);

            containers.Add(container);
        }

        public override void Close()
        {
        }

        public override void Update()
        {
            throw new NotImplementedException();
        }

        public override Task UpdateAsync(CancellationToken cancellationToken)
        {
            GetUptime();
            GetProcessInfo();

            return Task.CompletedTask;
        }

        private void GetUptime()
        {
            long uptimeMilliseconds = Environment.TickCount64;
            TimeSpan uptime = TimeSpan.FromMilliseconds(uptimeMilliseconds);

            _uptimeFormattedSensor.Value = $"{uptime.Days}:{uptime.Hours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}";
            _uptimeDaysSensor.Value = $"{uptime.Days:D2}";
            _uptimeHoursSensor.Value = $"{uptime.Hours:D2}";
            _uptimeMinutesSensor.Value = $"{uptime.Minutes:D2}";
            _uptimeSecondsSensor.Value = $"{uptime.Seconds:D2}";
        }

        private static void ReadCpuTimes(out long idleTime, out long totalTime)
        {
            idleTime = 0;
            totalTime = 0;

            try
            {
                var line = File.ReadLines("/proc/stat").FirstOrDefault(l => l.StartsWith("cpu "));
                if (line != null)
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    // cpu user nice system idle iowait irq softirq steal
                    if (parts.Length >= 5)
                    {
                        for (int i = 1; i < parts.Length; i++)
                        {
                            if (long.TryParse(parts[i], out var val))
                                totalTime += val;
                        }
                        if (long.TryParse(parts[4], out var idle))
                            idleTime = idle;
                    }
                }
            }
            catch { }
        }

        private static long GetMemoryUsageMB()
        {
            try
            {
                long memTotal = 0, memAvailable = 0;
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:"))
                        memTotal = ParseMemInfoLine(line);
                    else if (line.StartsWith("MemAvailable:"))
                        memAvailable = ParseMemInfoLine(line);

                    if (memTotal > 0 && memAvailable > 0)
                        break;
                }
                return (memTotal - memAvailable) / 1024; // kB to MB
            }
            catch { return 0; }
        }

        private static long ParseMemInfoLine(string line)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && long.TryParse(parts[1], out var val))
                return val;
            return 0;
        }

        private void GetProcessInfo()
        {
            var processes = Process.GetProcesses();
            _processCountSensor.Value = processes.Length;

            int totalThreadCount = 0;

            foreach (var process in processes)
            {
                try
                {
                    totalThreadCount += process.Threads.Count;
                }
                catch { }
            }

            _threadCountSensor.Value = totalThreadCount;
            _handleCountSensor.Value = 0; // Handle count not meaningful on Linux

            // CPU usage from /proc/stat
            ReadCpuTimes(out var currentIdle, out var currentTotal);
            var idleDelta = currentIdle - _prevIdleTime;
            var totalDelta = currentTotal - _prevTotalTime;

            if (totalDelta > 0)
            {
                _cpuUsage.Value = (float)((1.0 - (double)idleDelta / totalDelta) * 100.0);
            }

            _prevIdleTime = currentIdle;
            _prevTotalTime = currentTotal;

            // Memory usage from /proc/meminfo
            _memoryUsage.Value = GetMemoryUsageMB();

            // Process-level stats
            var processGroups = processes.GroupBy(p => p.ProcessName);
            var instances = new List<Instance>();

            foreach (var group in processGroups)
            {
                long memoryBytes = 0;
                foreach (var p in group)
                {
                    try { memoryBytes += p.WorkingSet64; } catch { }
                }
                instances.Add(new Instance { Name = group.Key, PrivateMemory = memoryBytes });
            }

            // Memory top
            instances.Sort((a, b) => b.PrivateMemory.CompareTo(a.PrivateMemory));
            _topMemoryUsage.Value = BuildDataTable(instances, blacklist);

            // CPU top (based on memory for now - per-process CPU requires sampling /proc/[pid]/stat)
            _topCpuUsage.Value = BuildDataTable(instances, blacklist);
        }

        private static DataTable BuildDataTable(List<Instance> instances, string[] blacklist)
        {
            var dataTable = new DataTable();

            dataTable.Columns.Add("Process Name", typeof(PluginText));
            dataTable.Columns.Add("Usage", typeof(PluginSensor));
            dataTable.Columns.Add("Utility", typeof(PluginSensor));
            dataTable.Columns.Add("Memory", typeof(PluginSensor));

            foreach(var instance in instances)
            {
                if (blacklist.Contains(instance.Name))
                {
                    continue;
                }

                var row = dataTable.NewRow();
                row[0] = new PluginText("Process Name", instance.Name);
                row[1] = new PluginSensor("Usage", 0, "%");
                row[2] = new PluginSensor("Utility", 0, "%");
                row[3] = new PluginSensor("Memory", (float)(instance.PrivateMemory) / 1024 / 1024, " MB");
                dataTable.Rows.Add(row);
            }

            return dataTable;
        }

        class Instance
        {
            public required string Name { get; set; }
            public long PrivateMemory { get; set; }
        }
    }
}
