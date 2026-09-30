using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace WorkshoppaHelper.Workshoppa;

public sealed record WorkshopPhase(string Name, IReadOnlyList<WorkshopItem> Items);

public sealed record WorkshopItem(uint ItemId, string Name, int TotalQuantity);

public sealed record WorkshopCraft(uint WorkshopItemId, uint ResultItem, string Name, IReadOnlyList<WorkshopPhase> Phases);

/// <summary>
/// Mirrors Workshoppa's WorkshopCache: expands FC workshop crafts from Lumina
/// (CompanyCraftSequence -> Part -> Process -> SupplyItem) and computes the materials still
/// needed for a queue snapshot, matching Workshoppa's own "Check Inventory" math.
/// </summary>
public sealed class ProjectMaterialsService
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    private Dictionary<uint, WorkshopCraft> crafts = new();
    private Task? loadTask;
    private bool loadFailed;

    public ProjectMaterialsService(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;

        loadTask = Task.Run(LoadCrafts);
    }

    public bool IsLoaded => loadTask?.IsCompleted == true && !loadFailed;

    public bool TryGetCraft(uint workshopItemId, out WorkshopCraft craft) => crafts.TryGetValue(workshopItemId, out craft!);

    private void LoadCrafts()
    {
        try
        {
            var itemMapping = dataManager.GetExcelSheet<CompanyCraftSupplyItem>()
                .Where(x => x.RowId > 0)
                .ToDictionary(x => x.RowId, x => x.Item);

            var result = new Dictionary<uint, WorkshopCraft>();
            foreach (var sequence in dataManager.GetExcelSheet<CompanyCraftSequence>().Where(x => x.RowId > 0))
            {
                var phases = new List<WorkshopPhase>();
                foreach (var part in sequence.CompanyCraftPart.Where(part => part.RowId != 0))
                {
                    foreach (var process in part.Value.CompanyCraftProcess)
                    {
                        var items = new List<WorkshopItem>();
                        for (var i = 0; i < process.Value.SupplyItem.Count; i++)
                        {
                            var supplyItem = process.Value.SupplyItem[i];
                            if (supplyItem.RowId == 0 || !itemMapping.TryGetValue(supplyItem.RowId, out var itemRow))
                                continue;

                            var item = itemRow.Value;
                            var setQuantity = (int)process.Value.SetQuantity[i];
                            var setsRequired = (int)process.Value.SetsRequired[i];
                            if (setQuantity <= 0 || setsRequired <= 0)
                                continue;

                            items.Add(new WorkshopItem(
                                item.RowId,
                                item.Name.ToString(),
                                setQuantity * setsRequired));
                        }

                        if (items.Count > 0)
                            phases.Add(new WorkshopPhase(part.Value.CompanyCraftType.Value.Name.ToString(), items));
                    }
                }

                if (phases.Count > 0)
                {
                    result[sequence.RowId] = new WorkshopCraft(
                        sequence.RowId,
                        sequence.ResultItem.RowId,
                        sequence.ResultItem.Value.Name.ToString(),
                        phases);
                }
            }

            crafts = result;
            log.Information($"[WorkshoppaHelper] Loaded {result.Count} workshop crafts from Lumina");
        }
        catch (Exception e)
        {
            loadFailed = true;
            log.Error(e, "Failed to load workshop crafts from Lumina");
        }
    }

    /// <summary>
    /// Total materials still needed to finish everything in the snapshot's queue, i.e. all phases of
    /// all queued crafts minus phases/contributions already completed on the current craft.
    /// Mirrors Workshoppa's MainWindow.GetMaterialList.
    /// </summary>
    public Dictionary<uint, int> ComputeRemainingNeeded(QueueSnapshot snapshot)
    {
        var totals = new Dictionary<uint, int>();

        void AddMaterial(Dictionary<uint, int> dict, uint itemId, int quantity)
        {
            if (quantity <= 0)
                return;

            dict.TryGetValue(itemId, out var existing);
            dict[itemId] = existing + quantity;
        }

        var workshopItemIds = new List<uint>();
        foreach (var queued in snapshot.Queue)
        {
            for (var i = 0; i < queued.Quantity; i++)
                workshopItemIds.Add(queued.WorkshopItemId);
        }

        var completedForCurrentCraft = new Dictionary<uint, int>();
        if (snapshot.Current != null && crafts.TryGetValue(snapshot.Current.WorkshopItemId, out var currentCraft))
        {
            workshopItemIds.Add(snapshot.Current.WorkshopItemId);

            for (var i = 0; i < snapshot.Current.PhasesComplete && i < currentCraft.Phases.Count; ++i)
            {
                foreach (var item in currentCraft.Phases[i].Items)
                    AddMaterial(completedForCurrentCraft, item.ItemId, item.TotalQuantity);
            }

            if (snapshot.Current.PhasesComplete < currentCraft.Phases.Count)
            {
                foreach (var contributed in snapshot.Current.ContributedInPhase)
                    AddMaterial(completedForCurrentCraft, contributed.ItemId, (int)contributed.Quantity);
            }
        }

        foreach (var workshopItemId in workshopItemIds)
        {
            if (!crafts.TryGetValue(workshopItemId, out var craft))
                continue;

            foreach (var phase in craft.Phases)
            {
                foreach (var item in phase.Items)
                    AddMaterial(totals, item.ItemId, item.TotalQuantity);
            }
        }

        var result = new Dictionary<uint, int>();
        foreach (var (itemId, total) in totals)
        {
            completedForCurrentCraft.TryGetValue(itemId, out var completed);
            var remaining = total - completed;
            if (remaining > 0)
                result[itemId] = remaining;
        }

        return result;
    }

    public HashSet<uint> ComputeQueueResultItems(QueueSnapshot snapshot)
    {
        var result = new HashSet<uint>();
        foreach (var workshopItemId in snapshot.Queue.Select(x => x.WorkshopItemId)
                     .Concat(snapshot.Current != null ? [snapshot.Current.WorkshopItemId] : []))
        {
            if (crafts.TryGetValue(workshopItemId, out var craft))
                result.Add(craft.ResultItem);
        }

        return result;
    }

    public string ResolveItemName(uint itemId)
    {
        var row = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(itemId);
        return row?.Name.ToString() ?? $"Item #{itemId}";
    }
}
