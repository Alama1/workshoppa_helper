using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using WorkshoppaHelper.Retainers;
using WorkshoppaHelper.Workshoppa;

namespace WorkshoppaHelper;

public enum RunKind
{
    PullMaterials,
    StashItems,
}

public enum RunStatus
{
    Idle,
    Running,
    Finished,
    Failed,
    Aborted,
}

public sealed class Orchestrator : IDisposable
{
    private enum Step
    {
        PauseWorkshoppa,
        MoveToBell,
        InteractBell,
        SelectRetainer,
        OpenTransfer,
        Transfer,
        CloseRetainer,
        CloseBell,
        MoveToStation,
        ResumeWorkshoppa,
    }

    private readonly Configuration configuration;
    private readonly WorkshoppaBridge bridge;
    private readonly ProjectMaterialsService materials;
    private readonly RetainerInterop retainerInterop;
    private readonly AutoRetainerInterop autoRetainer;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly ITargetManager targetManager;
    private readonly IChatGui chat;
    private readonly IPluginLog log;

    private Step step;
    private RunStatus status = RunStatus.Idle;

    private readonly Stopwatch stepWatch = new();
    private readonly Stopwatch actionWatch = Stopwatch.StartNew();
    private TimeSpan lastAction;
    private readonly Stopwatch escWatch = Stopwatch.StartNew();
    private TimeSpan lastEscPress = TimeSpan.MinValue;
    private bool escWasDown;

    private bool pausedWorkshoppa;
    private bool wasInWorkshop;
    private QueueSnapshot? snapshotAtPause;

    private List<RetainerInterop.RetainerListEntry> retainers = [];
    private int retainerIndex = -1;
    private bool awaitingRetainerOpen;
    private DateTime retainerSelectedAt = DateTime.MinValue;

    private Dictionary<uint, int> queueTotals = new();
    private Dictionary<uint, int> remainingNeeded = new();
    private readonly Dictionary<uint, int> pulled = new();
    private readonly Dictionary<uint, int> deposited = new();
    private string? lastError;
    private string inventoryFingerprint = string.Empty;
    private bool awaitingChange;
    private (uint ItemId, int Quantity, bool ToRetainer) pendingMove;
    private int consecutiveFailures;
    private int pendingQuantity;
    private DateTime? transferLostSince;
    private bool pullStoppedForSlots;

    public Orchestrator(
        Configuration configuration,
        WorkshoppaBridge bridge,
        ProjectMaterialsService materials,
        RetainerInterop retainerInterop,
        AutoRetainerInterop autoRetainer,
        IClientState clientState,
        IObjectTable objectTable,
        ICondition condition,
        ITargetManager targetManager,
        IChatGui chat,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.bridge = bridge;
        this.materials = materials;
        this.retainerInterop = retainerInterop;
        this.autoRetainer = autoRetainer;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.condition = condition;
        this.targetManager = targetManager;
        this.chat = chat;
        this.log = log;
    }

    public RunStatus Status => status;
    public RunKind Kind => kind;
    public string CurrentStep => status == RunStatus.Running ? step.ToString() : status.ToString();
    public string? LastError => lastError;
    public string CurrentRetainer => retainerIndex >= 0 && retainerIndex < retainers.Count ? retainers[retainerIndex].Name : "-";
    public IReadOnlyDictionary<uint, int> PulledThisRun => pulled;
    public IReadOnlyDictionary<uint, int> DepositedThisRun => deposited;

    private RunKind kind;

    public event Action? StateChanged;

    public bool Start(RunKind runKind)
    {
        if (status == RunStatus.Running)
            return false;

        if (objectTable.LocalPlayer == null)
        {
            Fail("you are not logged in");
            return false;
        }

        if (!retainerInterop.NativeCommandAvailable)
        {
            Fail("item move function is unavailable (signature scan failed)");
            return false;
        }

        if (autoRetainer.IsInstalled && autoRetainer.IsBusy)
        {
            Fail("AutoRetainer is currently busy - wait for it to finish");
            return false;
        }

        var snapshot = bridge.ReadQueue();
        if (snapshot == null)
        {
            Fail("Workshoppa queue could not be read (is Workshoppa loaded?)");
            return false;
        }

        if (!materials.IsLoaded)
        {
            Fail("workshop craft data is still loading, try again in a moment");
            return false;
        }

        var queueTotals = materials.ComputeRemainingNeeded(snapshot);
        if (runKind == RunKind.PullMaterials)
        {
            var needed = ComputeRemainingAfterBags(queueTotals);
            if (needed.Count == 0)
            {
                Fail("nothing to pull - you already have every material for the current queue");
                return false;
            }

            this.queueTotals = queueTotals;
            remainingNeeded = needed;
        }
        else
        {
            if (snapshot.Current == null && snapshot.Queue.Count == 0)
            {
                Fail("the Workshoppa queue is empty - stash-all would empty your bags; queue a craft first");
                return false;
            }

            if (!HasAnythingExcept(queueTotals.Keys))
            {
                Fail("nothing to stash - your bags only contain materials the queue still needs");
                return false;
            }

            // Deposit protection uses every queue-material key, regardless of current bag counts.
            this.queueTotals = queueTotals;
            remainingNeeded = queueTotals;
        }
        kind = runKind;
        pulled.Clear();
        deposited.Clear();
        awaitingChange = false;
        consecutiveFailures = 0;
        transferLostSince = null;
        pullStoppedForSlots = false;
        retainerIndex = -1;
        retainers = [];
        awaitingRetainerOpen = false;
        pausedWorkshoppa = false;
        snapshotAtPause = snapshot;
        wasInWorkshop = RetainerInterop.WorkshopTerritories.Contains(clientState.TerritoryType);
        lastError = null;

        if (configuration.SuppressAutoRetainer && autoRetainer.IsInstalled)
            autoRetainer.SetSuppressed(true);

        status = RunStatus.Running;
        SetStep(Step.PauseWorkshoppa);
        if (kind == RunKind.PullMaterials)
        {
            var top = string.Join(", ", remainingNeeded.OrderByDescending(kv => kv.Value).Take(10)
                .Select(kv => $"{kv.Value}x {materials.ResolveItemName(kv.Key)}(#{kv.Key})"));
            Announce($"pulling {remainingNeeded.Values.Sum()} items still missing: {top}");
        }
        else
        {
            Announce("stashing items into retainers...");
        }
        return true;
    }

    private static Dictionary<uint, int> ComputeRemainingAfterBags(Dictionary<uint, int> totals)
    {
        var result = new Dictionary<uint, int>(totals);
        foreach (var (itemId, owned) in RetainerInterop.CountPlayerItems())
        {
            if (!result.TryGetValue(itemId, out var missing))
                continue;

            if (owned >= missing)
                result.Remove(itemId);
            else
                result[itemId] = missing - owned;
        }

        return result;
    }

    private static bool HasAnythingExcept(IEnumerable<uint> protectedItemIds)
    {
        var protectedSet = new HashSet<uint>(protectedItemIds);
        var hasAny = false;
        RetainerInterop.WalkPlayerSlots((itemId, _, _) =>
        {
            if (!hasAny && !protectedSet.Contains(itemId))
                hasAny = true;
        });
        return hasAny;
    }

    public void Abort(string reason)
    {
        if (status != RunStatus.Running)
            return;

        CleanupMovement();
        TryResumeWorkshoppa();
        RestoreAutoRetainer();

        status = RunStatus.Aborted;
        lastError = reason;
        Announce($"aborted: {reason}");
        StateChanged?.Invoke();
    }

    private void RestoreAutoRetainer()
    {
        if (configuration.SuppressAutoRetainer && autoRetainer.IsInstalled)
            autoRetainer.SetSuppressed(false);
    }

    private void Finish()
    {
        CleanupMovement();
        RestoreAutoRetainer();

        var summary = kind == RunKind.PullMaterials ? Describe(pulled) : Describe(deposited);
        if (kind == RunKind.PullMaterials)
        {
            var stillMissing = ComputeRemainingAfterBags(queueTotals);
            if (pullStoppedForSlots)
                summary += $". Stopped early - bags nearly full; still missing {stillMissing.Values.Sum()}";
            else if (stillMissing.Count > 0)
                summary += $". Still missing {stillMissing.Values.Sum()} (not found in retainers)";
        }

        status = RunStatus.Finished;
        Announce($"done. {summary}");
        StateChanged?.Invoke();
    }

    private void Fail(string reason)
    {
        if (status == RunStatus.Running)
        {
            CleanupMovement();
            TryResumeWorkshoppa();
            RestoreAutoRetainer();
        }

        status = RunStatus.Failed;
        lastError = reason;
        Announce($"failed: {reason}");
        StateChanged?.Invoke();
    }

    private string Describe(Dictionary<uint, int> items) => items.Count == 0
        ? "nothing was moved"
        : string.Join(", ", items.Select(kv => $"{kv.Value}x {materials.ResolveItemName(kv.Key)}"));

    public void Tick(bool escapeDown)
    {
        if (status != RunStatus.Running)
            return;

        if (escapeDown && !escWasDown)
        {
            if (escWatch.Elapsed - lastEscPress < TimeSpan.FromMilliseconds(500))
            {
                Abort("Escape pressed twice");
                return;
            }

            lastEscPress = escWatch.Elapsed;
        }

        escWasDown = escapeDown;

        try
        {
            TickInternal();
        }
        catch (Exception e)
        {
            log.Error(e, $"[WorkshoppaHelper] Error in step {step}");
            Fail($"unexpected error in step {step}: {e.Message}");
        }
    }

    private bool ActionReady(int minMs = 250)
    {
        if (actionWatch.Elapsed - lastAction < TimeSpan.FromMilliseconds(minMs))
            return false;

        lastAction = actionWatch.Elapsed;
        return true;
    }

    private void TickInternal()
    {
        if (objectTable.LocalPlayer == null)
        {
            Abort("logged out");
            return;
        }

        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
            return;

        switch (step)
        {
            case Step.PauseWorkshoppa: TickPauseWorkshoppa(); break;
            case Step.MoveToBell: TickMoveToBell(); break;
            case Step.InteractBell: TickInteractBell(); break;
            case Step.SelectRetainer: TickSelectRetainer(); break;
            case Step.OpenTransfer: TickOpenTransfer(); break;
            case Step.Transfer: TickTransfer(); break;
            case Step.CloseRetainer: TickCloseRetainer(); break;
            case Step.CloseBell: TickCloseBell(); break;
            case Step.MoveToStation: TickMoveToStation(); break;
            case Step.ResumeWorkshoppa: TickResumeWorkshoppa(); break;
        }
    }

    private void SetStep(Step next)
    {
        step = next;
        stepWatch.Restart();
        StateChanged?.Invoke();
        log.Information($"[WorkshoppaHelper] Step: {step} (retainer {CurrentRetainer}, {RetainerInterop.CountFreeSlots()} free slots)");
    }

    private bool StepTimedOut(int seconds, string message)
    {
        if (stepWatch.Elapsed.TotalSeconds <= seconds)
            return false;

        Fail(message);
        return true;
    }

    private void TickPauseWorkshoppa()
    {
        if (!wasInWorkshop)
        {
            SetStep(Step.MoveToBell);
            return;
        }

        if (!bridge.IsAutomationRunning)
        {
            SetStep(Step.MoveToBell);
            return;
        }

        // Workshoppa only processes Pause/Resume next to the fabrication station. If we are not at
        // the station it cannot turn anything in anyway, so there is nothing to pause.
        retainerInterop.FindNearestFabricationStation(out var stationDistance);
        if (stationDistance > 3f)
        {
            log.Information($"[WorkshoppaHelper] Not at the fabrication station ({stationDistance:F1}y); skipping Workshoppa pause");
            SetStep(Step.MoveToBell);
            return;
        }

        if (!pausedWorkshoppa)
        {
            if (!ActionReady())
                return;

            if (!bridge.Pause())
            {
                Fail("could not pause Workshoppa (bridge unavailable)");
                return;
            }

            pausedWorkshoppa = true;
            snapshotAtPause = bridge.ReadQueue() ?? snapshotAtPause;
            Announce("paused Workshoppa");
            return;
        }

        // Workshoppa processes the pause on its next framework tick.
        if (bridge.IsAutomationRunning && !StepTimedOut(configuration.AddonTimeoutSeconds, "Workshoppa did not pause in time"))
            return;

        SetStep(Step.MoveToBell);
    }

    private void CleanupMovement()
    {
        RetainerInterop.ExecuteChatCommand("/automove off");
        RetainerInterop.ExecuteChatCommand("/lockon off");
    }

    private void TickMoveToBell()
    {
        var bell = retainerInterop.FindNearestRetainerBell(out var distance);
        if (bell == null || distance > 30f)
        {
            Fail("no retainer bell found within 30y - place one in your workshop estate or move closer");
            return;
        }

        if (distance < 4f)
        {
            CleanupMovement();
            SetStep(Step.InteractBell);
            return;
        }

        if (StepTimedOut(configuration.MovementTimeoutSeconds, $"could not reach the retainer bell (still {distance:F1}y away)"))
            return;

        if (!ActionReady(200))
            return;

        // Compare by address: ObjectTable may hand out fresh wrappers per access.
        if (targetManager.Target == null || targetManager.Target.Address != bell.Address)
        {
            targetManager.Target = bell;
            return;
        }

        RetainerInterop.ExecuteChatCommand("/lockon on");
        RetainerInterop.ExecuteChatCommand("/automove on");
    }

    private void TickInteractBell()
    {
        if (retainerInterop.IsRetainerListVisible)
        {
            retainers = retainerInterop.ReadRetainerList()
                .Where(r => r.IsActive && !configuration.ExcludedRetainers.Contains(r.Name, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (retainers.Count == 0)
            {
                Fail("no eligible retainers available");
                return;
            }

            log.Information($"[WorkshoppaHelper] Retainer list open, {retainers.Count} eligible retainers");
            SetStep(Step.SelectRetainer);
            return;
        }

        if (retainerInterop.IsAddonVisible("RetainerTaskResult") || retainerInterop.IsAddonVisible("RetainerTaskAsk"))
        {
            Fail("a retainer venture result is pending - finish it manually first");
            return;
        }

        if (StepTimedOut(configuration.AddonTimeoutSeconds, "retainer list did not open"))
            return;

        if (!ActionReady(500))
            return;

        var bell = retainerInterop.FindNearestRetainerBell(out var distance);
        if (bell != null && distance < 6f)
            RetainerInterop.Interact(bell);
    }

    private void TickSelectRetainer()
    {
        if (awaitingRetainerOpen)
        {
            if (retainerInterop.IsRetainerMenuVisible)
            {
                awaitingRetainerOpen = false;
                SetStep(Step.OpenTransfer);
                return;
            }

            if (retainerInterop.IsAddonVisible("RetainerTaskResult") || retainerInterop.IsAddonVisible("RetainerTaskAsk"))
            {
                Fail($"retainer {CurrentRetainer} has a pending venture result - finish it manually first");
                return;
            }

            StepTimedOut(configuration.AddonTimeoutSeconds, $"retainer {CurrentRetainer} did not open");
            return;
        }

        if (retainerInterop.IsRetainerListVisible)
        {
            if (kind == RunKind.PullMaterials && (remainingNeeded.Count == 0 || pullStoppedForSlots))
            {
                SetStep(Step.CloseBell);
                return;
            }

            if (kind == RunKind.StashItems && !HasDepositableItems())
            {
                SetStep(Step.CloseBell);
                return;
            }

            if (retainerIndex >= retainers.Count - 1)
            {
                if (kind == RunKind.StashItems)
                {
                    Fail("all retainers were tried but items remain - are your retainers full?");
                    return;
                }

                SetStep(Step.CloseBell);
                return;
            }

            if (!ActionReady())
                return;

            retainerIndex++;
            consecutiveFailures = 0;
            if (retainerInterop.SelectRetainer(retainers[retainerIndex].Index))
            {
                awaitingRetainerOpen = true;
                retainerSelectedAt = DateTime.UtcNow;
                log.Information($"[WorkshoppaHelper] Selected retainer {retainers[retainerIndex].Name}");
            }
            return;
        }

        StepTimedOut(configuration.AddonTimeoutSeconds, "the retainer list did not appear");
    }

    private void TickOpenTransfer()
    {
        // The retainer's inventory containers stream in after the window opens; do not start
        // moving items until they are all loaded, or FindRetainerSlot sees an empty retainer.
        if (retainerInterop.IsTransferReady && RetainerInterop.AreRetainerContainersLoaded)
        {
            var (loadedPages, stacks) = RetainerInterop.DescribeRetainerInventory();
            var presentTypes = new HashSet<uint>();
            RetainerInterop.WalkRetainerSlots((itemId, _, _) =>
            {
                if (remainingNeeded.ContainsKey(itemId))
                    presentTypes.Add(itemId);
            });
            log.Information(
                $"[WorkshoppaHelper] {CurrentRetainer}: {stacks} stacks across {loadedPages}/7 loaded pages; " +
                $"{presentTypes.Count} of {remainingNeeded.Count} needed item types present");

            awaitingChange = false;
            SetStep(Step.Transfer);
            return;
        }

        if (retainerInterop.IsRetainerMenuVisible)
        {
            // Give the retainer's inventory containers a moment to settle before the first click.
            if ((DateTime.UtcNow - retainerSelectedAt).TotalMilliseconds < 600)
                return;

            if (ActionReady())
                retainerInterop.OpenTransferWindow();
            return;
        }

        StepTimedOut(configuration.AddonTimeoutSeconds, "transfer window did not open");
    }

    private void TickTransfer()
    {
        if (!retainerInterop.IsTransferReady)
        {
            // The window can briefly disappear between moves; only fail when it stalls or was closed.
            transferLostSince ??= DateTime.UtcNow;
            if ((DateTime.UtcNow - transferLostSince.Value).TotalSeconds > configuration.AddonTimeoutSeconds)
            {
                Fail("transfer window closed unexpectedly");
                return;
            }

            return;
        }

        transferLostSince = null;

        if (retainerInterop.IsInputNumericVisible)
        {
            if (!awaitingChange || pendingQuantity <= 0)
            {
                Fail("an unexpected quantity popup is open - close it and retry");
                return;
            }

            if (ActionReady())
                retainerInterop.TryConfirmInputNumeric(pendingQuantity);
            return;
        }

        if (awaitingChange)
        {
            if (CaptureFingerprint() != inventoryFingerprint)
            {
                awaitingChange = false;
                consecutiveFailures = 0;
                RecordMove();
                return;
            }

            if (stepWatch.Elapsed.TotalSeconds > 5)
            {
                awaitingChange = false;
                consecutiveFailures++;
                if (consecutiveFailures >= 3)
                {
                    if (kind == RunKind.StashItems)
                    {
                        log.Warning("[WorkshoppaHelper] Retainer appears full, moving on");
                        SetStep(Step.CloseRetainer);
                        return;
                    }

                    Fail($"item moves kept failing for {CurrentRetainer} (player inventory full?)");
                    return;
                }
            }

            return;
        }

        if (!ActionReady())
            return;

        if (kind == RunKind.PullMaterials)
            TickWithdraw();
        else
            TickDeposit();
    }

    private void TickWithdraw()
    {
        // Always recompute from the original queue totals minus current bag contents: this
        // accounts for pre-stocked bags AND shrinks automatically as withdrawals land.
        remainingNeeded = ComputeRemainingAfterBags(queueTotals);
        if (remainingNeeded.Count == 0)
        {
            SetStep(Step.CloseRetainer);
            return;
        }

        if (RetainerInterop.CountFreeSlots() <= configuration.PullReserveSlots)
        {
            Announce($"inventory nearly full (reserve {configuration.PullReserveSlots} slots), stopping pulls");
            pullStoppedForSlots = true;
            SetStep(Step.CloseRetainer);
            return;
        }

        var slot = RetainerInterop.FindRetainerSlot((itemId, _) => remainingNeeded.ContainsKey(itemId));
        if (slot == null)
        {
            log.Debug($"[WorkshoppaHelper] {CurrentRetainer} has none of the remaining materials");
            SetStep(Step.CloseRetainer);
            return;
        }

        var needed = remainingNeeded[slot.ItemId];
        var quantity = Math.Min(needed, slot.Quantity);

        inventoryFingerprint = CaptureFingerprint();
        pendingQuantity = quantity;
        pendingMove = (slot.ItemId, quantity, false);

        var command = quantity >= slot.Quantity ? RetainerItemCommand.RetrieveFromRetainer : RetainerItemCommand.RetrieveQuantity;
        log.Information($"[WorkshoppaHelper] Withdraw {quantity}/{needed} x {materials.ResolveItemName(slot.ItemId)} (retainer stack {slot.Quantity}, page {slot.Type - InventoryType.RetainerPage1 + 1} slot {slot.Slot}) via {command}");

        retainerInterop.MoveItem(
            slot.Slot,
            slot.Type,
            command);

        awaitingChange = true;
        stepWatch.Restart();
    }

    private void TickDeposit()
    {
        var slot = RetainerInterop.FindPlayerSlot((itemId, _, _) => IsDepositable(itemId));
        if (slot == null)
        {
            SetStep(Step.CloseRetainer);
            return;
        }

        // Prefer topping up an existing partial stack in this retainer so the same item does
        // not get duplicated into a second stack across retainers.
        var stackSize = retainerInterop.GetItemStackSize(slot.ItemId);
        var partial = RetainerInterop.FindRetainerPartialStack(slot.ItemId, stackSize);

        inventoryFingerprint = CaptureFingerprint();

        int quantity;
        RetainerItemCommand command;
        if (partial != null)
        {
            var room = stackSize - partial.Quantity;
            quantity = Math.Min(slot.Quantity, room);
            command = RetainerItemCommand.EntrustQuantity;
            log.Information($"[WorkshoppaHelper] Topping up {quantity}x {materials.ResolveItemName(slot.ItemId)} into {CurrentRetainer}'s stack ({partial.Quantity}/{stackSize}, page {partial.Type - InventoryType.RetainerPage1 + 1} slot {partial.Slot})");
        }
        else
        {
            quantity = slot.Quantity;
            command = RetainerItemCommand.EntrustToRetainer;
            log.Information($"[WorkshoppaHelper] Depositing {quantity}x {materials.ResolveItemName(slot.ItemId)} (new stack, page {slot.Type - InventoryType.Inventory1 + 1} slot {slot.Slot})");
        }

        pendingQuantity = quantity;
        pendingMove = (slot.ItemId, quantity, true);

        retainerInterop.MoveItem(slot.Slot, slot.Type, command);

        awaitingChange = true;
        stepWatch.Restart();
    }

    /// <summary>Materials still needed by the remaining queue stay in the bags; everything else goes.</summary>
    private bool IsDepositable(uint itemId)
    {
        if (remainingNeeded.ContainsKey(itemId))
            return false;

        return !(configuration.RespectProtectionList && autoRetainer.IsInstalled && autoRetainer.IsItemProtected(itemId));
    }

    private bool HasDepositableItems()
    {
        var hasAny = false;
        RetainerInterop.WalkPlayerSlots((itemId, _, _) =>
        {
            if (!hasAny && IsDepositable(itemId))
                hasAny = true;
        });
        return hasAny;
    }

    private void RecordMove()
    {
        var (itemId, quantity, toRetainer) = pendingMove;
        var target = toRetainer ? deposited : pulled;
        target.TryGetValue(itemId, out var existing);
        target[itemId] = existing + quantity;
    }

    private string CaptureFingerprint()
    {
        var hash = new HashCode();
        RetainerInterop.WalkPlayerSlots((itemId, quantity, hq) =>
        {
            hash.Add(itemId);
            hash.Add(quantity);
            hash.Add(hq);
        });
        RetainerInterop.WalkRetainerSlots((itemId, quantity, hq) =>
        {
            hash.Add(itemId);
            hash.Add(quantity);
            hash.Add(hq);
        });
        return hash.ToHashCode().ToString();
    }

    private void TickCloseRetainer()
    {
        if (retainerInterop.IsTransferWindowVisible || retainerInterop.IsRetainerMenuVisible)
        {
            // Keep dismissing until the session actually ends; fail instead of looping forever.
            if (ActionReady(300))
                retainerInterop.CloseRetainerSession(log);

            StepTimedOut(configuration.AddonTimeoutSeconds, $"could not close the session with {CurrentRetainer}");
            return;
        }

        if (retainerInterop.IsRetainerListVisible)
        {
            awaitingRetainerOpen = false;
            SetStep(Step.SelectRetainer);
            return;
        }

        if (StepTimedOut(configuration.AddonTimeoutSeconds, "retainer session closed but the retainer list did not return"))
            return;
    }

    private void TickCloseBell()
    {
        if (retainerInterop.IsRetainerListVisible)
        {
            if (ActionReady())
                retainerInterop.CloseRetainerList();
            return;
        }

        if (kind == RunKind.StashItems && HasDepositableItems())
        {
            Fail("items remain in the inventory but no retainer could take them");
            return;
        }

        if (wasInWorkshop && pausedWorkshoppa)
        {
            SetStep(Step.MoveToStation);
            return;
        }

        Finish();
    }

    private void TickMoveToStation()
    {
        var station = retainerInterop.FindNearestFabricationStation(out var distance);
        if (station == null)
        {
            Fail("fabrication station not found - walk back and resume Workshoppa manually with /ws");
            return;
        }

        if (distance < 3f)
        {
            CleanupMovement();
            targetManager.Target = null;
            SetStep(Step.ResumeWorkshoppa);
            return;
        }

        if (StepTimedOut(configuration.MovementTimeoutSeconds, $"could not reach the fabrication station (still {distance:F1}y away)"))
            return;

        if (!ActionReady(200))
            return;

        // Compare by address: ObjectTable may hand out fresh wrappers per access.
        if (targetManager.Target == null || targetManager.Target.Address != station.Address)
        {
            targetManager.Target = station;
            return;
        }

        RetainerInterop.ExecuteChatCommand("/lockon on");
        RetainerInterop.ExecuteChatCommand("/automove on");
    }

    private void TickResumeWorkshoppa()
    {
        if (!pausedWorkshoppa)
        {
            Finish();
            return;
        }

        var snapshot = bridge.ReadQueue();
        if (snapshot == null || (snapshot.Current == null && snapshot.Queue.Count == 0))
        {
            Finish();
            return;
        }

        if (bridge.Resume(snapshot))
        {
            pausedWorkshoppa = false;
            Announce("resumed Workshoppa");
            Finish();
            return;
        }

        Fail("could not resume Workshoppa - resume it manually with /ws");
    }

    private void TryResumeWorkshoppa()
    {
        if (!pausedWorkshoppa)
            return;

        var snapshot = bridge.ReadQueue() ?? snapshotAtPause;
        if (snapshot != null)
            bridge.Resume(snapshot);

        pausedWorkshoppa = false;
    }

    private void Announce(string message)
    {
        if (configuration.VerboseChat)
            chat.Print($"[WorkshoppaHelper] {message}");

        log.Information($"[WorkshoppaHelper] {message}");
    }

    public void Dispose()
    {
        if (status == RunStatus.Running)
            Abort("plugin disposing");
    }
}
