using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AkusEnemySkillTracking;

public sealed record SkillObservationKey(
    ushort TerritoryId,
    uint SourceDataId,
    string SourceName,
    uint ActionId);

public sealed class SkillObservation
{
    public ushort TerritoryId { get; init; }

    public string TerritoryName { get; set; } = string.Empty;

    public bool TerritoryNameResolved { get; set; }

    public ContentMetadataObservation ContentMetadata { get; set; } = new();

    public uint SourceDataId { get; init; }

    public uint SourceBaseId { get; set; }

    public uint BattleNpcNameId { get; set; }

    public byte BattleNpcKind { get; set; }

    public uint ModelId { get; set; }

    public string ObjectKind { get; set; } = string.Empty;

    public string SubKind { get; set; } = string.Empty;

    public string CombatantType { get; set; } = string.Empty;

    public string SourceName { get; init; } = string.Empty;

    public byte Level { get; set; }

    public uint MinHp { get; set; }

    public uint MaxHp { get; set; }

    public uint ActionId { get; init; }

    public string ActionIdHex { get; init; } = string.Empty;

    public string ActionName { get; set; } = string.Empty;

    public uint ActionCategoryId { get; set; }

    public string ActionCategoryName { get; set; } = string.Empty;

    public uint ActionCastType { get; set; }

    public string DamageType { get; set; } = string.Empty;

    public string Element { get; set; } = string.Empty;

    public uint TotalUses { get; set; }

    public ushort LastTargetCount { get; set; }

    public RangeValue Damage { get; set; } = new();

    public Dictionary<uint, StatusApplicationObservation> StatusApplications { get; set; } = [];

    public List<RawActionEffectObservation> RawEffects { get; set; } = [];

    public DateTimeOffset FirstSeenUtc { get; set; }

    public DateTimeOffset LastSeenUtc { get; set; }
}

public sealed class ObservationExport
{
    public int Version { get; init; } = 1;

    public DateTimeOffset ExportedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public List<SkillObservation> Observations { get; init; } = [];

    [JsonPropertyName("Klassen_und_Jobs")]
    public Dictionary<string, JobObservation> KlassenUndJobs { get; init; } = [];

    public List<MusicObservation> Music { get; init; } = [];

    public List<ChatLineObservation> ChatLines { get; init; } = [];
}

public sealed class RangeValue
{
    public uint? Min { get; set; }

    public uint? Max { get; set; }

    public void Add(uint value)
    {
        if (value == 0)
            return;

        Min = Min.HasValue ? Math.Min(Min.Value, value) : value;
        Max = Max.HasValue ? Math.Max(Max.Value, value) : value;
    }
}

public sealed class StatusApplicationObservation
{
    public uint StatusId { get; init; }

    public string StatusIdHex { get; init; } = string.Empty;

    public string StatusName { get; set; } = string.Empty;

    public string MitigationType { get; set; } = "unknown";

    public uint? PhysicalMitigationPercent { get; set; }

    public uint? MagicalMitigationPercent { get; set; }

    public uint Count { get; set; }

    public HashSet<string> TargetRelations { get; set; } = [];
}

public sealed class RawActionEffectObservation
{
    public byte Type { get; init; }

    public byte Param0 { get; init; }

    public byte Param1 { get; init; }

    public byte Param2 { get; init; }

    public byte Param3 { get; init; }

    public byte Param4 { get; init; }

    public ushort Value { get; init; }

    public string DamageType { get; init; } = string.Empty;

    public string Element { get; init; } = string.Empty;
}

public sealed class JobObservation
{
    public uint ClassJobId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Abbreviation { get; init; } = string.Empty;

    public byte HighestSeenLevel { get; set; }

    public uint TotalActions { get; set; }

    public Dictionary<uint, JobSkillObservation> Skills { get; set; } = [];

    public Dictionary<uint, StatusApplicationObservation> StatusApplications { get; set; } = [];
}

public sealed class JobSkillObservation
{
    public uint ActionId { get; init; }

    public string ActionIdHex { get; init; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public uint Count { get; set; }

    public HashSet<string> Sources { get; set; } = [];

    public RangeValue Damage { get; set; } = new();

    public Dictionary<uint, StatusApplicationObservation> StatusApplications { get; set; } = [];
}

public sealed class MusicObservation
{
    public ushort TerritoryId { get; init; }

    public string TerritoryName { get; set; } = string.Empty;

    public bool TerritoryNameResolved { get; set; }

    public ContentMetadataObservation ContentMetadata { get; set; } = new();

    public ushort BgmId { get; init; }

    public string Name { get; set; } = string.Empty;

    public string File { get; set; } = string.Empty;

    public uint Count { get; set; }
}

public sealed class ChatLineObservation
{
    public ushort TerritoryId { get; init; }

    public string TerritoryName { get; set; } = string.Empty;

    public bool TerritoryNameResolved { get; set; }

    public ContentMetadataObservation ContentMetadata { get; set; } = new();

    public uint TypeId { get; init; }

    public uint? LogMessageId { get; init; }

    public string GameData { get; init; } = string.Empty;

    public string TypeName { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public string SourceKind { get; init; } = string.Empty;

    public string TargetKind { get; init; } = string.Empty;

    public string Sender { get; init; } = string.Empty;

    public uint SenderBaseId { get; set; }

    public string SenderObjectKind { get; set; } = string.Empty;

    public string SenderSubKind { get; set; } = string.Empty;

    public string SenderCombatantType { get; set; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public List<string> Parameters { get; init; } = [];

    public DateTimeOffset SeenAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public enum PullTimelineEventKind
{
    EnemyAction,
    EnemyStatus,
    AllyMitigation,
    AllyHealing
}

public sealed class PullTimelineEvent
{
    public DateTimeOffset SeenAtUtc { get; init; }

    public double SecondsFromPullStart { get; set; }

    public ushort TerritoryId { get; init; }

    public string TerritoryName { get; init; } = string.Empty;

    public string SourceKey { get; init; } = string.Empty;

    public string SourceName { get; init; } = string.Empty;

    public string SourceType { get; init; } = string.Empty;

    public uint SourceBaseId { get; init; }

    public uint BattleNpcNameId { get; init; }

    public uint ActionId { get; init; }

    public string ActionIdHex { get; init; } = string.Empty;

    public string ActionName { get; init; } = string.Empty;

    public PullTimelineEventKind Kind { get; init; }

    public uint StatusId { get; init; }

    public string StatusIdHex { get; init; } = string.Empty;

    public string StatusName { get; init; } = string.Empty;

    public string MitigationType { get; set; } = string.Empty;

    public uint HealingAmount { get; init; }

    public List<PartyHpSnapshot> PartyHp { get; init; } = [];

    public List<string> TargetRelations { get; init; } = [];
}

public sealed class PartyHpSnapshot
{
    public string Name { get; init; } = string.Empty;

    public uint CurrentHp { get; init; }

    public uint MaxHp { get; init; }

    public double Percent { get; init; }
}

public sealed class ContentMetadataObservation
{
    public uint ContentFinderConditionId { get; set; }

    public string ContentFinderConditionName { get; set; } = string.Empty;

    public uint PlaceNameId { get; set; }

    public string PlaceName { get; set; } = string.Empty;

    public uint MapId { get; set; }

    public string MapName { get; set; } = string.Empty;

    public List<MapMetadataObservation> Maps { get; set; } = [];
}

public sealed class MapMetadataObservation
{
    public uint Id { get; set; }

    public string Name { get; set; } = string.Empty;
}
