using System;
using System.Linq;
using Dalamud.Plugin.Services;
using WorkshoppaHelper.Retainers;
using WorkshoppaHelper.Workshoppa;

namespace WorkshoppaHelper;

public sealed class InventoryMonitor
{
    private readonly Configuration configuration;
    private readonly WorkshoppaBridge bridge;
    private readonly Orchestrator orchestrator;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;

    private DateTime lastCheck = DateTime.MinValue;
    private DateTime lastTrigger = DateTime.MinValue;
    private int freeSlots = -1;

    public InventoryMonitor(
        Configuration configuration,
        WorkshoppaBridge bridge,
        Orchestrator orchestrator,
        IClientState clientState,
        IObjectTable objectTable,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.bridge = bridge;
        this.orchestrator = orchestrator;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.log = log;
    }

    public int FreeSlots => freeSlots;

    public void Tick()
    {
        if (DateTime.UtcNow - lastCheck < TimeSpan.FromSeconds(1))
            return;

        lastCheck = DateTime.UtcNow;
        freeSlots = RetainerInterop.CountFreeSlots();

        if (!configuration.AutoStashEnabled)
            return;

        if (orchestrator.Status == RunStatus.Running)
            return;

        if (DateTime.UtcNow - lastTrigger < TimeSpan.FromMinutes(2))
            return;

        if (freeSlots > configuration.FreeSlotThreshold)
            return;

        if (objectTable.LocalPlayer == null || !RetainerInterop.WorkshopTerritories.Contains(clientState.TerritoryType))
            return;

        if (!bridge.IsAvailable || !bridge.IsAutomationRunning)
            return;

        lastTrigger = DateTime.UtcNow;
        log.Information($"[WorkshoppaHelper] Free slots {freeSlots} <= {configuration.FreeSlotThreshold}; auto-stashing");
        orchestrator.Start(RunKind.StashItems);
    }
}
