namespace InfoPanel.Utils
{
    /// <summary>
    /// UI-thread marshaling seam. Core models raise change notifications through this
    /// instead of referencing a UI framework dispatcher. The app wires it at startup
    /// (e.g. to Avalonia's Dispatcher); headless hosts leave the default inline execution.
    /// </summary>
    public static class UiThread
    {
        private static Action<Action> _post = static action => action();

        public static void Configure(Action<Action> post)
        {
            _post = post;
        }

        public static void Post(Action action)
        {
            _post(action);
        }
    }
}
