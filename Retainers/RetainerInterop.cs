using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace WorkshoppaHelper.Retainers;

/// <summary>
/// Item move commands for the retainer transfer window, mirroring AutoRetainer's
/// Internal/InventoryManagement/RetainerItemCommand.cs values.
/// </summary>
public enum RetainerItemCommand : long
{
    RetrieveFromRetainer = 0,
    EntrustToRetainer = 1,
    RetrieveQuantity = 3,
    EntrustQuantity = 4,
}

/// <summary>Unsafe, framework-thread game interop used by the retainer automation.</summary>
public sealed unsafe class RetainerInterop : IDisposable
{
    private const string RetainerItemCommandSignature =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 30 48 8B 5C 24 ?? 41 8B F0";

    private const string RetainerListAddon = "RetainerList";
    private const string SelectStringAddon = "SelectString";
    private const string InputNumericAddon = "InputNumeric";
    private const string RetainerInventoryLargeAddon = "InventoryRetainerLarge";
    private const string RetainerInventorySmallAddon = "InventoryRetainer";

    public static readonly IReadOnlyList<uint> WorkshopTerritories = new uint[] { 423, 424, 425, 653, 984 };

    private static readonly uint[] FabricationStationIds = [2005236, 2005238, 2005240, 2007821, 2011588];

    public static readonly InventoryType[] PlayerBags =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    public static readonly InventoryType[] RetainerPages =
    [
        InventoryType.RetainerPage1,
        InventoryType.RetainerPage2,
        InventoryType.RetainerPage3,
        InventoryType.RetainerPage4,
        InventoryType.RetainerPage5,
        InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void RetainerItemCommandDelegate(nint agentModule, uint slot, InventoryType inventoryType, uint a4, RetainerItemCommand command);

    private readonly IGameGui gameGui;
    private readonly IDataManager dataManager;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;
    private readonly ISigScanner sigScanner;

    private RetainerItemCommandDelegate? retainerItemCommand;

    public RetainerInterop(
        IGameGui gameGui,
        IDataManager dataManager,
        IObjectTable objectTable,
        IPluginLog log,
        ISigScanner sigScanner)
    {
        this.gameGui = gameGui;
        this.dataManager = dataManager;
        this.objectTable = objectTable;
        this.log = log;
        this.sigScanner = sigScanner;

        TryResolveNativeCommand();
    }

    public bool NativeCommandAvailable => retainerItemCommand != null;

    private void TryResolveNativeCommand()
    {
        try
        {
            // TryScanText resolves a .text function start (same as AutoRetainer's hook scan).
            // NOTE: TryGetStaticAddressFromSig would follow a RIP-relative displacement and
            // return a garbage address - calling it crashes the game with 0xc000001d.
            if (sigScanner.TryScanText(RetainerItemCommandSignature, out var address))
            {
                retainerItemCommand = Marshal.GetDelegateForFunctionPointer<RetainerItemCommandDelegate>(address);
                log.Information($"[WorkshoppaHelper] Resolved RetainerItemCommand native function at 0x{address:X}");
            }
            else
            {
                log.Warning("[WorkshoppaHelper] Could not resolve RetainerItemCommand signature; item moves unavailable");
            }
        }
        catch (Exception e)
        {
            log.Warning(e, "[WorkshoppaHelper] RetainerItemCommand resolution failed");
        }
    }

    private AtkUnitBase* GetAddon(string name)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(name, 1);
        return addon != null && addon->IsReady && addon->IsVisible ? addon : null;
    }

    public bool IsAddonVisible(string name) => GetAddon(name) != null;

    public bool IsRetainerListVisible => GetAddon(RetainerListAddon) != null;

    public bool IsRetainerMenuVisible
    {
        get
        {
            var addon = gameGui.GetAddonByName<AddonSelectString>(SelectStringAddon, 1);
            return addon != null && addon->AtkUnitBase.IsReady && addon->AtkUnitBase.IsVisible;
        }
    }

    public bool IsTransferWindowVisible => GetAddon(RetainerInventoryLargeAddon) != null || GetAddon(RetainerInventorySmallAddon) != null;

    /// <summary>Whether the retainer transfer window is usable: addon visible and retainer agent active
    /// (same predicate AutoRetainer uses; container load state is checked per page on read).</summary>
    public bool IsTransferReady =>
        IsTransferWindowVisible &&
        AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer)->IsAgentActive();

    private static nint AgentRetainerItemCommandModule =>
        (nint)AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer) + 40;

    public static bool AreRetainerContainersLoaded
    {
        get
        {
            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null)
                return false;

            foreach (var type in RetainerPages)
            {
                var container = inventoryManager->GetInventoryContainer(type);
                if (container == null || !container->IsLoaded)
                    return false;
            }

            return true;
        }
    }

    public static (int LoadedPages, int Stacks) DescribeRetainerInventory()
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return (0, 0);

        var loadedPages = 0;
        var stacks = 0;
        foreach (var type in RetainerPages)
        {
            var container = inventoryManager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                continue;

            loadedPages++;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot != null && slot->ItemId != 0)
                    stacks++;
            }
        }

        return (loadedPages, stacks);
    }

    private string GetEntrustOrWithdrawText()
    {
        var row = dataManager.GetExcelSheet<Addon>().GetRow(2378);
        return row.Text.ExtractText();
    }

    private static string NormalizeEntry(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (char.IsControl(character) || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.PrivateUse)
                continue;

            builder.Append(character);
        }

        var normalized = builder.ToString().Trim();
        var detailStart = normalized.IndexOf(" (", StringComparison.Ordinal);
        if (detailStart >= 0)
            normalized = normalized[..detailStart].TrimEnd();

        return normalized.TrimEnd('.');
    }

    private unsafe List<string> GetSelectStringEntries(out int entrustOrWithdrawIndex)
    {
        entrustOrWithdrawIndex = -1;
        var entries = new List<string>();
        var addon = gameGui.GetAddonByName<AddonSelectString>(SelectStringAddon, 1);
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
            return entries;

        var popup = addon->PopupMenu.PopupMenu;
        var target = NormalizeEntry(GetEntrustOrWithdrawText());
        for (var i = 0; i < popup.EntryCount; i++)
        {
            var entry = popup.EntryNames[i].ToString();
            entries.Add(entry ?? string.Empty);
            if (entrustOrWithdrawIndex < 0 && !string.IsNullOrEmpty(entry) &&
                NormalizeEntry(entry).StartsWith(target, StringComparison.OrdinalIgnoreCase))
            {
                entrustOrWithdrawIndex = i;
            }
        }

        return entries;
    }

    /// <summary>Click "Entrust or withdraw items" on the retainer command menu. True when clicked.</summary>
    public bool OpenTransferWindow()
    {
        var addon = gameGui.GetAddonByName<AddonSelectString>(SelectStringAddon, 1);
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
            return false;

        var entries = GetSelectStringEntries(out var index);
        if (index < 0)
            return false;

        addon->AtkUnitBase.FireCallbackInt(index);
        return true;
    }

    public bool TryConfirmInputNumeric(int requestedQuantity)
    {
        var addon = GetAddon(InputNumericAddon);
        if (addon == null || addon->AtkValuesCount <= 3 || addon->AtkValues == null)
            return false;

        var maxAmount = addon->AtkValues[3].UInt;
        var result = Math.Clamp(requestedQuantity, 1, (int)Math.Max(1, maxAmount));

        var values = stackalloc AtkValue[1];
        values[0].Type = AtkValueType.Int;
        values[0].Int = result;
        addon->FireCallback(1, values, true);
        return true;
    }

    public bool IsInputNumericVisible => GetAddon(InputNumericAddon) != null;

    public sealed record RetainerListEntry(int Index, string Name, bool IsActive);

    public List<RetainerListEntry> ReadRetainerList()
    {
        var result = new List<RetainerListEntry>();
        var addon = GetAddon(RetainerListAddon);
        if (addon == null || addon->AtkValues == null)
            return result;

        // Mirrors ECommons ReaderRetainerList: entries start at index 3, 10 values each, max 10.
        const int start = 3;
        const int stride = 10;
        for (var i = 0; i < 10; i++)
        {
            var baseIndex = start + i * stride;
            if (baseIndex + 8 >= addon->AtkValuesCount)
                break;

            var nameValue = addon->AtkValues[baseIndex];
            if (nameValue.Type != AtkValueType.String)
                continue;

            var name = nameValue.String.ToString();
            if (string.IsNullOrEmpty(name))
                continue;

            result.Add(new RetainerListEntry(i, name!, addon->AtkValues[baseIndex + 8].Bool));
        }

        return result;
    }

    public bool SelectRetainer(int index)
    {
        var addon = GetAddon(RetainerListAddon);
        if (addon == null)
            return false;

        var values = stackalloc AtkValue[4];
        values[0].Type = AtkValueType.Int;
        values[0].Int = 2;
        values[1].Type = AtkValueType.UInt;
        values[1].UInt = (uint)index;
        values[2].Type = AtkValueType.Int;
        values[2].Int = 0;
        values[3].Type = AtkValueType.Int;
        values[3].Int = 0;
        addon->FireCallback(4, values, true);
        return true;
    }

    public bool CloseRetainerList()
    {
        var addon = GetAddon(RetainerListAddon);
        if (addon == null)
            return false;

        var values = stackalloc AtkValue[1];
        values[0].Type = AtkValueType.Int;
        values[0].Int = -1;
        addon->FireCallback(1, values, false);
        return true;
    }

    /// <summary>
    /// Ends the active retainer session. Two stages, because the retainer agent's Hide()
    /// does not dismiss the open transfer windows:
    /// 1. close InventoryRetainer(Large) explicitly (same as InventoryReporter does),
    /// 2. once they are gone, Hide() the agent (closes the menu, dismisses the retainer).
    /// </summary>
    public void CloseRetainerSession(IPluginLog log)
    {
        var large = gameGui.GetAddonByName<AtkUnitBase>(RetainerInventoryLargeAddon, 1);
        var small = gameGui.GetAddonByName<AtkUnitBase>(RetainerInventorySmallAddon, 1);
        var largeVisible = large != null && large->IsReady && large->IsVisible;
        var smallVisible = small != null && small->IsReady && small->IsVisible;

        if (largeVisible)
            large->Close(true);
        if (smallVisible)
            small->Close(true);

        if (!largeVisible && !smallVisible)
        {
            var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer);
            log.Debug($"[WorkshoppaHelper] Closing retainer session: agent active={agent->IsAgentActive()}, menu visible={IsRetainerMenuVisible}");
            agent->Hide();
        }
        else
        {
            log.Information($"[WorkshoppaHelper] Closing transfer window (large={largeVisible}, small={smallVisible})");
        }
    }

    public void MoveItem(uint slot, InventoryType inventoryType, RetainerItemCommand command)
    {
        if (retainerItemCommand == null)
            throw new InvalidOperationException("RetainerItemCommand was not resolved.");

        retainerItemCommand(AgentRetainerItemCommandModule, slot, inventoryType, 0, command);
    }

    public static int CountFreeSlots()
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return 0;

        var free = 0;
        foreach (var type in PlayerBags)
        {
            var container = inventoryManager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0)
                    free++;
            }
        }

        return free;
    }

    public static Dictionary<uint, int> CountPlayerItems(bool includeHq = true)
    {
        var counts = new Dictionary<uint, int>();
        WalkPlayerSlots((itemId, quantity, _) =>
        {
            counts.TryGetValue(itemId, out var existing);
            counts[itemId] = existing + quantity;
        }, includeHq);
        return counts;
    }

    public static void WalkPlayerSlots(Action<uint, int, bool> visit, bool includeHq = true)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return;

        foreach (var type in PlayerBags)
        {
            var container = inventoryManager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0)
                    continue;

                var hq = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                if (hq && !includeHq)
                    continue;

                visit(slot->ItemId, slot->Quantity, hq);
            }
        }
    }

    public static void WalkRetainerSlots(Action<uint, int, bool> visit)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return;

        foreach (var type in RetainerPages)
        {
            var container = inventoryManager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0)
                    continue;

                visit(slot->ItemId, slot->Quantity, slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality));
            }
        }
    }

    public sealed record InventorySlotRef(InventoryType Type, uint Slot, uint ItemId, int Quantity);

    public int GetItemStackSize(uint itemId)
    {
        try
        {
            var row = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(itemId);
            return row != null && row.Value.StackSize > 0 ? (int)Math.Min(row.Value.StackSize, 9999) : 999;
        }
        catch
        {
            return 999;
        }
    }

    /// <summary>
    /// Finds an existing retainer stack of itemId with room left, so deposits top up partial
    /// stacks instead of creating duplicates.
    /// </summary>
    public static InventorySlotRef? FindRetainerPartialStack(uint itemId, int stackSize)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return null;

        foreach (var type in RetainerPages)
        {
            var container = inventoryManager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId != itemId || slot->Quantity >= stackSize)
                    continue;

                return new InventorySlotRef(type, (uint)i, itemId, slot->Quantity);
            }
        }

        return null;
    }

    public static InventorySlotRef? FindRetainerSlot(Func<uint, int, bool> filter)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return null;

        foreach (var type in RetainerPages)
        {
            var container = inventoryManager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0)
                    continue;

                if (filter(slot->ItemId, slot->Quantity))
                    return new InventorySlotRef(type, (uint)i, slot->ItemId, slot->Quantity);
            }
        }

        return null;
    }

    public static InventorySlotRef? FindPlayerSlot(Func<uint, int, bool, bool> filter)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return null;

        foreach (var type in PlayerBags)
        {
            var container = inventoryManager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0)
                    continue;

                var hq = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                if (filter(slot->ItemId, slot->Quantity, hq))
                    return new InventorySlotRef(type, (uint)i, slot->ItemId, slot->Quantity);
            }
        }

        return null;
    }

    private string GetBellName()
    {
        var row = dataManager.GetExcelSheet<EObjName>().GetRow(2000401);
        return row.Singular.ExtractText();
    }

    public IGameObject? FindNearestRetainerBell(out float distance)
    {
        distance = float.MaxValue;
        IGameObject? nearest = null;
        var player = objectTable.LocalPlayer;
        if (player == null)
            return null;

        var bellNames = new[] { GetBellName(), "リテイナーベル" };
        foreach (var obj in objectTable)
        {
            if (!obj.IsTargetable || (obj.ObjectKind != ObjectKind.HousingEventObject && obj.ObjectKind != ObjectKind.EventObj))
                continue;

            var name = obj.Name.ToString();
            if (string.IsNullOrEmpty(name) || !bellNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;

            var d = System.Numerics.Vector3.Distance(player.Position, obj.Position);
            if (d < distance)
            {
                distance = d;
                nearest = obj;
            }
        }

        return nearest;
    }

    public IGameObject? FindNearestFabricationStation(out float distance)
    {
        distance = float.MaxValue;
        IGameObject? nearest = null;
        var player = objectTable.LocalPlayer;
        if (player == null)
            return null;

        foreach (var obj in objectTable)
        {
            if (obj.ObjectKind != ObjectKind.EventObj || !FabricationStationIds.Contains(obj.BaseId))
                continue;

            var d = System.Numerics.Vector3.Distance(player.Position, obj.Position);
            if (d < distance)
            {
                distance = d;
                nearest = obj;
            }
        }

        return nearest;
    }

    public static void Interact(IGameObject obj)
    {
        TargetSystem.Instance()->InteractWithObject((GameObject*)obj.Address, false);
    }

    /// <summary>
    /// Executes a chat command (/lockon on, /automove on, ...) by typing it into the chat box,
    /// mirroring ECommons Chat.ExecuteCommand. ICommandManager.ProcessCommand does NOT run
    /// built-in game commands - it only dispatches plugin-registered handlers.
    /// </summary>
    public static unsafe void ExecuteChatCommand(string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        Array.Resize(ref bytes, bytes.Length + 1); // null terminator
        var text = Utf8String.FromSequence(bytes);
        try
        {
            UIModule.Instance()->ProcessChatBoxEntry(text, 0, false);
        }
        finally
        {
            text->Dtor(true);
        }
    }

    public void Dispose()
    {
        retainerItemCommand = null;
    }
}
