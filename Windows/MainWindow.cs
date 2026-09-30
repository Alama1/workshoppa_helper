using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Windowing;
using WorkshoppaHelper.Retainers;
using WorkshoppaHelper.Workshoppa;

namespace WorkshoppaHelper.Windows;

public sealed class MainWindow : Window
{
    private static readonly Vector4 HeaderColor = new(0.42f, 0.77f, 0.94f, 1f);
    private static readonly Vector4 OkColor = new(0.35f, 0.85f, 0.35f, 1f);
    private static readonly Vector4 BadColor = new(0.94f, 0.35f, 0.35f, 1f);
    private static readonly Vector4 WarnColor = new(0.95f, 0.80f, 0.30f, 1f);

    private readonly Configuration configuration;
    private readonly WorkshoppaBridge bridge;
    private readonly ProjectMaterialsService materials;
    private readonly RetainerInterop retainerInterop;
    private readonly AutoRetainerInterop autoRetainer;
    private readonly Orchestrator orchestrator;
    private readonly InventoryMonitor monitor;

    public MainWindow(
        Configuration configuration,
        WorkshoppaBridge bridge,
        ProjectMaterialsService materials,
        RetainerInterop retainerInterop,
        AutoRetainerInterop autoRetainer,
        Orchestrator orchestrator,
        InventoryMonitor monitor)
        : base("Workshoppa Helper###WorkshoppaHelper", ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.configuration = configuration;
        this.bridge = bridge;
        this.materials = materials;
        this.retainerInterop = retainerInterop;
        this.autoRetainer = autoRetainer;
        this.orchestrator = orchestrator;
        this.monitor = monitor;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 200),
            MaximumSize = new Vector2(600, 800),
        };
    }

    public override void Draw()
    {
        DrawStatusSection();
        ImGui.Spacing();
        DrawActionsSection();
        ImGui.Spacing();
        DrawMaterialsSection();
        ImGui.Spacing();
        DrawSettingsSection();
    }

    private void DrawStatusSection()
    {
        ImGui.TextColored(HeaderColor, "Status");
        ImGui.Separator();

        var bridgeOk = bridge.IsAvailable;
        ImGui.Text("Workshoppa:");
        ImGui.SameLine();
        if (bridgeOk)
        {
            var stage = bridge.CurrentStageName ?? "?";
            ImGui.TextColored(stage == "Stopped" ? OkColor : WarnColor, $"linked ({stage})");
        }
        else
        {
            ImGui.TextColored(BadColor, bridge.UnavailableReason);
        }

        ImGui.Text("AutoRetainer:");
        ImGui.SameLine();
        ImGui.TextColored(autoRetainer.IsInstalled ? OkColor : WarnColor, autoRetainer.IsInstalled ? "installed" : "not found (optional)");

        ImGui.Text("Item moves:");
        ImGui.SameLine();
        ImGui.TextColored(retainerInterop.NativeCommandAvailable ? OkColor : BadColor,
            retainerInterop.NativeCommandAvailable ? "ready" : "signature not found");

        ImGui.Text("Free bag slots:");
        ImGui.SameLine();
        var free = monitor.FreeSlots;
        ImGui.TextColored(
            free < 0 ? WarnColor : free <= configuration.FreeSlotThreshold ? BadColor : OkColor,
            free < 0 ? "..." : $"{free} (stash at {configuration.FreeSlotThreshold})");

        if (orchestrator.Status == RunStatus.Running)
        {
            ImGui.TextColored(WarnColor, $"Running: {orchestrator.Kind} / {orchestrator.CurrentStep}");
            ImGui.Text($"  Retainer: {orchestrator.CurrentRetainer}");
        }
        else if (orchestrator.LastError != null)
        {
            ImGui.TextColored(BadColor, $"Last run {orchestrator.Status}: {orchestrator.LastError}");
        }
        else if (orchestrator.Status != RunStatus.Idle)
        {
            ImGui.TextColored(OkColor, $"Last run: {orchestrator.Status}");
        }
    }

    private void DrawActionsSection()
    {
        ImGui.TextColored(HeaderColor, "Actions");
        ImGui.Separator();

        var running = orchestrator.Status == RunStatus.Running;

        ImGui.BeginDisabled(running);
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.BoxOpen, "Pull materials from retainers"))
            orchestrator.Start(RunKind.PullMaterials);
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(running);
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.ArrowDown, "Stash into retainers now"))
            orchestrator.Start(RunKind.StashItems);
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(!running);
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Ban, "Abort"))
            orchestrator.Abort("user request");
        ImGui.EndDisabled();

        if (orchestrator.Status == RunStatus.Running && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("You can also abort by pressing ESC twice.");
    }

    private void DrawMaterialsSection()
    {
        ImGui.TextColored(HeaderColor, "Queue materials");
        ImGui.Separator();

        var snapshot = bridge.ReadQueue();
        if (snapshot == null)
        {
            ImGui.TextWrapped("Workshoppa queue is not available.");
            return;
        }

        if (snapshot.Current == null && snapshot.Queue.Count == 0)
        {
            ImGui.TextWrapped("Workshoppa queue is empty.");
            return;
        }

        if (snapshot.Current != null)
        {
            var started = snapshot.Current.StartedCrafting ? "started" : "not started";
            ImGui.Text($"Current: {ResolveCraftName(snapshot.Current.WorkshopItemId)} ({started}, phase {snapshot.Current.PhasesComplete + 1})");
        }

        if (snapshot.Queue.Count > 0)
        {
            ImGui.Text("Queued:");
            foreach (var queued in snapshot.Queue.Take(5))
                ImGui.Text($"  {queued.Quantity}x {ResolveCraftName(queued.WorkshopItemId)}");
            if (snapshot.Queue.Count > 5)
                ImGui.Text($"  ... and {snapshot.Queue.Count - 5} more");
        }

        ImGui.Spacing();
        var needed = materials.ComputeRemainingNeeded(snapshot);
        if (needed.Count == 0)
        {
            ImGui.TextColored(OkColor, "All materials for the queue are in your bags.");
            return;
        }

        var owned = RetainerInterop.CountPlayerItems();
        ImGui.Text("Missing for queue:");
        if (ImGui.BeginTable("materials", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Need", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Have", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableHeadersRow();

            foreach (var (itemId, quantity) in needed.OrderBy(kv => materials.ResolveItemName(kv.Key)))
            {
                var have = owned.TryGetValue(itemId, out var count) ? count : 0;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(materials.ResolveItemName(itemId));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(quantity.ToString());
                ImGui.TableNextColumn();
                ImGui.TextColored(have >= quantity ? OkColor : have > 0 ? WarnColor : BadColor, have.ToString());
            }

            ImGui.EndTable();
        }
    }

    private string ResolveCraftName(uint workshopItemId) =>
        materials.TryGetCraft(workshopItemId, out var craft) ? craft.Name : $"workshop item #{workshopItemId}";

    private void DrawSettingsSection()
    {
        ImGui.TextColored(HeaderColor, "Settings");
        ImGui.Separator();

        DrawCheckbox("Auto-stash when bags fill up", v => configuration.AutoStashEnabled = v, configuration.AutoStashEnabled);
        DrawIntSlider("Free slot threshold", v => configuration.FreeSlotThreshold = v, configuration.FreeSlotThreshold, 0, 20);
        DrawIntSlider("Pull reserve slots", v => configuration.PullReserveSlots = v, configuration.PullReserveSlots, 0, 20);
        DrawCheckbox("Suppress AutoRetainer during runs", v => configuration.SuppressAutoRetainer = v, configuration.SuppressAutoRetainer);
        DrawCheckbox("Respect AutoRetainer protection list", v => configuration.RespectProtectionList = v, configuration.RespectProtectionList);
        DrawCheckbox("Verbose chat output", v => configuration.VerboseChat = v, configuration.VerboseChat);
        DrawIntSlider("Movement timeout (s)", v => configuration.MovementTimeoutSeconds = v, configuration.MovementTimeoutSeconds, 15, 120);
        DrawIntSlider("Addon timeout (s)", v => configuration.AddonTimeoutSeconds = v, configuration.AddonTimeoutSeconds, 5, 60);
    }

    private void DrawCheckbox(string label, Action<bool> set, bool current)
    {
        if (ImGui.Checkbox(label, ref current))
        {
            set(current);
            Save();
        }
    }

    private void DrawIntSlider(string label, Action<int> set, int current, int min, int max)
    {
        if (ImGui.SliderInt(label, ref current, min, max))
        {
            set(current);
            Save();
        }
    }

    private void Save() => Plugin.PluginInterface.SavePluginConfig(configuration);
}
