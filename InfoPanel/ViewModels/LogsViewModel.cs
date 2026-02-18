using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace InfoPanel.ViewModels
{
    public partial class LogsViewModel : ObservableObject
    {
        private static readonly string LogDirectory =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "logs");

        private string _logText = string.Empty;

        public string LogText
        {
            get => _logText;
            set => SetProperty(ref _logText, value);
        }

        [RelayCommand]
        private void LoadLogs()
        {
            try
            {
                var logFile = Directory.EnumerateFiles(LogDirectory, "infopanel-*.log")
                    .OrderByDescending(File.GetLastWriteTime)
                    .FirstOrDefault();

                if (logFile == null)
                {
                    LogText = "No log file found.";
                    return;
                }

                using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                LogText = reader.ReadToEnd();
            }
            catch (Exception ex)
            {
                LogText = $"Failed to read log file: {ex.Message}";
            }
        }

        [RelayCommand]
        private void CopyToClipboard()
        {
            if (!string.IsNullOrEmpty(LogText))
            {
                Clipboard.SetText(LogText);
            }
        }
    }
}
