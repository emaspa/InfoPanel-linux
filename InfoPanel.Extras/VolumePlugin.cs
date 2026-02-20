using InfoPanel.Plugins;
using System.ComponentModel;

namespace InfoPanel.Extras
{
    public class VolumePlugin : BasePlugin
    {
        private readonly List<PluginContainer> _containers = [];

        public VolumePlugin() : base("volume-plugin","Volume Info", "Retrieves audio output devices and relevant details. Linux support via PulseAudio/PipeWire planned.")
        {
        }
        public override string? ConfigFilePath => null;
        public override TimeSpan UpdateInterval => TimeSpan.FromMilliseconds(50);

        public override void Initialize()
        {
            // TODO: Implement Linux audio volume via PulseAudio/PipeWire (pactl)
            PluginContainer container = new("Default");
            container.Entries.Add(new PluginSensor("volume", "Volume", 0, "%"));
            container.Entries.Add(new PluginText("mute", "Mute", "N/A"));
            _containers.Add(container);
        }

        public override void Close()
        {
        }

        public override void Load(List<IPluginContainer> containers)
        {
            containers.AddRange(_containers);
        }

        public override Task UpdateAsync(CancellationToken cancellationToken)
        {
            Update();
            return Task.CompletedTask;
        }

        public override void Update()
        {
            // TODO: Implement Linux audio volume monitoring
        }
    }
}
