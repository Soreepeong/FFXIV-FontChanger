using Dalamud.Configuration;
using Dalamud.Plugin;
using System;

namespace FontChangerForDalamud {
    [Serializable]
    public class Configuration : IPluginConfiguration {
        public int Version { get; set; } = 0;

        public bool ConfigVisible = true;

        // the below exist just to make saving less cumbersome

        [NonSerialized]
        private IDalamudPluginInterface? _pluginInterface;

        public void Initialize(IDalamudPluginInterface pluginInterface) {
            _pluginInterface = pluginInterface;
        }

        public void Save() {
            _pluginInterface!.SavePluginConfig(this);
        }
    }
}
