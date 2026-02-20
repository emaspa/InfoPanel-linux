using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace InfoPanel.ViewModels
{
    public partial class LogsPageViewModel : ObservableObject
    {
        private static readonly string LogDirectory =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "logs");

        [ObservableProperty]
        private string _logText = string.Empty;

        [ObservableProperty]
        private bool _hasLogs;

        public LogsPageViewModel()
        {
            LoadLogs();
        }

        [RelayCommand]
        private void LoadLogs()
        {
            try
            {
                if (!Directory.Exists(LogDirectory))
                {
                    LogText = "No log directory found.";
                    HasLogs = false;
                    return;
                }

                var logFile = Directory.EnumerateFiles(LogDirectory, "infopanel-*.log")
                    .OrderByDescending(File.GetLastWriteTime)
                    .FirstOrDefault();

                if (logFile == null)
                {
                    LogText = "No log file found.";
                    HasLogs = false;
                    return;
                }

                var sessionStart = new DateTimeOffset(Process.GetCurrentProcess().StartTime);
                var sb = new StringBuilder();
                bool inSession = false;

                using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length >= 29 && DateTimeOffset.TryParse(line[..29], out var lineTime))
                    {
                        inSession = lineTime >= sessionStart;
                    }

                    if (inSession)
                    {
                        sb.AppendLine(line);
                    }
                }

                LogText = sb.ToString();
                HasLogs = LogText.Length > 0;

                if (!HasLogs)
                {
                    LogText = "No log entries found for this session.";
                }
            }
            catch (Exception ex)
            {
                LogText = $"Failed to read log file: {ex.Message}";
                HasLogs = false;
            }
        }

        [RelayCommand]
        private async System.Threading.Tasks.Task CopyToClipboard()
        {
            if (!string.IsNullOrEmpty(LogText))
            {
                var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime
                    as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                if (mainWindow != null)
                {
                    var clipboard = Avalonia.Controls.TopLevel.GetTopLevel(mainWindow)?.Clipboard;
                    if (clipboard != null)
                    {
                        await clipboard.SetTextAsync(LogText);
                    }
                }
            }
        }
    }
}
