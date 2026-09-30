using System;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using WorkshoppaHelper.Retainers;
using WorkshoppaHelper.Windows;
using WorkshoppaHelper.Workshoppa;

namespace WorkshoppaHelper;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;

    internal static Plugin Instance { get; private set; } = null!;

    internal ProjectMaterialsService Materials { get; }

    private const string CmdMain = "/whelper";

    public Configuration Configuration { get; }

    private readonly WorkshoppaBridge bridge;
    private readonly RetainerInterop retainerInterop;
    private readonly AutoRetainerInterop autoRetainer;
    private readonly Orchestrator orchestrator;
    private readonly InventoryMonitor monitor;
    private readonly WindowSystem windowSystem = new("WorkshoppaHelper");
    private readonly MainWindow mainWindow;

    public Plugin()
    {
        Instance = this;

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        bridge = new WorkshoppaBridge(PluginInterface, Log);
        Materials = new ProjectMaterialsService(DataManager, Log);
        retainerInterop = new RetainerInterop(GameGui, DataManager, ObjectTable, Log, SigScanner);
        autoRetainer = new AutoRetainerInterop(PluginInterface, Log);
        orchestrator = new Orchestrator(
            Configuration, bridge, Materials, retainerInterop, autoRetainer,
            ClientState, ObjectTable, Condition, TargetManager, ChatGui, Log);
        monitor = new InventoryMonitor(Configuration, bridge, orchestrator, ClientState, ObjectTable, Log);
        mainWindow = new MainWindow(Configuration, bridge, Materials, retainerInterop, autoRetainer, orchestrator, monitor);

        windowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(CmdMain, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Workshoppa Helper window. Args: pull, stash, abort.",
        });

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += OpenConfigUi;

        Framework.Update += OnFrameworkUpdate;

        Log.Information("[WorkshoppaHelper] Plugin loaded. Use /whelper to open the window.");
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "pull":
                orchestrator.Start(RunKind.PullMaterials);
                break;

            case "stash":
                orchestrator.Start(RunKind.StashItems);
                break;

            case "abort":
                orchestrator.Abort("user command");
                break;

            default:
                mainWindow.IsOpen = !mainWindow.IsOpen;
                break;
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!ClientState.IsLoggedIn)
            return;

        var escapeDown = false;
        try
        {
            escapeDown = KeyState[VirtualKey.ESCAPE];
        }
        catch
        {
            // KeyState can throw during early login; ignore.
        }

        orchestrator.Tick(escapeDown);
        monitor.Tick();
    }

    private void DrawUI() => windowSystem.Draw();
    private void OpenConfigUi() => mainWindow.IsOpen = true;

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;

        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenConfigUi;

        CommandManager.RemoveHandler(CmdMain);

        orchestrator.Dispose();
        retainerInterop.Dispose();
        bridge.Dispose();

        windowSystem.RemoveAllWindows();
    }
}
