using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AkusEnemySkillTracking.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Dictionary<string, TimelineEntitySelection> timelineSelections = [];
    private List<PullTimelineEvent> generatedTimeline = [];
    private List<TimelineEntityRow> timelineEntities = [];
    private int timelineSelectionVersion = -1;
    private string timelineExportStatus = "Timeline has not been exported this session.";

    public MainWindow(Plugin plugin)
        : base("AkusEnemySkillTracking##AkusEnemySkillTracking")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        DrawTabs();
    }

    private void DrawSettings()
    {
        var config = plugin.Configuration;
        var enabled = config.Enabled;
        if (ImGui.Checkbox("Recording enabled", ref enabled))
        {
            config.Enabled = enabled;
            config.Save();
        }

        ImGui.SameLine();
        var jsonl = config.RecordJsonLines;
        if (ImGui.Checkbox("Append JSONL", ref jsonl))
        {
            config.RecordJsonLines = jsonl;
            config.Save();
        }

        if (jsonl)
        {
            ImGui.SameLine();
            var maxJsonlMb = config.JsonLinesMaxMegabytes;
            ImGui.SetNextItemWidth(90);
            if (ImGui.InputInt("Max JSONL MB", ref maxJsonlMb))
            {
                config.JsonLinesMaxMegabytes = Math.Clamp(maxJsonlMb, 1, 1024);
                config.Save();
            }
        }

        var autoSaveOnZone = config.AutoSaveOnTerritoryChange;
        if (ImGui.Checkbox("Autosave on zone change", ref autoSaveOnZone))
        {
            config.AutoSaveOnTerritoryChange = autoSaveOnZone;
            config.Save();
        }

        ImGui.SameLine();
        var autoSaveOnKo = config.AutoSaveOnLocalPlayerKo;
        if (ImGui.Checkbox("Autosave on KO", ref autoSaveOnKo))
        {
            config.AutoSaveOnLocalPlayerKo = autoSaveOnKo;
            config.Save();
        }

        ImGui.SameLine();
        var autoSaveMinutes = config.AutoSaveIntervalMinutes;
        ImGui.SetNextItemWidth(90);
        if (ImGui.InputInt("Autosave min", ref autoSaveMinutes))
        {
            config.AutoSaveIntervalMinutes = Math.Clamp(autoSaveMinutes, 0, 60);
            config.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Storage");

        var storeLocal = config.StoreLocalFiles;
        if (ImGui.Checkbox("Store local files", ref storeLocal))
        {
            config.StoreLocalFiles = storeLocal;
            config.Save();
        }

        ImGui.SameLine();
        var remoteUpload = config.RemoteUploadEnabled;
        if (ImGui.Checkbox("Remote upload", ref remoteUpload))
        {
            config.RemoteUploadEnabled = remoteUpload;
            config.Save();
        }

        if (remoteUpload)
        {
            var storeLocalWithRemote = config.StoreLocalFilesWhenRemoteUploadEnabled;
            if (ImGui.Checkbox("Try remote, but also store local copy", ref storeLocalWithRemote))
            {
                config.StoreLocalFilesWhenRemoteUploadEnabled = storeLocalWithRemote;
                config.Save();
            }

            ImGui.TextUnformatted("Endpoint URL");
            var endpoint = config.RemoteEndpointUrl;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##RemoteEndpointUrl", ref endpoint, 512))
            {
                config.RemoteEndpointUrl = endpoint.Trim();
                config.Save();
            }

            ImGui.TextUnformatted("Endpoint token");
            var token = config.RemoteEndpointToken;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##RemoteEndpointToken", ref token, 256, ImGuiInputTextFlags.Password))
            {
                config.RemoteEndpointToken = token;
                config.Save();
            }

            if (plugin.Recorder.UploadInProgress)
                ImGui.TextUnformatted("Upload status: running...");
            else
                // ImGui.TextUnformatted($"Upload status: {plugin.Recorder.LastUploadStatus}");
                ImGui.TextUnformatted($"Upload status: done");
        }

        if (plugin.Recorder.SaveInProgress)
            ImGui.TextUnformatted("Save status: running...");
        else
            ImGui.TextUnformatted($"Save status: {plugin.Recorder.LastSaveStatus}");

        ImGui.TextUnformatted($"Enemy skill observations: {plugin.Recorder.Observations.Count} | Job buckets: {plugin.Recorder.JobObservations.Count} | Music observations: {plugin.Recorder.MusicObservations.Count} | Chat lines: {plugin.Recorder.ChatLines.Count}");

        if (ImGui.Button("Save snapshot"))
            plugin.Recorder.SaveSnapshot();

        ImGui.SameLine();
        if (ImGui.Button("Open folder"))
            OpenFolder(Path.GetDirectoryName(plugin.Recorder.SnapshotPath)!);

        ImGui.SameLine();
        if (ImGui.Button("Clear JSONL"))
            plugin.Recorder.ClearJsonLines();

        ImGui.SameLine();
        if (ImGui.Button("Clear"))
            ImGui.OpenPopup("Clear observations?");

        using (var popup = ImRaii.PopupModal("Clear observations?", ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (popup.Success)
            {
                ImGui.TextUnformatted("Delete all in-memory observations and overwrite the snapshot?");
                if (ImGui.Button("Clear now"))
                {
                    plugin.Recorder.Clear();
                    ImGui.CloseCurrentPopup();
                }

                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                    ImGui.CloseCurrentPopup();
            }
        }

        ImGui.Separator();
    }

    private void DrawTabs()
    {
        using var tabs = ImRaii.TabBar("tracking-tabs");
        if (!tabs.Success)
            return;

        using (var tab = ImRaii.TabItem("Settings"))
        {
            if (tab.Success)
                DrawSettings();
        }

        using (var tab = ImRaii.TabItem("Enemy skills"))
        {
            if (tab.Success)
                DrawEnemySkills();
        }

        using (var tab = ImRaii.TabItem("Statuses"))
        {
            if (tab.Success)
                DrawStatuses();
        }

        using (var tab = ImRaii.TabItem("Music"))
        {
            if (tab.Success)
                DrawMusic();
        }

        using (var tab = ImRaii.TabItem("Chat lines"))
        {
            if (tab.Success)
                DrawChatLines();
        }

        using (var tab = ImRaii.TabItem("Player jobs"))
        {
            if (tab.Success)
                DrawPlayerJobs();
        }

        using (var tab = ImRaii.TabItem("Pull timeline"))
        {
            if (tab.Success)
                DrawPullTimeline();
        }
    }

    private void DrawPullTimeline()
    {
        var events = plugin.Recorder.ActivePullTimeline;
        SyncTimelineSelections(events);

        ImGui.TextUnformatted($"Tracked pull events: {events.Count} | Generated rows: {generatedTimeline.Count}");
        ImGui.SameLine();
        if (plugin.Recorder.CurrentPullTimeline.Count == 0 && plugin.Recorder.LastPullTimeline.Count > 0)
            ImGui.TextUnformatted("(showing last pull)");
        else
            ImGui.TextUnformatted("(showing current pull)");

        if (ImGui.Button("Enable all"))
            SetAllTimelineSelections(true);

        ImGui.SameLine();
        if (ImGui.Button("Disable all"))
            SetAllTimelineSelections(false);

        ImGui.SameLine();
        if (ImGui.Button("Generate timeline"))
            generatedTimeline = events.Where(IsTimelineEventSelected).OrderBy(e => e.SecondsFromPullStart).ToList();

        ImGui.SameLine();
        if (ImGui.Button("Export CSV"))
            ExportGeneratedTimelineCsv();

        ImGui.SameLine();
        if (ImGui.Button("Export JSON"))
            ExportGeneratedTimelineJson();

        ImGui.TextUnformatted($"Export status: {timelineExportStatus}");
        ImGui.Separator();
        DrawGeneratedTimeline();
        ImGui.Separator();
        DrawTimelineEntityToggles();
    }

    private void DrawTimelineEntityToggles()
    {
        using var table = ImRaii.Table("timeline-entities", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Entity");
        ImGui.TableSetupColumn("Type");
        ImGui.TableSetupColumn("Actions");
        ImGui.TableSetupColumn("Statuses");
        ImGui.TableSetupColumn("Mitigation");
        ImGui.TableSetupColumn("Healing");
        ImGui.TableHeadersRow();

        foreach (var entity in timelineEntities
                     .OrderBy(e => e.SourceType, StringComparer.CurrentCulture)
                     .ThenBy(e => e.SourceName, StringComparer.CurrentCulture))
        {
            if (!timelineSelections.TryGetValue(entity.SourceKey, out var selection))
                continue;

            ImGui.TableNextRow();
            TextCell(entity.SourceName);
            TextCell(entity.SourceType);

            ImGui.TableNextColumn();
            if (entity.SourceType == "Enemy")
            {
                var enabled = selection.IncludeEnemyActions;
                if (ImGui.Checkbox($"##action-{entity.SourceKey}", ref enabled))
                    selection.IncludeEnemyActions = enabled;
            }

            ImGui.TableNextColumn();
            if (entity.SourceType == "Enemy")
            {
                var enabled = selection.IncludeEnemyStatuses;
                if (ImGui.Checkbox($"##status-{entity.SourceKey}", ref enabled))
                    selection.IncludeEnemyStatuses = enabled;
            }

            ImGui.TableNextColumn();
            if (entity.SourceType == "Ally")
            {
                var enabled = selection.IncludeAllyMitigation;
                if (ImGui.Checkbox($"##mitigation-{entity.SourceKey}", ref enabled))
                    selection.IncludeAllyMitigation = enabled;
            }

            ImGui.TableNextColumn();
            if (entity.SourceType == "Ally")
            {
                var enabled = selection.IncludeAllyHealing;
                if (ImGui.Checkbox($"##healing-{entity.SourceKey}", ref enabled))
                    selection.IncludeAllyHealing = enabled;
            }
        }
    }

    private void DrawGeneratedTimeline()
    {
        ImGui.TextUnformatted("Generated timeline");
        if (generatedTimeline.Count == 0)
            ImGui.TextUnformatted("No rows generated yet. Select entities below, then press Generate timeline.");

        using var table = ImRaii.Table("generated-timeline", 11, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Time");
        ImGui.TableSetupColumn("UTC");
        ImGui.TableSetupColumn("Zone");
        ImGui.TableSetupColumn("Entity");
        ImGui.TableSetupColumn("Type");
        ImGui.TableSetupColumn("Action");
        ImGui.TableSetupColumn("Hex");
        ImGui.TableSetupColumn("Status");
        ImGui.TableSetupColumn("Healing");
        ImGui.TableSetupColumn("Party HP");
        ImGui.TableSetupColumn("Targets");
        ImGui.TableHeadersRow();

        foreach (var item in generatedTimeline.Take(1000))
        {
            ImGui.TableNextRow();
            TextCell(FormatTimelineTime(item.SecondsFromPullStart));
            TextCell(item.SeenAtUtc.ToString("HH:mm:ss.fff"));
            TextCell(item.TerritoryName);
            TextCell(item.SourceName);
            TextCell(FormatTimelineKind(item.Kind));
            TextCell(string.IsNullOrWhiteSpace(item.ActionName) ? "(unknown)" : item.ActionName);
            TextCell(item.ActionIdHex);
            TextCell(FormatTimelineStatus(item));
            TextCell(item.HealingAmount == 0 ? "" : item.HealingAmount.ToString());
            TextCell(FormatPartyHp(item.PartyHp));
            TextCell(string.Join(", ", item.TargetRelations));
        }
    }

    private void SyncTimelineSelections(IReadOnlyList<PullTimelineEvent> events)
    {
        if (timelineSelectionVersion == plugin.Recorder.PullTimelineVersion)
            return;

        timelineEntities = BuildTimelineEntityRows(events);
        var keys = timelineEntities.Select(e => e.SourceKey).Distinct().ToHashSet(StringComparer.CurrentCulture);
        foreach (var key in timelineSelections.Keys.Where(key => !keys.Contains(key)).ToArray())
            timelineSelections.Remove(key);

        foreach (var item in timelineEntities)
        {
            if (!timelineSelections.ContainsKey(item.SourceKey))
                timelineSelections[item.SourceKey] = new TimelineEntitySelection();
        }

        timelineSelectionVersion = plugin.Recorder.PullTimelineVersion;
    }

    private static List<TimelineEntityRow> BuildTimelineEntityRows(IReadOnlyList<PullTimelineEvent> events)
    {
        var rows = new Dictionary<string, TimelineEntityRow>(StringComparer.CurrentCulture);
        foreach (var item in events)
        {
            rows.TryAdd(item.SourceKey, new TimelineEntityRow(item.SourceKey, item.SourceName, item.SourceType));
        }

        AddLocalPlayerTimelineEntity(rows);

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null)
                continue;

            if (obj is IPlayerCharacter player)
            {
                var key = GetTimelineEntitySourceKey(obj, player);
                rows.TryAdd(key, new TimelineEntityRow(key, player.Name.ToString(), "Ally"));
                continue;
            }

            if (obj.ObjectKind == ObjectKind.BattleNpc && !string.IsNullOrWhiteSpace(obj.Name.ToString()))
            {
                var key = GetTimelineEntitySourceKey(obj);
                rows.TryAdd(key, new TimelineEntityRow(key, obj.Name.ToString(), "Enemy"));
            }
        }

        return rows.Values.ToList();
    }

    private static void AddLocalPlayerTimelineEntity(Dictionary<string, TimelineEntityRow> rows)
    {
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IPlayerCharacter player || player.ObjectIndex != 0)
                continue;

            var key = GetTimelineEntitySourceKey(player, player);
            rows.TryAdd(key, new TimelineEntityRow(key, player.Name.ToString(), "Ally"));
            return;
        }
    }

    private static string GetTimelineEntitySourceKey(IGameObject source, IPlayerCharacter? owner = null)
    {
        if (owner != null)
            return $"ally:{owner.GameObjectId:X}:{source.GameObjectId:X}";

        return $"{source.ObjectKind}:{source.BaseId}:{source.GameObjectId:X}";
    }

    private void SetAllTimelineSelections(bool enabled)
    {
        foreach (var selection in timelineSelections.Values)
        {
            selection.IncludeEnemyActions = enabled;
            selection.IncludeEnemyStatuses = enabled;
            selection.IncludeAllyMitigation = enabled;
            selection.IncludeAllyHealing = enabled;
        }
    }

    private bool IsTimelineEventSelected(PullTimelineEvent item)
    {
        if (!timelineSelections.TryGetValue(item.SourceKey, out var selection))
            return false;

        return item.Kind switch
        {
            PullTimelineEventKind.EnemyAction => selection.IncludeEnemyActions,
            PullTimelineEventKind.EnemyStatus => selection.IncludeEnemyStatuses,
            PullTimelineEventKind.AllyMitigation => selection.IncludeAllyMitigation,
            PullTimelineEventKind.AllyHealing => selection.IncludeAllyHealing,
            _ => false
        };
    }

    private void ExportGeneratedTimelineCsv()
    {
        if (generatedTimeline.Count == 0)
        {
            timelineExportStatus = "Generate a timeline before exporting.";
            return;
        }

        try
        {
            var path = GetTimelineExportPath("csv");
            var csv = new StringBuilder();
            csv.AppendLine("time,seconds_from_pull_start,seen_at_utc,zone,entity,source_type,event_type,action_id,action_hex,action,status_id,status_hex,status,mitigation,healing_amount,party_hp,targets");
            foreach (var item in generatedTimeline)
            {
                csv.AppendLine(string.Join(",", new[]
                {
                    CsvEscape(FormatTimelineTime(item.SecondsFromPullStart)),
                    CsvEscape(item.SecondsFromPullStart.ToString("0.000")),
                    CsvEscape(item.SeenAtUtc.ToString("O")),
                    CsvEscape(item.TerritoryName),
                    CsvEscape(item.SourceName),
                    CsvEscape(item.SourceType),
                    CsvEscape(FormatTimelineKind(item.Kind)),
                    CsvEscape(item.ActionId.ToString()),
                    CsvEscape(item.ActionIdHex),
                    CsvEscape(item.ActionName),
                    CsvEscape(item.StatusId == 0 ? "" : item.StatusId.ToString()),
                    CsvEscape(item.StatusIdHex),
                    CsvEscape(item.StatusName),
                    CsvEscape(item.MitigationType),
                    CsvEscape(item.HealingAmount == 0 ? "" : item.HealingAmount.ToString()),
                    CsvEscape(FormatPartyHp(item.PartyHp)),
                    CsvEscape(string.Join(", ", item.TargetRelations))
                }));
            }

            File.WriteAllText(path, csv.ToString(), Encoding.UTF8);
            timelineExportStatus = $"CSV exported to {path}";
        }
        catch (Exception ex)
        {
            timelineExportStatus = $"CSV export failed: {ex.Message}";
            Plugin.Log.Warning(ex, "Could not export pull timeline CSV.");
        }
    }

    private void ExportGeneratedTimelineJson()
    {
        if (generatedTimeline.Count == 0)
        {
            timelineExportStatus = "Generate a timeline before exporting.";
            return;
        }

        try
        {
            var path = GetTimelineExportPath("json");
            var json = JsonSerializer.Serialize(generatedTimeline, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            File.WriteAllText(path, json, Encoding.UTF8);
            timelineExportStatus = $"JSON exported to {path}";
        }
        catch (Exception ex)
        {
            timelineExportStatus = $"JSON export failed: {ex.Message}";
            Plugin.Log.Warning(ex, "Could not export pull timeline JSON.");
        }
    }

    private string GetTimelineExportPath(string extension)
    {
        var directory = Path.GetDirectoryName(plugin.Recorder.SnapshotPath)!;
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"pull-timeline-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.{extension}");
    }

    private static string CsvEscape(string? value)
    {
        value ??= string.Empty;
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private void DrawEnemySkills()
    {
        using var table = ImRaii.Table("recent-actions", 14, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Zone");
        ImGui.TableSetupColumn("Enemy");
        ImGui.TableSetupColumn("BNpc");
        ImGui.TableSetupColumn("Model");
        ImGui.TableSetupColumn("Level");
        ImGui.TableSetupColumn("HP");
        ImGui.TableSetupColumn("Action");
        ImGui.TableSetupColumn("Hex");
        ImGui.TableSetupColumn("Damage");
        ImGui.TableSetupColumn("Damage type");
        ImGui.TableSetupColumn("Element");
        ImGui.TableSetupColumn("Applied statuses");
        ImGui.TableSetupColumn("Uses");
        ImGui.TableSetupColumn("Last seen UTC");
        ImGui.TableHeadersRow();

        foreach (var item in plugin.Recorder.Recent.Take(200))
        {
            ImGui.TableNextRow();
            TextCell(item.TerritoryName);
            TextCell(item.SourceName);
            TextCell(item.BattleNpcNameId == 0 ? item.SourceDataId.ToString() : item.BattleNpcNameId.ToString());
            TextCell(item.ModelId == 0 ? "" : item.ModelId.ToString());
            TextCell(item.Level == 0 ? "" : item.Level.ToString());
            TextCell(item.MaxHp == 0 ? "" : $"{item.MinHp}-{item.MaxHp}");
            TextCell(string.IsNullOrWhiteSpace(item.ActionName) ? "(unknown)" : item.ActionName);
            TextCell(item.ActionIdHex);
            TextCell(FormatRange(item.Damage));
            TextCell(item.DamageType);
            TextCell(item.Element);
            TextCell(FormatStatusesWithIds(item.StatusApplications.Values));
            TextCell(item.TotalUses.ToString());
            TextCell(item.LastSeenUtc.ToString("u"));
        }
    }

    private void DrawStatuses()
    {
        var enemyRows = plugin.Recorder.Observations
            .SelectMany(skill => skill.StatusApplications.Values.Select(status => new
            {
                SourceType = "Enemy",
                skill.TerritoryName,
                Actor = skill.SourceName,
                Skill = string.IsNullOrWhiteSpace(skill.ActionName) ? skill.ActionIdHex : skill.ActionName,
                Status = string.IsNullOrWhiteSpace(status.StatusName) ? "(unknown)" : status.StatusName,
                Hex = status.StatusIdHex,
                Mitigation = status.MitigationType,
                Targets = string.Join(", ", status.TargetRelations),
                Raw = ""
            }))
            .Concat(plugin.Recorder.JobObservations.Values.SelectMany(job => job.Skills.Values.SelectMany(skill => skill.StatusApplications.Values.Select(status => new
            {
                SourceType = "Player skill",
                TerritoryName = "",
                Actor = job.Name,
                Skill = string.IsNullOrWhiteSpace(skill.Name) ? skill.ActionIdHex : skill.Name,
                Status = string.IsNullOrWhiteSpace(status.StatusName) ? "(unknown)" : status.StatusName,
                Hex = status.StatusIdHex,
                Mitigation = status.MitigationType,
                Targets = string.Join(", ", status.TargetRelations),
                Raw = ""
            }))))
            .Concat(plugin.Recorder.JobObservations.Values.SelectMany(job => job.StatusApplications.Values.Select(status => new
            {
                SourceType = "Player job",
                TerritoryName = "",
                Actor = job.Name,
                Skill = "(any skill)",
                Status = string.IsNullOrWhiteSpace(status.StatusName) ? "(unknown)" : status.StatusName,
                Hex = status.StatusIdHex,
                Mitigation = status.MitigationType,
                Targets = string.Join(", ", status.TargetRelations),
                Raw = ""
            })))
            .Concat(plugin.Recorder.Observations.SelectMany(skill => skill.RawEffects
                .Where(IsStatusLikeRawEffect)
                .Select(raw => new
                {
                    SourceType = "Raw",
                    skill.TerritoryName,
                    Actor = skill.SourceName,
                    Skill = string.IsNullOrWhiteSpace(skill.ActionName) ? skill.ActionIdHex : skill.ActionName,
                    Status = "(raw status-like effect)",
                    Hex = "",
                    Mitigation = "unknown",
                    Targets = "",
                    Raw = $"type={raw.Type} p0={raw.Param0} p1={raw.Param1} p2={raw.Param2} p3={raw.Param3} p4={raw.Param4} value={raw.Value}"
                })))
            .OrderBy(row => row.SourceType, StringComparer.CurrentCulture)
            .ThenBy(row => row.TerritoryName, StringComparer.CurrentCulture)
            .ThenBy(row => row.Actor, StringComparer.CurrentCulture)
            .Take(700);

        using var table = ImRaii.Table("statuses", 9, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Type");
        ImGui.TableSetupColumn("Zone");
        ImGui.TableSetupColumn("Actor");
        ImGui.TableSetupColumn("Skill");
        ImGui.TableSetupColumn("Status");
        ImGui.TableSetupColumn("Hex");
        ImGui.TableSetupColumn("Mitigation");
        ImGui.TableSetupColumn("Targets");
        ImGui.TableSetupColumn("Raw");
        ImGui.TableHeadersRow();

        foreach (var row in enemyRows)
        {
            ImGui.TableNextRow();
            TextCell(row.SourceType);
            TextCell(row.TerritoryName);
            TextCell(row.Actor);
            TextCell(row.Skill);
            TextCell(row.Status);
            TextCell(row.Hex);
            TextCell(row.Mitigation);
            TextCell(row.Targets);
            TextCell(row.Raw);
        }
    }

    private void DrawMusic()
    {
        using var table = ImRaii.Table("music", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Zone");
        ImGui.TableSetupColumn("BGM ID");
        ImGui.TableSetupColumn("Hex");
        ImGui.TableSetupColumn("Name");
        ImGui.TableSetupColumn("File");
        ImGui.TableSetupColumn("Seen");
        ImGui.TableHeadersRow();

        foreach (var item in plugin.Recorder.MusicObservations
                     .OrderBy(m => m.TerritoryName, StringComparer.CurrentCulture)
                     .ThenBy(m => m.BgmId)
                     .Take(500))
        {
            ImGui.TableNextRow();
            TextCell(item.TerritoryNameResolved ? item.TerritoryName : $"{item.TerritoryName} (unresolved)");
            TextCell(item.BgmId.ToString());
            TextCell(item.BgmId.ToString("X"));
            TextCell(item.Name);
            TextCell(item.File);
            TextCell(item.Count.ToString());
        }
    }

    private void DrawChatLines()
    {
        using var table = ImRaii.Table("chat-lines", 9, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Zone");
        ImGui.TableSetupColumn("Category");
        ImGui.TableSetupColumn("Type ID");
        ImGui.TableSetupColumn("Log ID");
        ImGui.TableSetupColumn("Type");
        ImGui.TableSetupColumn("Sender");
        ImGui.TableSetupColumn("Message");
        ImGui.TableSetupColumn("Parameters");
        ImGui.TableSetupColumn("Seen UTC");
        ImGui.TableHeadersRow();

        foreach (var item in plugin.Recorder.ChatLines
                     .OrderByDescending(c => c.SeenAtUtc)
                     .Take(500))
        {
            ImGui.TableNextRow();
            TextCell(item.TerritoryNameResolved ? item.TerritoryName : $"{item.TerritoryName} (unresolved)");
            TextCell(item.Category);
            TextCell(item.TypeId.ToString());
            TextCell(item.LogMessageId?.ToString() ?? "");
            TextCell(item.TypeName);
            TextCell(item.Sender);
            TextCell(item.Message);
            TextCell(string.Join(", ", item.Parameters));
            TextCell(item.SeenAtUtc.ToString("u"));
        }
    }

    private void DrawPlayerJobs()
    {
        using var table = ImRaii.Table("player-jobs", 11, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Job");
        ImGui.TableSetupColumn("Level");
        ImGui.TableSetupColumn("Source");
        ImGui.TableSetupColumn("Skill");
        ImGui.TableSetupColumn("Hex");
        ImGui.TableSetupColumn("Damage");
        ImGui.TableSetupColumn("Applied statuses");
        ImGui.TableSetupColumn("Mitigation");
        ImGui.TableSetupColumn("Uses");
        ImGui.TableSetupColumn("Status targets");
        ImGui.TableSetupColumn("Job statuses");
        ImGui.TableHeadersRow();

        foreach (var job in plugin.Recorder.JobObservations.Values.OrderBy(j => j.Name, StringComparer.CurrentCulture))
        {
            if (job.Skills.Count == 0)
            {
                ImGui.TableNextRow();
                TextCell(job.Name);
                TextCell(job.HighestSeenLevel.ToString());
                TextCell("");
                TextCell("");
                TextCell("");
                TextCell("");
                TextCell("");
                TextCell("");
                TextCell(job.TotalActions.ToString());
                TextCell(FormatStatusTargets(job.StatusApplications.Values));
                TextCell(FormatStatusesWithIds(job.StatusApplications.Values));
                continue;
            }

            foreach (var skill in job.Skills.Values.OrderByDescending(s => s.Count).ThenBy(s => s.Name, StringComparer.CurrentCulture).Take(300))
            {
                ImGui.TableNextRow();
                TextCell(job.Name);
                TextCell(job.HighestSeenLevel.ToString());
                TextCell(FormatSources(skill.Sources));
                TextCell(string.IsNullOrWhiteSpace(skill.Name) ? "(unknown)" : skill.Name);
                TextCell(skill.ActionIdHex);
                TextCell(FormatRange(skill.Damage));
                TextCell(FormatStatusesWithIds(skill.StatusApplications.Values));
                TextCell(FormatMitigation(skill.StatusApplications.Values));
                TextCell(skill.Count.ToString());
                TextCell(FormatStatusTargets(skill.StatusApplications.Values));
                TextCell(FormatStatusesWithIds(job.StatusApplications.Values));
            }
        }
    }

    private static void TextCell(string text)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(text);
    }

    private static string FormatRange(RangeValue range)
    {
        return range.Min.HasValue || range.Max.HasValue ? $"{range.Min ?? 0}-{range.Max ?? 0}" : "";
    }

    private static string FormatSources(IEnumerable<string> sources)
    {
        return string.Join(", ", sources.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Take(4));
    }

    private static string FormatStatuses(IEnumerable<StatusApplicationObservation> statuses)
    {
        return string.Join(", ", statuses.Take(6).Select(s => string.IsNullOrWhiteSpace(s.StatusName) ? s.StatusIdHex : s.StatusName));
    }

    private static string FormatStatusesWithIds(IEnumerable<StatusApplicationObservation> statuses)
    {
        return string.Join(", ", statuses.Take(8).Select(s =>
        {
            var name = string.IsNullOrWhiteSpace(s.StatusName) ? "(unknown)" : s.StatusName;
            var mitigation = s.MitigationType == "unknown" ? "" : $" {s.MitigationType}";
            return $"{name} [{s.StatusIdHex}]{mitigation}";
        }));
    }

    private static string FormatMitigation(IEnumerable<StatusApplicationObservation> statuses)
    {
        var values = statuses
            .Select(s => s.MitigationType)
            .Where(s => !string.IsNullOrWhiteSpace(s) && s != "unknown")
            .Distinct()
            .Take(4)
            .ToArray();

        return values.Length == 0 ? "unknown" : string.Join(", ", values);
    }

    private static string FormatStatusTargets(IEnumerable<StatusApplicationObservation> statuses)
    {
        return string.Join(", ", statuses.SelectMany(s => s.TargetRelations).Distinct().Take(6));
    }

    private static string FormatTimelineTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return $"{(int)time.TotalMinutes:00}:{time.Seconds:00}.{time.Milliseconds / 100}";
    }

    private static string FormatTimelineKind(PullTimelineEventKind kind)
    {
        return kind switch
        {
            PullTimelineEventKind.EnemyAction => "Enemy action",
            PullTimelineEventKind.EnemyStatus => "Enemy status",
            PullTimelineEventKind.AllyMitigation => "Ally mitigation",
            PullTimelineEventKind.AllyHealing => "Ally healing",
            _ => kind.ToString()
        };
    }

    private static string FormatTimelineStatus(PullTimelineEvent item)
    {
        if (item.StatusId == 0)
            return string.Empty;

        var name = string.IsNullOrWhiteSpace(item.StatusName) ? "(unknown)" : item.StatusName;
        var mitigation = string.IsNullOrWhiteSpace(item.MitigationType) || item.MitigationType == "unknown" ? string.Empty : $" {item.MitigationType}";
        return $"{name} [{item.StatusIdHex}]{mitigation}";
    }

    private static string FormatPartyHp(IEnumerable<PartyHpSnapshot> partyHp)
    {
        return string.Join(" | ", partyHp.Select(member => $"{member.Name} {member.CurrentHp}/{member.MaxHp} ({member.Percent:0.#}%)"));
    }

    private static bool IsStatusLikeRawEffect(RawActionEffectObservation effect)
    {
        return effect.Type is >= 14 and <= 20;
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not open output folder.");
        }
    }

    private sealed class TimelineEntitySelection
    {
        public bool IncludeEnemyActions { get; set; } = true;

        public bool IncludeEnemyStatuses { get; set; } = true;

        public bool IncludeAllyMitigation { get; set; } = true;

        public bool IncludeAllyHealing { get; set; } = true;
    }

    private sealed record TimelineEntityRow(string SourceKey, string SourceName, string SourceType);
}
