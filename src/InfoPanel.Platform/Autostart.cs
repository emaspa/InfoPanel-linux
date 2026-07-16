namespace InfoPanel.Platform
{
    /// <summary>Login autostart. Linux: XDG autostart desktop file; Windows (later): Task Scheduler.</summary>
    public interface IAutostartService
    {
        bool IsEnabled { get; }

        /// <summary>Enables or disables start-at-login, with an optional startup delay in seconds.</summary>
        void Apply(bool enabled, int delaySeconds);
    }
}
