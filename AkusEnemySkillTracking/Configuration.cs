using System;
using Dalamud.Configuration;

namespace AkusEnemySkillTracking;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    public bool RecordJsonLines { get; set; }

    public int JsonLinesMaxMegabytes { get; set; } = 25;

    public bool AutoSaveOnTerritoryChange { get; set; } = true;

    public bool AutoSaveOnLocalPlayerKo { get; set; } = true;

    public int AutoSaveIntervalMinutes { get; set; } = 5;

    public int RecentLimit { get; set; } = 200;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
