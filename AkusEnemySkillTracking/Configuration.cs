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

    public bool StoreLocalFiles { get; set; } = true;

    public bool StoreLocalFilesWhenRemoteUploadEnabled { get; set; } = true;

    public bool AutoSaveOnTerritoryChange { get; set; } = true;

    public bool AutoSaveOnLocalPlayerKo { get; set; } = true;

    public int AutoSaveIntervalMinutes { get; set; } = 5;

    public bool RemoteUploadEnabled { get; set; }

    public string RemoteEndpointUrl { get; set; } = string.Empty;

    public string RemoteEndpointToken { get; set; } = string.Empty;

    public int RecentLimit { get; set; } = 200;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
