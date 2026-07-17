using Serilog.Core;
using Serilog.Events;
using System.Collections.ObjectModel;

namespace InfoPanel.Utils
{
    /// <summary>
    /// Bounded in-memory log feed for the UI's logs drawer. Serilog writes from any
    /// thread; lines are marshaled to the UI thread and capped at the last 500.
    /// </summary>
    public sealed class UiLogSink : ILogEventSink
    {
        public static UiLogSink Instance { get; } = new();

        public ObservableCollection<string> Lines { get; } = [];

        private const int MaxLines = 500;
        private Action<Action> _post = static action => action();

        private UiLogSink() { }

        /// <summary>Call once the UI dispatcher exists; entries before that are appended inline.</summary>
        public void AttachDispatcher(Action<Action> post) => _post = post;

        public void Emit(LogEvent logEvent)
        {
            var line = $"{logEvent.Timestamp:HH:mm:ss} {LevelLabel(logEvent.Level)} {logEvent.RenderMessage()}";
            if (logEvent.Exception != null)
            {
                line += $" - {logEvent.Exception.GetType().Name}: {logEvent.Exception.Message}";
            }

            _post(() =>
            {
                Lines.Add(line);
                while (Lines.Count > MaxLines)
                {
                    Lines.RemoveAt(0);
                }
            });
        }

        private static string LevelLabel(LogEventLevel level) => level switch
        {
            LogEventLevel.Verbose => "VRB",
            LogEventLevel.Debug => "DBG",
            LogEventLevel.Information => "INF",
            LogEventLevel.Warning => "WRN",
            LogEventLevel.Error => "ERR",
            LogEventLevel.Fatal => "FTL",
            _ => "???"
        };
    }
}
