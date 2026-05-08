using System;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using AkusEnemySkillTracking.Services;
using AkusEnemySkillTracking.Windows;

namespace AkusEnemySkillTracking;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/akust";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    internal Configuration Configuration { get; }

    internal SkillRecorder Recorder { get; }

    internal WindowSystem WindowSystem { get; } = new("AkusEnemySkillTracking");

    private MainWindow MainWindow { get; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Recorder = new SkillRecorder(Configuration);
        MainWindow = new MainWindow(this);

        WindowSystem.AddWindow(MainWindow);
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open AkusEnemySkillTracking."
        });

        Log.Information("AkusEnemySkillTracking loaded.");
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();
        Recorder.Dispose();
    }

    internal void ToggleMainUi()
    {
        MainWindow.Toggle();
    }

    private void OnCommand(string command, string args)
    {
        if (args.Equals("export", StringComparison.OrdinalIgnoreCase))
        {
            Recorder.SaveSnapshot();
            return;
        }

        ToggleMainUi();
    }
}
