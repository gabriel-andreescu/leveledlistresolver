using System.Collections.Generic;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.WPF.Reflection.Attributes;

namespace leveledlistresolver
{
    public record ProgramSettings
    {
        [SettingName("Plugin Blacklist")]
        [Tooltip(
            "List of plugin filenames to exclude (e.g., MyMod.esp). Plugins dependent on blacklisted plugins will also be excluded."
        )]
        public List<string> BlacklistedPluginNames { get; set; } = new();

        [SettingName("Remove Empty Sublists")]
        [Tooltip(
            "Remove empty sublists from leveled lists. Some mods may add these intentionally."
        )]
        public bool RemoveEmptySublists { get; set; } = false;

        [SettingName("Verbose Logging")]
        [Tooltip(
            "Show detailed information for each record processed. Disable for cleaner logs with only summary statistics."
        )]
        public bool VerboseLogging { get; set; } = true;

        public HashSet<ModKey> GetBlacklistedPlugins()
        {
            var plugins = new HashSet<ModKey>();
            foreach (var pluginName in BlacklistedPluginNames)
            {
                if (
                    !string.IsNullOrWhiteSpace(pluginName)
                    && ModKey.TryFromNameAndExtension(pluginName, out var modKey)
                )
                {
                    plugins.Add(modKey);
                }
            }
            return plugins;
        }
    }
}
