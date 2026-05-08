using System;
using Dalamud.Configuration;

namespace AkusEnemySkillTracking;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    public bool RecordJsonLines { get; set; } = true;

    public int RecentLimit { get; set; } = 200;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
