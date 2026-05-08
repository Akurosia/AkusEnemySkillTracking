using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;
using GameObjectId = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectId;
using DalamudObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace AkusEnemySkillTracking.Services;

public sealed unsafe class SkillRecorder : IDisposable
{
    private const string HostedBgmCsvUrl = "https://raw.githubusercontent.com/ff-meli/OrchestrionPlugin/master/Data/xiv_bgm.csv";
    private static readonly object HostedBgmLock = new();
    private static Dictionary<ushort, string>? hostedBgmNames;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Configuration configuration;
    private readonly Dictionary<SkillObservationKey, SkillObservation> observations = [];
    private readonly Dictionary<uint, JobObservation> jobObservations = [];
    private readonly Dictionary<string, MusicObservation> musicObservations = [];
    private readonly List<ChatLineObservation> chatLines = [];
    private readonly List<SkillObservation> recent = [];
    private readonly string snapshotPath;
    private readonly string logdataPath;
    private readonly string newLogdataPath;
    private readonly string jsonlPath;
    private DateTime lastBgmPollUtc = DateTime.MinValue;
    private Hook<ReceiveActionEffectDelegate>? actionEffectHook;
    private Hook<SetBgmDelegate>? setBgmHook;

    private delegate void ReceiveActionEffectDelegate(
        uint sourceId,
        Character* sourceCharacter,
        System.Numerics.Vector3* targetPosition,
        ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId* targetEntityIds);

    private delegate void SetBgmDelegate(
        ushort bgmId,
        uint sceneId,
        byte a3,
        bool enableCustomFade,
        uint fadeOutMs,
        uint fadeInMs,
        uint fadeInStartMs,
        byte a8,
        byte a9,
        float initialVolume);

    public SkillRecorder(Configuration configuration)
    {
        this.configuration = configuration;
        var configDirectory = Plugin.PluginInterface.GetPluginConfigDirectory();
        snapshotPath = Path.Combine(configDirectory, "enemy-skill-observations.json");
        logdataPath = Path.Combine(configDirectory, "akus-logdata-shaped.json");
        newLogdataPath = Path.Combine(configDirectory, "akus-logdata-new-shaped.json");
        jsonlPath = Path.Combine(configDirectory, "enemy-skill-observations.jsonl");

        LoadSnapshot();
        actionEffectHook = Plugin.GameInteropProvider.HookFromAddress<ReceiveActionEffectDelegate>(
            ActionEffectHandler.MemberFunctionPointers.Receive,
            OnReceiveActionEffect);
        actionEffectHook.Enable();

        setBgmHook = Plugin.GameInteropProvider.HookFromAddress<SetBgmDelegate>(
            BGMSystem.MemberFunctionPointers.SetBGM,
            OnSetBgm);
        setBgmHook.Enable();
        Plugin.Framework.Update += OnFrameworkUpdate;
        Plugin.ChatGui.ChatMessageUnhandled += OnChatMessage;
        Plugin.ChatGui.LogMessage += OnLogMessage;
    }

    public IReadOnlyCollection<SkillObservation> Observations => observations.Values;

    public IReadOnlyDictionary<uint, JobObservation> JobObservations => jobObservations;

    public IReadOnlyCollection<MusicObservation> MusicObservations => musicObservations.Values;

    public IReadOnlyList<ChatLineObservation> ChatLines => chatLines;

    public IReadOnlyList<SkillObservation> Recent => recent;

    public string SnapshotPath => snapshotPath;

    public string LogdataPath => logdataPath;

    public string NewLogdataPath => newLogdataPath;

    public string JsonLinesPath => jsonlPath;

    public void SaveSnapshot()
    {
        RepairTerritoryNames();
        foreach (var observation in observations.Values)
            NormalizeObservationClassification(observation);

        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
        var export = new ObservationExport
        {
            Observations = observations.Values
                .OrderBy(o => o.TerritoryName, StringComparer.CurrentCulture)
                .ThenBy(o => o.SourceName, StringComparer.CurrentCulture)
                .ThenBy(o => o.ActionId)
                .ToList(),
            KlassenUndJobs = jobObservations.Values
                .OrderBy(j => j.Name, StringComparer.CurrentCulture)
                .ToDictionary(j => j.Name, j => j, StringComparer.CurrentCulture),
            Music = musicObservations.Values
                .OrderBy(m => m.TerritoryName, StringComparer.CurrentCulture)
                .ThenBy(m => m.BgmId)
                .ToList(),
            ChatLines = chatLines
                .OrderByDescending(c => c.SeenAtUtc)
                .Take(1000)
                .Reverse()
                .ToList()
        };

        File.WriteAllText(snapshotPath, JsonSerializer.Serialize(export, JsonOptions), Encoding.UTF8);
        File.WriteAllText(logdataPath, BuildLogdataJson().ToJsonString(JsonOptions), Encoding.UTF8);
        File.WriteAllText(newLogdataPath, BuildNewLogdataJson().ToJsonString(JsonOptions), Encoding.UTF8);
        Plugin.Log.Information("Saved {Count} enemy observations to {Path}, {LogdataPath}, and {NewLogdataPath}", observations.Count, snapshotPath, logdataPath, newLogdataPath);
    }

    private JsonObject BuildLogdataJson()
    {
        var root = new JsonObject();

        foreach (var observation in observations.Values
                     .Where(o => o.TerritoryNameResolved && !IsUnresolvedRsvName(o.TerritoryName))
                     .OrderBy(o => o.TerritoryName, StringComparer.CurrentCulture)
                     .ThenBy(o => o.SourceName, StringComparer.CurrentCulture)
                     .ThenBy(o => o.ActionId))
        {
            var content = GetOrCreateObject(root, observation.TerritoryName);
            var enemy = GetOrCreateObject(content, observation.SourceName);
            AddUniqueString(GetOrCreateArray(enemy, "id"), observation.SourceDataId.ToString());

            SetIfPositive(enemy, "base_id", observation.SourceBaseId);
            SetIfPositive(enemy, "bnpc_id", observation.BattleNpcNameId);
            SetIfPositive(enemy, "model_id", observation.ModelId);
            SetIfPositive(enemy, "level", observation.Level);
            SetIfPositive(enemy, "minHP", observation.MinHp);
            SetIfPositive(enemy, "maxHP", observation.MaxHp);

            var skill = GetOrCreateObject(GetOrCreateObject(enemy, "skill"), observation.ActionIdHex);
            skill["name"] = observation.ActionName;
            skill["type_id"] = observation.ActionCategoryId.ToString();
            skill["category"] = observation.ActionCategoryName;
            SetDamageClassification(skill, observation.DamageType, observation.Element);
            skill["uses"] = observation.TotalUses;
            AddRange(skill, observation.Damage);

            if (observation.StatusApplications.Count > 0)
            {
                var addStatus = GetOrCreateArray(skill, "add_status");
                var mitigation = GetOrCreateObject(skill, "status_mitigation");
                var enemyStatuses = GetOrCreateObject(enemy, "status");

                foreach (var status in observation.StatusApplications.Values.OrderBy(s => s.StatusId))
                {
                    AddUniqueString(addStatus, status.StatusIdHex);
                    var statusObject = GetOrCreateObject(enemyStatuses, status.StatusIdHex);
                    statusObject["name"] = status.StatusName;
                    statusObject["count"] = status.Count;
                    statusObject["targets"] = JsonSerializer.SerializeToNode(status.TargetRelations.Order(StringComparer.CurrentCulture).ToArray());
                    SetMitigationValue(mitigation, status.StatusIdHex, status);
                }
            }
        }

        AddJobs(root);
        AddMusic(root);
        AddChatLines(root);
        return root;
    }

    private JsonObject BuildNewLogdataJson()
    {
        var root = new JsonObject();

        foreach (var observation in observations.Values
                     .Where(o => o.TerritoryNameResolved && !IsUnresolvedRsvName(o.TerritoryName))
                     .OrderBy(o => o.TerritoryName, StringComparer.CurrentCulture)
                     .ThenBy(o => o.SourceBaseId)
                     .ThenBy(o => o.ActionId))
        {
            var content = GetNewContent(root, observation.TerritoryName, observation.ContentMetadata);
            var combatantKey = (observation.SourceBaseId > 0 ? observation.SourceBaseId : observation.SourceDataId).ToString();
            var combatant = GetOrCreateObject(GetOrCreateObject(content, "combatants"), combatantKey);
            var metadata = GetOrCreateObject(combatant, "metadata");
            metadata["name"] = observation.SourceName;
            SetIfPositive(metadata, "base_id", observation.SourceBaseId);
            SetIfPositive(metadata, "bnpc_id", observation.BattleNpcNameId);
            SetIfPositive(metadata, "model_id", observation.ModelId);
            SetIfPositive(metadata, "level", observation.Level);
            SetIfPositive(metadata, "minHP", observation.MinHp);
            SetIfPositive(metadata, "maxHP", observation.MaxHp);
            SetCombatantTypeMetadata(metadata, observation.ObjectKind, observation.SubKind, observation.CombatantType);

            var skill = GetOrCreateObject(GetOrCreateObject(combatant, "skills"), observation.ActionIdHex);
            skill["name"] = observation.ActionName;
            skill["type_id"] = observation.ActionCategoryId.ToString();
            skill["category"] = observation.ActionCategoryName;
            SetDamageClassification(skill, observation.DamageType, observation.Element);
            AddRange(skill, observation.Damage);

            if (observation.StatusApplications.Count > 0)
            {
                var addStatus = GetOrCreateArray(skill, "add_status");
                var mitigation = GetOrCreateObject(skill, "status_mitigation");
                var combatantStatuses = GetOrCreateObject(combatant, "status");

                foreach (var status in observation.StatusApplications.Values.OrderBy(s => s.StatusId))
                {
                    AddUniqueString(addStatus, status.StatusIdHex);
                    var statusObject = GetOrCreateObject(combatantStatuses, status.StatusIdHex);
                    statusObject["name"] = status.StatusName;
                    statusObject["count"] = status.Count;
                    statusObject["targets"] = JsonSerializer.SerializeToNode(status.TargetRelations.Order(StringComparer.CurrentCulture).ToArray());
                    SetMitigationValue(mitigation, status.StatusIdHex, status);
                }
            }
        }

        AddNewMusic(root);
        AddNewChatLines(root);
        AddNewJobs(root);
        return root;
    }

    private static JsonObject GetNewContent(JsonObject root, string territoryName, ContentMetadataObservation? contentMetadata = null)
    {
        var content = GetOrCreateObject(root, territoryName);
        PopulateContentMetadata(GetOrCreateObject(content, "metadata"), contentMetadata);
        _ = GetOrCreateObject(content, "music");
        _ = GetOrCreateObject(content, "combatants");
        return content;
    }

    private static void PopulateContentMetadata(JsonObject metadata, ContentMetadataObservation? cached)
    {
        cached ??= GetContentMetadata((ushort)Plugin.ClientState.TerritoryType);

        if (cached.ContentFinderConditionId != 0)
        {
            var contentFinder = GetOrCreateObject(metadata, "contentfindercondition");
            contentFinder["id"] = cached.ContentFinderConditionId.ToString();
            contentFinder["name"] = cached.ContentFinderConditionName;
        }

        if (cached.PlaceNameId != 0)
        {
            var placeName = GetOrCreateObject(metadata, "placename");
            placeName["id"] = cached.PlaceNameId.ToString();
            placeName["name"] = cached.PlaceName;
        }

        if (cached.MapId != 0)
        {
            var maps = GetOrCreateArray(metadata, "maps");
            AddMapIfMissing(maps, cached.MapId.ToString(), cached.MapName);
        }
    }

    private static void AddMapIfMissing(JsonArray maps, string id, string name)
    {
        foreach (var node in maps)
        {
            if (node is JsonObject obj && obj["id"]?.GetValue<string>() == id)
                return;
        }

        maps.Add(new JsonObject
        {
            ["id"] = id,
            ["name"] = name
        });
    }

    private void AddNewMusic(JsonObject root)
    {
        foreach (var item in musicObservations.Values
                     .Where(m => m.TerritoryNameResolved && !IsUnresolvedRsvName(m.TerritoryName))
                     .OrderBy(m => m.TerritoryName, StringComparer.CurrentCulture)
                     .ThenBy(m => m.BgmId))
        {
            var music = GetOrCreateObject(GetNewContent(root, item.TerritoryName, item.ContentMetadata), "music");
            var bgm = GetOrCreateObject(music, item.BgmId.ToString("X"));
            bgm["id"] = item.BgmId.ToString();
            bgm["name"] = item.Name;
            bgm["file"] = item.File;
        }
    }

    private void AddNewChatLines(JsonObject root)
    {
        foreach (var line in chatLines
                     .Where(c => c.TerritoryNameResolved && !IsUnresolvedRsvName(c.TerritoryName))
                     .OrderBy(c => c.TerritoryName, StringComparer.CurrentCulture)
                     .ThenBy(c => c.SeenAtUtc))
        {
            var content = GetNewContent(root, line.TerritoryName, line.ContentMetadata);
            var combatants = GetOrCreateObject(content, "combatants");
            var combatantKey = line.SenderBaseId > 0 ? line.SenderBaseId.ToString() : "";
            var combatant = GetOrCreateObject(combatants, combatantKey);
            if (!string.IsNullOrWhiteSpace(line.Sender))
            {
                var metadata = GetOrCreateObject(combatant, "metadata");
                metadata["name"] = line.Sender;
                SetIfPositive(metadata, "base_id", line.SenderBaseId);
                SetCombatantTypeMetadata(metadata, line.SenderObjectKind, line.SenderSubKind, line.SenderCombatantType);
            }

            var text = GetOrCreateObject(combatant, "text");
            var category = GetOrCreateObject(text, line.Category);
            var id = line.LogMessageId?.ToString() ?? line.TypeId.ToString();
            var entry = GetOrCreateObject(category, id);
            entry["text"] = line.Message;
            entry["type_id"] = id;
            entry["type"] = line.TypeName;
            entry["seen_at_utc"] = line.SeenAtUtc;
            if (line.Parameters.Count > 0)
                entry["parameters"] = JsonSerializer.SerializeToNode(line.Parameters);
        }
    }

    private void AddNewJobs(JsonObject root)
    {
        if (jobObservations.Count == 0)
            return;

        var jobs = GetOrCreateObject(root, "Klassen_und_Jobs");
        var metadata = GetOrCreateObject(jobs, "metadata");
        metadata["name"] = "Klassen_und_Jobs";
        var combatants = GetOrCreateObject(jobs, "combatants");

        foreach (var job in jobObservations.Values.OrderBy(j => j.Name, StringComparer.CurrentCulture))
        {
            var jobObject = GetOrCreateObject(combatants, job.ClassJobId.ToString());
            var jobMetadata = GetOrCreateObject(jobObject, "metadata");
        jobMetadata["name"] = job.Name;
        jobMetadata["abbreviation"] = job.Abbreviation;
        jobMetadata["level"] = job.HighestSeenLevel;
        jobMetadata["combatant_type"] = "PlayerJob";

            var skills = GetOrCreateObject(jobObject, "skills");
            foreach (var skillObservation in job.Skills.Values.OrderBy(s => s.ActionId))
            {
                var skill = GetOrCreateObject(skills, skillObservation.ActionIdHex);
                skill["name"] = skillObservation.Name;
                AddRange(skill, skillObservation.Damage);
                if (skillObservation.StatusApplications.Count > 0)
                {
                    var addStatus = GetOrCreateArray(skill, "add_status");
                    var mitigation = GetOrCreateObject(skill, "status_mitigation");
                    foreach (var status in skillObservation.StatusApplications.Values.OrderBy(s => s.StatusId))
                    {
                        AddUniqueString(addStatus, status.StatusIdHex);
                        SetMitigationValue(mitigation, status.StatusIdHex, status);
                    }
                }
            }

            var statuses = GetOrCreateObject(jobObject, "status");
            foreach (var status in job.StatusApplications.Values.OrderBy(s => s.StatusId))
            {
                var statusObject = GetOrCreateObject(statuses, status.StatusIdHex);
                statusObject["name"] = status.StatusName;
                statusObject["count"] = status.Count;
                statusObject["targets"] = JsonSerializer.SerializeToNode(status.TargetRelations.Order(StringComparer.CurrentCulture).ToArray());
                SetMitigationType(statusObject, status);
            }
        }
    }

    private void AddJobs(JsonObject root)
    {
        var jobs = GetOrCreateObject(root, "Klassen_und_Jobs");
        foreach (var job in jobObservations.Values.OrderBy(j => j.Name, StringComparer.CurrentCulture))
        {
            var jobObject = GetOrCreateObject(jobs, job.Name);
            jobObject["id"] = job.ClassJobId.ToString();
            jobObject["abbreviation"] = job.Abbreviation;
            jobObject["max_level_seen"] = job.HighestSeenLevel;
            jobObject["uses"] = job.TotalActions;

            var skills = GetOrCreateObject(jobObject, "skill");
            foreach (var skillObservation in job.Skills.Values.OrderBy(s => s.ActionId))
            {
                var skill = GetOrCreateObject(skills, skillObservation.ActionIdHex);
                skill["name"] = skillObservation.Name;
                skill["uses"] = skillObservation.Count;
                AddRange(skill, skillObservation.Damage);

                if (skillObservation.StatusApplications.Count > 0)
                {
                    var addStatus = GetOrCreateArray(skill, "add_status");
                    var mitigation = GetOrCreateObject(skill, "status_mitigation");
                    foreach (var status in skillObservation.StatusApplications.Values.OrderBy(s => s.StatusId))
                    {
                        AddUniqueString(addStatus, status.StatusIdHex);
                        SetMitigationValue(mitigation, status.StatusIdHex, status);
                    }
                }
            }

            var statuses = GetOrCreateObject(jobObject, "status");
            foreach (var status in job.StatusApplications.Values.OrderBy(s => s.StatusId))
            {
                var statusObject = GetOrCreateObject(statuses, status.StatusIdHex);
                statusObject["name"] = status.StatusName;
                statusObject["count"] = status.Count;
                statusObject["targets"] = JsonSerializer.SerializeToNode(status.TargetRelations.Order(StringComparer.CurrentCulture).ToArray());
                SetMitigationType(statusObject, status);
            }
        }
    }

    private void AddMusic(JsonObject root)
    {
        var music = GetOrCreateObject(root, "Musik");
        foreach (var item in musicObservations.Values
                     .Where(m => m.TerritoryNameResolved && !IsUnresolvedRsvName(m.TerritoryName))
                     .OrderBy(m => m.TerritoryName, StringComparer.CurrentCulture)
                     .ThenBy(m => m.BgmId))
        {
            var territory = GetOrCreateObject(music, item.TerritoryName);
            var bgm = GetOrCreateObject(territory, item.BgmId.ToString("X"));
            bgm["id"] = item.BgmId.ToString();
            bgm["name"] = item.Name;
            bgm["file"] = item.File;
            bgm["count"] = item.Count;
        }
    }

    private void AddChatLines(JsonObject root)
    {
        foreach (var line in chatLines
                     .Where(c => c.TerritoryNameResolved && !IsUnresolvedRsvName(c.TerritoryName))
                     .OrderBy(c => c.TerritoryName, StringComparer.CurrentCulture)
                     .ThenBy(c => c.SeenAtUtc))
        {
            var content = GetOrCreateObject(root, line.TerritoryName);
            var text = GetOrCreateObject(GetOrCreateObject(content, line.Sender.Length == 0 ? "" : line.Sender), "text");
            var bucketName = line.Category.Equals("NPCYell", StringComparison.OrdinalIgnoreCase)
                ? "npcyell_ids"
                : line.Category.Equals("InstanceContentTextData", StringComparison.OrdinalIgnoreCase)
                    ? "instancecontenttextdata_ids"
                    : $"{line.Category.ToLowerInvariant()}_ids";
            var bucket = GetOrCreateObject(text, bucketName);
            var id = line.LogMessageId?.ToString() ?? line.TypeId.ToString();
            var entry = GetOrCreateObject(bucket, id);
            entry["sender"] = line.Sender;
            entry["text"] = line.Message;
            entry["type_id"] = line.TypeId.ToString();
            entry["type"] = line.TypeName;
            entry["category"] = line.Category;
            entry["seen_at_utc"] = line.SeenAtUtc;
            if (line.Parameters.Count > 0)
                entry["parameters"] = JsonSerializer.SerializeToNode(line.Parameters);
        }
    }

    private static JsonObject GetOrCreateObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing)
            return existing;

        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    private static JsonArray GetOrCreateArray(JsonObject parent, string key)
    {
        if (parent[key] is JsonArray existing)
            return existing;

        var created = new JsonArray();
        parent[key] = created;
        return created;
    }

    private static void AddUniqueString(JsonArray array, string value)
    {
        if (!array.Any(node => node?.GetValue<string>() == value))
            array.Add(value);
    }

    private static void SetIfPositive(JsonObject obj, string key, uint value)
    {
        if (value > 0)
            obj[key] = value;
    }

    private static void AddRange(JsonObject obj, RangeValue range)
    {
        if (!range.Min.HasValue && !range.Max.HasValue)
            return;

        var damage = GetOrCreateObject(obj, "damage");
        if (range.Min.HasValue)
            damage["min"] = range.Min.Value;
        if (range.Max.HasValue)
            damage["max"] = range.Max.Value;
    }

    private static void SetDamageClassification(JsonObject skill, string damageType, string element)
    {
        var normalizedDamageType = NormalizeDamageType(damageType);
        if (IsKnownDamageType(normalizedDamageType))
            skill["damage_type"] = normalizedDamageType;

        var normalizedElement = NormalizeElement(element);
        if (IsKnownElement(normalizedElement))
            skill["element"] = normalizedElement;
    }

    private static void NormalizeObservationClassification(SkillObservation observation)
    {
        observation.DamageType = NormalizeDamageType(observation.DamageType);
        observation.Element = NormalizeElement(observation.Element);
    }

    private static void SetMitigationValue(JsonObject parent, string key, StatusApplicationObservation status)
    {
        var value = CreateMitigationValue(status);
        if (value != null)
            parent[key] = value;
    }

    private static void SetMitigationType(JsonObject statusObject, StatusApplicationObservation status)
    {
        var value = CreateMitigationValue(status);
        if (value != null)
            statusObject["mitigation_type"] = value;
    }

    private static JsonNode? CreateMitigationValue(StatusApplicationObservation status)
    {
        if (status.PhysicalMitigationPercent.HasValue || status.MagicalMitigationPercent.HasValue)
        {
            var value = new JsonObject();
            if (status.PhysicalMitigationPercent.HasValue)
                value["physical"] = $"{status.PhysicalMitigationPercent.Value}%";
            if (status.MagicalMitigationPercent.HasValue)
                value["magical"] = $"{status.MagicalMitigationPercent.Value}%";
            return value;
        }

        if (status.MitigationType == "unknown")
            return null;

        return JsonValue.Create(status.MitigationType);
    }

    private static void SetCombatantTypeMetadata(JsonObject metadata, string objectKind, string subKind, string combatantType)
    {
        if (!string.IsNullOrWhiteSpace(objectKind))
            metadata["object_kind"] = objectKind;
        if (!string.IsNullOrWhiteSpace(subKind))
            metadata["sub_kind"] = subKind;
        if (!string.IsNullOrWhiteSpace(combatantType))
            metadata["combatant_type"] = combatantType;
    }

    public void Clear()
    {
        observations.Clear();
        jobObservations.Clear();
        musicObservations.Clear();
        chatLines.Clear();
        recent.Clear();
        SaveSnapshot();
    }

    public void Dispose()
    {
        SaveSnapshot();
        Plugin.ChatGui.ChatMessageUnhandled -= OnChatMessage;
        Plugin.ChatGui.LogMessage -= OnLogMessage;
        Plugin.Framework.Update -= OnFrameworkUpdate;
        actionEffectHook?.Disable();
        actionEffectHook?.Dispose();
        actionEffectHook = null;
        setBgmHook?.Disable();
        setBgmHook?.Dispose();
        setBgmHook = null;
    }

    private void LoadSnapshot()
    {
        if (!File.Exists(snapshotPath))
            return;

        try
        {
        var export = JsonSerializer.Deserialize<ObservationExport>(File.ReadAllText(snapshotPath, Encoding.UTF8), JsonOptions);
            if (export?.Observations == null)
                return;

            foreach (var observation in export.Observations)
            {
                NormalizeObservationClassification(observation);
                var key = new SkillObservationKey(
                    observation.TerritoryId,
                    observation.SourceDataId,
                    observation.SourceName,
                    observation.ActionId);
                if (observations.TryGetValue(key, out var existing))
                {
                    existing.TotalUses += observation.TotalUses;
                    existing.FirstSeenUtc = existing.FirstSeenUtc <= observation.FirstSeenUtc ? existing.FirstSeenUtc : observation.FirstSeenUtc;
                    existing.LastSeenUtc = existing.LastSeenUtc >= observation.LastSeenUtc ? existing.LastSeenUtc : observation.LastSeenUtc;
                    existing.LastTargetCount = observation.LastTargetCount;
                    if (!existing.TerritoryNameResolved && observation.TerritoryNameResolved)
                    {
                        existing.TerritoryName = observation.TerritoryName;
                        existing.TerritoryNameResolved = true;
                    }

                    continue;
                }

                observation.TerritoryNameResolved = !IsUnresolvedRsvName(observation.TerritoryName);
                observations[key] = observation;
            }

            if (export.KlassenUndJobs != null)
            {
                foreach (var job in export.KlassenUndJobs.Values)
                    jobObservations[job.ClassJobId] = job;
            }

            if (export.Music != null)
            {
                foreach (var music in export.Music)
                    musicObservations[GetMusicKey(music.TerritoryId, music.BgmId)] = music;
            }

            if (export.ChatLines != null)
                chatLines.AddRange(export.ChatLines.TakeLast(1000));
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not load existing enemy skill observations.");
        }
    }

    private void OnReceiveActionEffect(
        uint sourceId,
        Character* sourceCharacter,
        System.Numerics.Vector3* targetPosition,
        ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId* targetEntityIds)
    {
        actionEffectHook?.Original(sourceId, sourceCharacter, targetPosition, header, effects, targetEntityIds);

        try
        {
            Record(sourceId, header, effects, targetEntityIds);
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Failed to record action effect.");
        }
    }

    private void OnSetBgm(
        ushort bgmId,
        uint sceneId,
        byte a3,
        bool enableCustomFade,
        uint fadeOutMs,
        uint fadeInMs,
        uint fadeInStartMs,
        byte a8,
        byte a9,
        float initialVolume)
    {
        setBgmHook?.Original(bgmId, sceneId, a3, enableCustomFade, fadeOutMs, fadeInMs, fadeInStartMs, a8, a9, initialVolume);

        try
        {
            RecordMusic(bgmId, sceneId);
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Failed to record BGM.");
        }
    }

    private void OnChatMessage(IChatMessage message)
    {
        try
        {
            RecordChatLine(message);
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Failed to record chat line.");
        }
    }

    private void OnLogMessage(ILogMessage message)
    {
        try
        {
            RecordLogMessage(message);
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Failed to record log message.");
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if ((DateTime.UtcNow - lastBgmPollUtc).TotalSeconds < 2)
            return;

        lastBgmPollUtc = DateTime.UtcNow;

        try
        {
            PollCurrentBgm();
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Failed to poll current BGM.");
        }
    }

    private void Record(uint sourceId, ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds)
    {
        if (!configuration.Enabled || header == null)
            return;

        if (header->ActionType != (byte)ActionType.Action || header->ActionId == 0)
            return;

        var source = FindSource(sourceId);
        if (source is IPlayerCharacter player)
        {
            RecordJobAction(player, player, header, effects, targetEntityIds);
            return;
        }

        var owner = FindPlayerOwner(source);
        if (source != null && owner != null)
        {
            RecordJobAction(owner, source, header, effects, targetEntityIds);
            return;
        }

        if (source is not IBattleNpc npc)
            return;

        if (source.ObjectKind != DalamudObjectKind.BattleNpc || source.Name.ToString().Length == 0)
            return;

        var territoryId = (ushort)Plugin.ClientState.TerritoryType;
        var contentMetadata = GetContentMetadata(territoryId);
        var resolvedTerritoryName = TryGetResolvedTerritoryName(territoryId);
        RepairTerritoryNames(territoryId, resolvedTerritoryName);
        var territoryName = resolvedTerritoryName ?? GetFallbackTerritoryName(territoryId);
        var sourceName = source.Name.ToString();
        var sourceDataId = source.BaseId;
        var actionId = header->ActionId;
        var now = DateTimeOffset.UtcNow;
        var key = new SkillObservationKey(territoryId, sourceDataId, sourceName, actionId);

        if (!observations.TryGetValue(key, out var observation))
        {
            observation = new SkillObservation
            {
                TerritoryId = territoryId,
                TerritoryName = territoryName,
                SourceDataId = sourceDataId,
                SourceName = sourceName,
                ActionId = actionId,
                ActionIdHex = actionId.ToString("X"),
                ContentMetadata = contentMetadata,
                TerritoryNameResolved = resolvedTerritoryName != null,
                FirstSeenUtc = now
            };
            observations[key] = observation;
        }
        else if (resolvedTerritoryName != null && !observation.TerritoryNameResolved)
        {
            observation.TerritoryName = resolvedTerritoryName;
            observation.TerritoryNameResolved = true;
        }

        EnrichAction(observation, actionId);
        EnrichEnemy(observation, npc, source);
        RecordActionEffects(observation, source, effects, targetEntityIds, header->NumTargets);
        observation.TotalUses++;
        observation.LastTargetCount = header->NumTargets;
        observation.LastSeenUtc = now;

        recent.Insert(0, observation);
        if (recent.Count > Math.Max(25, configuration.RecentLimit))
            recent.RemoveAt(recent.Count - 1);

        if (configuration.RecordJsonLines)
            AppendJsonLine(observation);
    }

    private void RecordJobAction(IPlayerCharacter owner, IGameObject source, ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds)
    {
        var jobId = owner.ClassJob.RowId;
        if (jobId == 0)
            return;

        var job = GetOrCreateJobObservation(jobId);
        job.HighestSeenLevel = Math.Max(job.HighestSeenLevel, owner.Level);
        job.TotalActions++;

        if (!job.Skills.TryGetValue(header->ActionId, out var skill))
        {
            skill = new JobSkillObservation
            {
                ActionId = header->ActionId,
                ActionIdHex = header->ActionId.ToString("X")
            };
            EnrichJobSkill(skill, header->ActionId);
            job.Skills[header->ActionId] = skill;
        }

        skill.Count++;
        var sourceName = source.Name.ToString();
        skill.Sources.Add(string.IsNullOrWhiteSpace(sourceName) ? source.ObjectKind.ToString() : sourceName);

        var targetCount = Math.Min(header->NumTargets, (byte)32);
        for (var targetIndex = 0; targetIndex < targetCount; targetIndex++)
        {
            var target = FindTarget(targetEntityIds[targetIndex]);
            foreach (var effect in effects[targetIndex].Effects)
            {
                if (effect.Type == 0)
                    continue;

                if (IsDamageEffect(effect))
                    skill.Damage.Add(effect.Value);

                var statusId = TryGetStatusId(effect);
                if (statusId == null)
                    continue;

                var status = GetOrCreateStatus(job.StatusApplications, statusId.Value);
                status.Count++;
                status.TargetRelations.Add(GetPlayerStatusTargetRelation(source, target, statusId.Value));
                UpdateMitigationType(status, effect);

                var skillStatus = GetOrCreateStatus(skill.StatusApplications, statusId.Value);
                skillStatus.Count++;
                skillStatus.TargetRelations.Add(GetPlayerStatusTargetRelation(source, target, statusId.Value));
                UpdateMitigationType(skillStatus, effect);
            }
        }
    }

    private void RecordMusic(ushort bgmId, uint? sceneId = null)
    {
        if (!configuration.Enabled || bgmId == 0)
            return;

        var territoryId = (ushort)Plugin.ClientState.TerritoryType;
        var contentMetadata = GetContentMetadata(territoryId);
        var resolvedTerritoryName = TryGetResolvedTerritoryName(territoryId);
        RepairTerritoryNames(territoryId, resolvedTerritoryName);
        var key = GetMusicKey(territoryId, bgmId);

        if (!musicObservations.TryGetValue(key, out var music))
        {
            music = new MusicObservation
            {
                TerritoryId = territoryId,
                TerritoryName = resolvedTerritoryName ?? GetFallbackTerritoryName(territoryId),
                TerritoryNameResolved = resolvedTerritoryName != null,
                ContentMetadata = contentMetadata,
                BgmId = bgmId
            };
            EnrichMusic(music);
            musicObservations[key] = music;
        }
        else if (resolvedTerritoryName != null && !music.TerritoryNameResolved)
        {
            music.TerritoryName = resolvedTerritoryName;
            music.TerritoryNameResolved = true;
        }

        music.Count++;
    }

    private static void EnrichMusic(MusicObservation music)
    {
        if (Plugin.DataManager.GetExcelSheet<BGM>().TryGetRow(music.BgmId, out var bgm))
            music.File = bgm.File.ToString();

        if (TryGetHostedBgmName(music.BgmId, out var hostedName))
        {
            music.Name = hostedName;
            return;
        }

        TryEnrichMusicFromOrchestrion(music);

        foreach (var weddingBgm in Plugin.DataManager.GetExcelSheet<WeddingBGM>())
        {
            if (weddingBgm.Song.RowId != music.BgmId)
                continue;

            music.Name = weddingBgm.SongName.ToString();
            return;
        }

        if (string.IsNullOrWhiteSpace(music.Name))
            music.Name = music.File;
    }

    private static bool TryGetHostedBgmName(ushort bgmId, out string name)
    {
        var names = GetHostedBgmNames();
        if (names.TryGetValue(bgmId, out name!) && !string.IsNullOrWhiteSpace(name))
            return true;

        name = string.Empty;
        return false;
    }

    private static Dictionary<ushort, string> GetHostedBgmNames()
    {
        lock (HostedBgmLock)
        {
            if (hostedBgmNames != null)
                return hostedBgmNames;

            hostedBgmNames = LoadHostedBgmNames();
            return hostedBgmNames;
        }
    }

    private static Dictionary<ushort, string> LoadHostedBgmNames()
    {
        var names = new Dictionary<ushort, string>();
        var cachePath = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "xiv_bgm.csv");

        try
        {
            if (!File.Exists(cachePath) || DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) > TimeSpan.FromDays(7))
            {
                using var client = new System.Net.Http.HttpClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                var csv = client.GetStringAsync(HostedBgmCsvUrl).GetAwaiter().GetResult();
                File.WriteAllText(cachePath, csv, Encoding.UTF8);
            }

            ParseHostedBgmCsv(File.ReadAllLines(cachePath, Encoding.UTF8), names);
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Failed to load hosted Orchestrion BGM mapping.");
            TryLoadCachedHostedBgmNames(cachePath, names);
        }

        return names;
    }

    private static void TryLoadCachedHostedBgmNames(string cachePath, Dictionary<ushort, string> names)
    {
        try
        {
            if (File.Exists(cachePath))
                ParseHostedBgmCsv(File.ReadAllLines(cachePath, Encoding.UTF8), names);
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Failed to load cached Orchestrion BGM mapping.");
        }
    }

    private static void ParseHostedBgmCsv(IEnumerable<string> lines, Dictionary<ushort, string> names)
    {
        foreach (var line in lines)
        {
            var parts = line.Split(';');
            if (parts.Length < 2 || !ushort.TryParse(parts[0], out var id))
                continue;

            var name = parts[1].Trim();
            if (id == 0 || string.IsNullOrWhiteSpace(name) || name.Equals("N/A", StringComparison.OrdinalIgnoreCase))
                continue;

            names[id] = name;
        }
    }

    private static void TryEnrichMusicFromOrchestrion(MusicObservation music)
    {
        if (string.IsNullOrWhiteSpace(music.File))
            return;

        foreach (var path in Plugin.DataManager.GetExcelSheet<OrchestrionPath>())
        {
            if (!IsSameBgmFile(path.File.ToString(), music.File))
                continue;

            if (Plugin.DataManager.GetExcelSheet<Orchestrion>().TryGetRow(path.RowId, out var orchestrion))
            {
                var name = orchestrion.Name.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                    music.Name = name;
                return;
            }
        }
    }

    private static bool IsSameBgmFile(string orchestrionPath, string bgmFile)
    {
        var left = NormalizeBgmFile(orchestrionPath);
        var right = NormalizeBgmFile(bgmFile);

        return left == right
            || left.EndsWith(right, StringComparison.OrdinalIgnoreCase)
            || right.EndsWith(left, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(left).Equals(Path.GetFileName(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeBgmFile(string value)
    {
        var normalized = value
            .Replace('\\', '/')
            .Trim()
            .ToLowerInvariant();

        if (normalized.EndsWith(".scd", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^4];

        return normalized;
    }

    private void RecordChatLine(IChatMessage message)
    {
        if (!configuration.Enabled)
            return;

        var typeName = message.LogKind.ToString();
        if (!IsContentChatType(typeName))
            return;

        var territoryId = (ushort)Plugin.ClientState.TerritoryType;
        var contentMetadata = GetContentMetadata(territoryId);
        var resolvedTerritoryName = TryGetResolvedTerritoryName(territoryId);
        RepairTerritoryNames(territoryId, resolvedTerritoryName);

        var line = new ChatLineObservation
        {
            TerritoryId = territoryId,
            TerritoryName = resolvedTerritoryName ?? GetFallbackTerritoryName(territoryId),
            TerritoryNameResolved = resolvedTerritoryName != null,
            ContentMetadata = contentMetadata,
            TypeId = Convert.ToUInt32(message.LogKind),
            TypeName = typeName,
            Category = GetChatCategory(typeName),
            SourceKind = message.SourceKind.ToString(),
            TargetKind = message.TargetKind.ToString(),
            Sender = message.Sender.ToString(),
            SenderBaseId = FindObjectBaseIdByName(message.Sender.ToString()),
            Message = message.Message.ToString(),
            SeenAtUtc = DateTimeOffset.UtcNow
        };
        EnrichChatSender(line);

        chatLines.Add(line);
        if (chatLines.Count > 1000)
            chatLines.RemoveRange(0, chatLines.Count - 1000);
    }

    private void RecordLogMessage(ILogMessage message)
    {
        if (!configuration.Enabled || !IsContentLogMessage(message))
            return;

        var territoryId = (ushort)Plugin.ClientState.TerritoryType;
        var contentMetadata = GetContentMetadata(territoryId);
        var resolvedTerritoryName = TryGetResolvedTerritoryName(territoryId);
        RepairTerritoryNames(territoryId, resolvedTerritoryName);

        var line = new ChatLineObservation
        {
            TerritoryId = territoryId,
            TerritoryName = resolvedTerritoryName ?? GetFallbackTerritoryName(territoryId),
            TerritoryNameResolved = resolvedTerritoryName != null,
            ContentMetadata = contentMetadata,
            TypeId = 0,
            LogMessageId = message.LogMessageId,
            GameData = message.GameData.ToString() ?? string.Empty,
            TypeName = "LogMessage",
            Category = GetLogMessageCategory(message),
            SourceKind = message.SourceEntity?.ObjStrId.ToString() ?? string.Empty,
            TargetKind = message.TargetEntity?.ObjStrId.ToString() ?? string.Empty,
            Sender = message.SourceEntity?.Name.ToString() ?? string.Empty,
            SenderBaseId = FindObjectBaseIdByName(message.SourceEntity?.Name.ToString() ?? string.Empty),
            Message = message.FormatLogMessageForDebugging().ToString(),
            Parameters = GetLogMessageParameters(message),
            SeenAtUtc = DateTimeOffset.UtcNow
        };
        EnrichChatSender(line);

        chatLines.Add(line);
        if (chatLines.Count > 1000)
            chatLines.RemoveRange(0, chatLines.Count - 1000);
    }

    private void PollCurrentBgm()
    {
        var bgm = BGMSystem.Instance();
        if (bgm == null)
            return;

        for (var i = 0; i < bgm->Scenes.Count; i++)
        {
            var scene = bgm->Scenes[i];
            if (scene.BgmId != 0)
                RecordMusic(scene.BgmId, scene.SceneId);

            if (scene.PlayingBgmId != 0)
                RecordMusic(scene.PlayingBgmId, scene.SceneId);
        }
    }

    private static IGameObject? FindSource(uint sourceId)
    {
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj?.GameObjectId == sourceId)
                return obj;
        }

        return null;
    }

    private static IGameObject? FindTarget(GameObjectId targetId)
    {
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj != null && obj.GameObjectId == targetId)
                return obj;
        }

        return null;
    }

    private static IPlayerCharacter? FindPlayerOwner(IGameObject? source)
    {
        if (source == null || source.OwnerId == 0)
            return null;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is IPlayerCharacter player && player.GameObjectId == source.OwnerId)
                return player;
        }

        return null;
    }

    private static uint FindObjectBaseIdByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return 0;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj != null && obj.Name.ToString().Equals(name, StringComparison.CurrentCultureIgnoreCase))
                return obj.BaseId;
        }

        return 0;
    }

    private static IGameObject? FindObjectByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj != null && obj.Name.ToString().Equals(name, StringComparison.CurrentCultureIgnoreCase))
                return obj;
        }

        return null;
    }

    private static void EnrichChatSender(ChatLineObservation line)
    {
        var sender = FindObjectByName(line.Sender);
        if (sender == null)
            return;

        line.SenderBaseId = sender.BaseId;
        line.SenderObjectKind = sender.ObjectKind.ToString();
        line.SenderSubKind = sender.SubKind.ToString();
        line.SenderCombatantType = ClassifyCombatant(sender);
    }

    private static string ClassifyCombatant(IGameObject obj)
    {
        if (obj is IPlayerCharacter)
            return "Player";

        return obj.ObjectKind switch
        {
            DalamudObjectKind.BattleNpc => "BNPC",
            DalamudObjectKind.EventNpc => "ENPC",
            DalamudObjectKind.Companion => "Companion",
            DalamudObjectKind.Retainer => "Retainer",
            _ => obj.OwnerId != 0 ? "Owned" : "Other"
        };
    }

    private static string GetMusicKey(ushort territoryId, ushort bgmId)
    {
        return $"{territoryId}:{bgmId}";
    }

    private static string? TryGetResolvedTerritoryName(ushort territoryId)
    {
        if (!Plugin.DataManager.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out var territory))
            return null;

        var contentName = territory.ContentFinderCondition.Value.Name.ToString();
        if (!IsUnresolvedRsvName(contentName))
            return contentName;

        var name = territory.PlaceName.Value.Name.ToString();
        return IsUnresolvedRsvName(name) ? null : name;
    }

    private static ContentMetadataObservation GetContentMetadata(ushort territoryId)
    {
        if (!Plugin.DataManager.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out var territory))
            return new ContentMetadataObservation();

        return new ContentMetadataObservation
        {
            ContentFinderConditionId = territory.ContentFinderCondition.RowId,
            ContentFinderConditionName = territory.ContentFinderCondition.Value.Name.ToString(),
            PlaceNameId = territory.PlaceName.RowId,
            PlaceName = territory.PlaceName.Value.Name.ToString(),
            MapId = territory.Map.RowId,
            MapName = territory.Map.Value.PlaceName.Value.Name.ToString()
        };
    }

    private static string GetFallbackTerritoryName(ushort territoryId)
    {
        return $"Territory {territoryId}";
    }

    private void RepairTerritoryNames()
    {
        foreach (var territoryId in observations.Values.Select(o => o.TerritoryId).Distinct().ToArray())
            RepairTerritoryNames(territoryId, TryGetResolvedTerritoryName(territoryId));
    }

    private void RepairTerritoryNames(ushort territoryId, string? resolvedName)
    {
        if (string.IsNullOrWhiteSpace(resolvedName))
            return;

        foreach (var observation in observations.Values.Where(o => o.TerritoryId == territoryId && (!o.TerritoryNameResolved || o.TerritoryName != resolvedName)))
        {
            observation.TerritoryName = resolvedName;
            observation.TerritoryNameResolved = true;
        }

        foreach (var music in musicObservations.Values.Where(o => o.TerritoryId == territoryId && (!o.TerritoryNameResolved || o.TerritoryName != resolvedName)))
        {
            music.TerritoryName = resolvedName;
            music.TerritoryNameResolved = true;
        }

        foreach (var chatLine in chatLines.Where(o => o.TerritoryId == territoryId && (!o.TerritoryNameResolved || o.TerritoryName != resolvedName)))
        {
            chatLine.TerritoryName = resolvedName;
            chatLine.TerritoryNameResolved = true;
        }
    }

    private static bool IsUnresolvedRsvName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var trimmed = value.Trim();
        return trimmed.StartsWith("rsv_", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("_rsv_", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetChatCategory(string typeName)
    {
        return typeName switch
        {
            "NPCDialogueAnnouncements" => "NPCYell",
            "NPCDialogue" => "NPCDialogue",
            "SystemMessage" or "SystemError" or "GatheringSystemMessage" or "ErrorMessage" => "System",
            "Battle" or "Damage" or "Miss" or "Action" or "Item" or "Healing" or "GainBuff" or "GainDebuff" or "LoseBuff" or "LoseDebuff" => "BattleLog",
            _ => typeName
        };
    }

    private static bool IsContentChatType(string typeName)
    {
        return typeName is
            "NPCDialogue"
            or "NPCDialogueAnnouncements"
            or "Notice"
            or "Urgent"
            or "Progress"
            or "Echo";
    }

    private static bool IsContentLogMessage(ILogMessage message)
    {
        var gameData = message.GameData.ToString() ?? string.Empty;
        if (gameData.Contains("InstanceContentTextData", StringComparison.OrdinalIgnoreCase)
            || gameData.Contains("NpcYell", StringComparison.OrdinalIgnoreCase)
            || gameData.Contains("NPCYell", StringComparison.OrdinalIgnoreCase))
            return true;

        var debug = message.FormatLogMessageForDebugging().ToString();
        return debug.Contains("InstanceContentTextData", StringComparison.OrdinalIgnoreCase)
            || debug.Contains("NpcYell", StringComparison.OrdinalIgnoreCase)
            || debug.Contains("NPCYell", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetLogMessageCategory(ILogMessage message)
    {
        var text = $"{message.GameData} {message.FormatLogMessageForDebugging()}";
        if (text.Contains("InstanceContentTextData", StringComparison.OrdinalIgnoreCase))
            return "InstanceContentTextData";

        if (text.Contains("NpcYell", StringComparison.OrdinalIgnoreCase)
            || text.Contains("NPCYell", StringComparison.OrdinalIgnoreCase))
            return "NPCYell";

        return "LogMessage";
    }

    private static List<string> GetLogMessageParameters(ILogMessage message)
    {
        var parameters = new List<string>();
        for (var i = 0; i < message.ParameterCount; i++)
        {
            if (message.TryGetIntParameter(i, out var intValue))
            {
                parameters.Add($"{i}:int:{intValue}");
                continue;
            }

            if (message.TryGetStringParameter(i, out var stringValue))
                parameters.Add($"{i}:string:{stringValue}");
        }

        return parameters;
    }

    private static void EnrichAction(SkillObservation observation, uint actionId)
    {
        if (!Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().TryGetRow(actionId, out var action))
            return;

        observation.ActionName = action.Name.ToString();
        observation.ActionCategoryId = action.ActionCategory.RowId;
        observation.ActionCategoryName = action.ActionCategory.Value.Name.ToString();
        observation.ActionCastType = action.CastType;
        if (string.IsNullOrWhiteSpace(observation.DamageType))
            observation.DamageType = GetActionCategoryDamageType(action);
        if (string.IsNullOrWhiteSpace(observation.Element))
            observation.Element = GetActionElement(action);
    }

    private static string GetActionCategoryDamageType(Lumina.Excel.Sheets.Action action)
    {
        return action.ActionCategory.RowId switch
        {
            1 or 3 => "Physical",
            2 => "Magical",
            _ => string.Empty
        };
    }

    private static string GetActionElement(Lumina.Excel.Sheets.Action action)
    {
        return action.Aspect switch
        {
            1 => "Fire",
            2 => "Ice",
            3 => "Wind",
            4 => "Earth",
            5 => "Lightning",
            6 => "Water",
            7 => "Unaspected",
            _ => "None"
        };
    }

    private static string NormalizeDamageType(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        if (value.Contains("dark", StringComparison.OrdinalIgnoreCase)
            || value.Contains("dunkel", StringComparison.OrdinalIgnoreCase)
            || value.Contains("darkness", StringComparison.OrdinalIgnoreCase)
            || value.Contains("unique", StringComparison.OrdinalIgnoreCase)
            || value.Contains("特", StringComparison.OrdinalIgnoreCase))
            return "Darkness";

        if (value.Contains("magic", StringComparison.OrdinalIgnoreCase)
            || value.Contains("magisch", StringComparison.OrdinalIgnoreCase)
            || value.Contains("魔", StringComparison.OrdinalIgnoreCase))
            return "Magical";

        if (value.Contains("physical", StringComparison.OrdinalIgnoreCase)
            || value.Contains("physisch", StringComparison.OrdinalIgnoreCase)
            || value.Contains("slashing", StringComparison.OrdinalIgnoreCase)
            || value.Contains("piercing", StringComparison.OrdinalIgnoreCase)
            || value.Contains("blunt", StringComparison.OrdinalIgnoreCase)
            || value.Contains("shot", StringComparison.OrdinalIgnoreCase)
            || value.Contains("斬", StringComparison.OrdinalIgnoreCase)
            || value.Contains("突", StringComparison.OrdinalIgnoreCase)
            || value.Contains("打", StringComparison.OrdinalIgnoreCase)
            || value.Contains("射", StringComparison.OrdinalIgnoreCase)
            || value.Contains("物理", StringComparison.OrdinalIgnoreCase))
            return "Physical";

        return string.Empty;
    }

    private static bool IsKnownDamageType(string value)
    {
        return value is "Physical" or "Magical" or "Darkness";
    }

    private static string NormalizeElement(string value)
    {
        return value switch
        {
            "Fire" or "Ice" or "Wind" or "Earth" or "Lightning" or "Water" or "Unaspected" => value,
            _ => string.Empty
        };
    }

    private static bool IsKnownElement(string value)
    {
        return value is "Fire" or "Ice" or "Wind" or "Earth" or "Lightning" or "Water" or "Unaspected";
    }

    private static void EnrichEnemy(SkillObservation observation, IBattleNpc npc, IGameObject source)
    {
        observation.SourceBaseId = source.BaseId;
        observation.BattleNpcNameId = npc.NameId;
        observation.BattleNpcKind = (byte)npc.BattleNpcKind;
        observation.ModelId = TryGetModelCharaId(source.BaseId) ?? source.BaseId;
        observation.ObjectKind = source.ObjectKind.ToString();
        observation.SubKind = source.SubKind.ToString();
        observation.CombatantType = ClassifyCombatant(source);
        observation.Level = npc.Level;

        var currentHp = npc.CurrentHp;
        var maxHp = npc.MaxHp;
        if (currentHp > 0)
        {
            observation.MinHp = observation.MinHp == 0 ? currentHp : Math.Min(observation.MinHp, currentHp);
            observation.MaxHp = Math.Max(observation.MaxHp, currentHp);
        }

        if (maxHp > 0)
        {
            observation.MinHp = observation.MinHp == 0 ? maxHp : Math.Min(observation.MinHp, maxHp);
            observation.MaxHp = Math.Max(observation.MaxHp, maxHp);
        }
    }

    private static uint? TryGetModelCharaId(uint bnpcBaseId)
    {
        return Plugin.DataManager.GetExcelSheet<BNpcBase>().TryGetRow(bnpcBaseId, out var row)
            ? row.ModelChara.RowId
            : null;
    }

    private static void RecordActionEffects(
        SkillObservation observation,
        IGameObject source,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId* targetEntityIds,
        byte numTargets)
    {
        var targetCount = Math.Min(numTargets, (byte)32);
        for (var targetIndex = 0; targetIndex < targetCount; targetIndex++)
        {
            var target = FindTarget(targetEntityIds[targetIndex]);
            foreach (var effect in effects[targetIndex].Effects)
            {
                if (effect.Type == 0)
                    continue;

                var raw = new RawActionEffectObservation
                {
                    Type = effect.Type,
                    Param0 = effect.Param0,
                    Param1 = effect.Param1,
                    Param2 = effect.Param2,
                    Param3 = effect.Param3,
                    Param4 = effect.Param4,
                    Value = effect.Value,
                    DamageType = IsDamageEffect(effect) ? NormalizeDamageType(GetNetworkDamageType(effect)) : string.Empty,
                    Element = IsDamageEffect(effect) ? GetNetworkElement(effect) : string.Empty
                };

                if (!observation.RawEffects.Any(existing =>
                        existing.Type == raw.Type
                        && existing.Param0 == raw.Param0
                        && existing.Param1 == raw.Param1
                        && existing.Param2 == raw.Param2
                        && existing.Param3 == raw.Param3
                        && existing.Param4 == raw.Param4
                        && existing.Value == raw.Value))
                    observation.RawEffects.Add(raw);

                if (IsDamageEffect(effect))
                {
                    observation.Damage.Add(effect.Value);
                    ApplyNetworkDamageClassification(observation, effect);
                }

                var statusId = TryGetStatusId(effect);
                if (statusId == null)
                    continue;

                var status = GetOrCreateStatus(observation.StatusApplications, statusId.Value);
                status.Count++;
                status.TargetRelations.Add(GetTargetRelation(source, target));
                UpdateMitigationType(status, effect);
            }
        }
    }

    private static bool IsDamageEffect(ActionEffectHandler.Effect effect)
    {
        return effect.Value > 0 && effect.Type is 3 or 4 or 5 or 6;
    }

    private static void ApplyNetworkDamageClassification(SkillObservation observation, ActionEffectHandler.Effect effect)
    {
        var damageType = GetNetworkDamageType(effect);
        var element = GetNetworkElement(effect);

        var normalizedDamageType = NormalizeDamageType(damageType);
        if (IsKnownDamageType(normalizedDamageType))
            observation.DamageType = normalizedDamageType;

        var normalizedElement = NormalizeElement(element);
        if (IsKnownElement(normalizedElement))
            observation.Element = normalizedElement;
    }

    private static string GetNetworkDamageType(ActionEffectHandler.Effect effect)
    {
        return (effect.Param3 & 0x0F) switch
        {
            1 or 2 or 3 or 4 or 7 => "Physical",
            5 => "Magical",
            6 => "Magical",
            9 => "Darkness",
            _ => string.Empty
        };
    }

    private static string GetNetworkElement(ActionEffectHandler.Effect effect)
    {
        return ((effect.Param3 >> 4) & 0x0F) switch
        {
            1 => "Fire",
            2 => "Ice",
            3 => "Wind",
            4 => "Earth",
            5 => "Lightning",
            6 => "Water",
            7 => "Unaspected",
            _ => "None"
        };
    }

    private static uint? TryGetStatusId(ActionEffectHandler.Effect effect)
    {
        if (effect.Type is not (14 or 15 or 16 or 17 or 18 or 19 or 20))
            return null;

        foreach (var candidate in GetStatusCandidates(effect))
        {
            if (candidate != 0 && Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>().TryGetRow(candidate, out var row) && !string.IsNullOrWhiteSpace(row.Name.ToString()))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<uint> GetStatusCandidates(ActionEffectHandler.Effect effect)
    {
        yield return effect.Value;
        yield return (uint)((effect.Param2 << 8) | effect.Param1);
        yield return (uint)((effect.Param3 << 8) | effect.Param2);
        yield return (uint)((effect.Param1 << 8) | effect.Param0);
        yield return effect.Param0;
        yield return effect.Param1;
        yield return effect.Param2;
        yield return effect.Param3;
    }

    private static StatusApplicationObservation GetOrCreateStatus(Dictionary<uint, StatusApplicationObservation> statuses, uint statusId)
    {
        if (statuses.TryGetValue(statusId, out var status))
            return status;

        status = new StatusApplicationObservation
        {
            StatusId = statusId,
            StatusIdHex = statusId.ToString("X")
        };
        EnrichStatus(status);
        statuses[statusId] = status;
        return status;
    }

    private static void EnrichStatus(StatusApplicationObservation status)
    {
        if (Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>().TryGetRow(status.StatusId, out var row))
            status.StatusName = row.Name.ToString();
    }

    private static void UpdateMitigationType(StatusApplicationObservation status, ActionEffectHandler.Effect effect)
    {
        if (effect.Type is not (14 or 15))
            return;

        var physical = GetMitigationPercent(effect.Param0);
        var magical = GetMitigationPercent(effect.Param1);

        if (!physical.HasValue && !magical.HasValue)
            return;

        status.PhysicalMitigationPercent = MaxNullable(status.PhysicalMitigationPercent, physical);
        status.MagicalMitigationPercent = MaxNullable(status.MagicalMitigationPercent, magical);
        status.MitigationType = GetMitigationType(status.PhysicalMitigationPercent, status.MagicalMitigationPercent);
    }

    private static uint? GetMitigationPercent(byte value)
    {
        var signed = unchecked((sbyte)value);
        return signed < 0 ? (uint)-signed : null;
    }

    private static uint? MaxNullable(uint? current, uint? next)
    {
        if (!next.HasValue)
            return current;

        return current.HasValue ? Math.Max(current.Value, next.Value) : next.Value;
    }

    private static string GetMitigationType(uint? physical, uint? magical)
    {
        return (physical.HasValue, magical.HasValue) switch
        {
            (true, true) when physical == magical => "all",
            (true, true) => "mixed",
            (true, false) => "physical",
            (false, true) => "magical",
            _ => "unknown"
        };
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        return needles.Any(text.Contains);
    }

    private static string GetTargetRelation(IGameObject source, IGameObject? target)
    {
        if (target == null)
            return "unknown";

        if (target.GameObjectId == source.GameObjectId)
            return "self";

        var sourceOwner = ResolveOwnerId(source);
        var targetOwner = ResolveOwnerId(target);

        if (sourceOwner != 0 && target.GameObjectId == sourceOwner)
            return "self";

        if (targetOwner != 0 && targetOwner == source.GameObjectId)
            return "self";

        if (sourceOwner != 0 && targetOwner != 0 && sourceOwner == targetOwner)
            return "self";

        if (IsLocalPlayer(target))
            return "player";

        if (target is IPlayerCharacter)
            return "ally";

        if (target.ObjectKind == DalamudObjectKind.BattleNpc)
            return "add";

        return target.ObjectKind.ToString();
    }

    private static string GetPlayerStatusTargetRelation(IGameObject source, IGameObject? effectTarget, uint statusId)
    {
        if (IsBeneficialStatus(statusId))
        {
            if (effectTarget == null || effectTarget.ObjectKind == DalamudObjectKind.BattleNpc)
                return source is IPlayerCharacter ? "player" : "self";

            return GetTargetRelation(source, effectTarget);
        }

        return GetTargetRelation(source, effectTarget);
    }

    private static bool IsBeneficialStatus(uint statusId)
    {
        if (!Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>().TryGetRow(statusId, out var status))
            return false;

        var name = status.Name.ToString();
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var lower = name.ToLowerInvariant();
        if (ContainsAny(lower, "bereit", "ready", "proc", "verfügbar", "verfuegbar", "boost", "besserung", "barriere", "schild", "aether", "mana"))
            return true;

        var flags = status.Flags.ToString();
        return flags.Contains("Buff", StringComparison.OrdinalIgnoreCase)
            || flags.Contains("Beneficial", StringComparison.OrdinalIgnoreCase);
    }

    private static ulong ResolveOwnerId(IGameObject obj)
    {
        if (obj is IPlayerCharacter)
            return obj.GameObjectId;

        return obj.OwnerId;
    }

    private static bool IsLocalPlayer(IGameObject target)
    {
        if (target is not IPlayerCharacter)
            return false;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is IPlayerCharacter player && player.ObjectIndex == 0)
                return player.GameObjectId == target.GameObjectId;
        }

        return false;
    }

    private JobObservation GetOrCreateJobObservation(uint jobId)
    {
        if (Plugin.DataManager.GetExcelSheet<ClassJob>().TryGetRow(jobId, out var row))
        {
            var name = row.Name.ToString();
            if (jobObservations.TryGetValue(jobId, out var existing))
                return existing;

            var observation = new JobObservation
            {
                ClassJobId = jobId,
                Name = string.IsNullOrWhiteSpace(name) ? $"ClassJob {jobId}" : name,
                Abbreviation = row.Abbreviation.ToString()
            };
            jobObservations[jobId] = observation;
            return observation;
        }

        if (jobObservations.TryGetValue(jobId, out var fallbackExisting))
            return fallbackExisting;

        var fallback = new JobObservation
        {
            ClassJobId = jobId,
            Name = $"ClassJob {jobId}"
        };
        jobObservations[jobId] = fallback;
        return fallback;
    }

    private static void EnrichJobSkill(JobSkillObservation skill, uint actionId)
    {
        if (Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().TryGetRow(actionId, out var action))
            skill.Name = action.Name.ToString();
    }

    private void AppendJsonLine(SkillObservation observation)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(jsonlPath)!);
            File.AppendAllText(jsonlPath, JsonSerializer.Serialize(observation, JsonOptions) + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not append enemy skill JSONL observation.");
        }
    }
}
