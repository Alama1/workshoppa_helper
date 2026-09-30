using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace WorkshoppaHelper.Workshoppa;

public sealed record QueueSnapshot(
    CurrentItemDto? Current,
    IReadOnlyList<QueuedItemDto> Queue);

public sealed record QueuedItemDto(uint WorkshopItemId, int Quantity);

public sealed record CurrentItemDto(
    uint WorkshopItemId,
    bool StartedCrafting,
    uint PhasesComplete,
    IReadOnlyList<(uint ItemId, uint Quantity)> ContributedInPhase);

/// <summary>
/// Bridge into Workshoppa, which exists in two layouts:
/// <list type="bullet">
/// <item>VIWI module (Vera's Integrated World Improvements): static singleton
/// <c>VIWI.Modules.Workshoppa.WorkshoppaModule.Instance</c>, config via the
/// <c>_configuration</c> property.</item>
/// <item>Standalone Workshoppa plugin (VeraNala fork): instance found through Dalamud's
/// plugin manager, config via the <c>_configuration</c> field.</item>
/// </list>
/// Both drive the same state machine: set <c>MainWindow.State</c> to Pause (3) to stop
/// cleanly, Resume (2)/Start (1) to continue the queue.
/// </summary>
public sealed class WorkshoppaBridge : IDisposable
{
    private const string ViwiAssemblyName = "VIWI";
    private const string ViwiModuleTypeName = "VIWI.Modules.Workshoppa.WorkshoppaModule";
    private const string StandaloneInternalName = "Workshoppa";
    private const string StandalonePluginTypeName = "Workshoppa.WorkshopPlugin";

    private const int ButtonStateStart = 1;
    private const int ButtonStateResume = 2;
    private const int ButtonStatePause = 3;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;

    private Resolved? resolved;

    public WorkshoppaBridge(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
    }

    private sealed class Resolved
    {
        public required string LayoutName { get; init; }
        public required WeakReference InstanceRef { get; init; }
        public required MemberInfo MainWindowMember { get; init; }
        public required PropertyInfo StateProperty { get; init; }
        public required Type ButtonStateType { get; init; }
        public required MemberInfo ConfigurationMember { get; init; }
        public required PropertyInfo CurrentItemProperty { get; init; }
        public required PropertyInfo ItemQueueProperty { get; init; }
        public required PropertyInfo CurrentStageProperty { get; init; }

        public object? GetLiveInstance()
        {
            if (InstanceRef.Target is not { } target)
                return null;

            try
            {
                var window = ReadMember(target, MainWindowMember);
                if (window == null)
                    return null;

                // Touching the live window validates the instance is still alive.
                _ = StateProperty.GetValue(window);
                return target;
            }
            catch
            {
                return null;
            }
        }

        public object? ReadConfiguration(object instance) => ReadMember(instance, ConfigurationMember);
    }

    public bool IsAvailable => TryResolve() != null;

    public string UnavailableReason { get; private set; } = "Workshoppa (standalone or VIWI module) has not been found yet.";

    /// <summary>Workshoppa's current Stage name (e.g. "ContributeMaterials", "Stopped"), or null.</summary>
    public string? CurrentStageName
    {
        get
        {
            var bridge = TryResolve();
            if (bridge?.GetLiveInstance() is not { } instance)
                return null;

            try
            {
                return bridge.CurrentStageProperty.GetValue(instance)?.ToString();
            }
            catch
            {
                return null;
            }
        }
    }

    public bool IsAutomationRunning => CurrentStageName is { } stage && stage != "Stopped";

    public QueueSnapshot? ReadQueue()
    {
        var bridge = TryResolve();
        if (bridge?.GetLiveInstance() is not { } instance)
            return null;

        try
        {
            var configuration = bridge.ReadConfiguration(instance);
            if (configuration == null)
                return null;

            var queue = ReadItemQueue(bridge.ItemQueueProperty, configuration);
            var current = ReadCurrentItem(bridge.CurrentItemProperty, configuration);
            return new QueueSnapshot(current, queue);
        }
        catch (Exception e)
        {
            log.Warning(e, "Failed to read Workshoppa queue");
            return null;
        }
    }

    private static List<QueuedItemDto> ReadItemQueue(PropertyInfo itemQueueProperty, object configuration)
    {
        var result = new List<QueuedItemDto>();
        if (itemQueueProperty.GetValue(configuration) is not System.Collections.IEnumerable rawQueue)
            return result;

        foreach (var entry in rawQueue)
        {
            var entryType = entry.GetType();
            var workshopItemId = Convert.ToUInt32(entryType.GetProperty("WorkshopItemId")!.GetValue(entry));
            var quantity = Convert.ToInt32(entryType.GetProperty("Quantity")!.GetValue(entry));
            result.Add(new QueuedItemDto(workshopItemId, quantity));
        }

        return result;
    }

    private static CurrentItemDto? ReadCurrentItem(PropertyInfo currentItemProperty, object configuration)
    {
        if (currentItemProperty.GetValue(configuration) is not { } currentItem)
            return null;

        var type = currentItem.GetType();
        var workshopItemId = Convert.ToUInt32(type.GetProperty("WorkshopItemId")!.GetValue(currentItem));
        var startedCrafting = Convert.ToBoolean(type.GetProperty("StartedCrafting")!.GetValue(currentItem));
        var phasesComplete = Convert.ToUInt32(type.GetProperty("PhasesComplete")!.GetValue(currentItem));

        var contributed = new List<(uint, uint)>();
        if (type.GetProperty("ContributedItemsInCurrentPhase")!.GetValue(currentItem) is System.Collections.IEnumerable rawContributed)
        {
            foreach (var entry in rawContributed)
            {
                var entryType = entry.GetType();
                var itemId = Convert.ToUInt32(entryType.GetProperty("ItemId")!.GetValue(entry));
                var quantity = Convert.ToUInt32(entryType.GetProperty("QuantityComplete")!.GetValue(entry));
                contributed.Add((itemId, quantity));
            }
        }

        return new CurrentItemDto(workshopItemId, startedCrafting, phasesComplete, contributed);
    }

    public bool Pause()
    {
        if (SetButtonState(ButtonStatePause))
            return true;

        log.Warning("Failed to pause Workshoppa");
        return false;
    }

    public bool Resume(QueueSnapshot? snapshot)
    {
        var button = snapshot?.Current?.StartedCrafting == true ? ButtonStateResume : ButtonStateStart;
        return SetButtonState(button);
    }

    private bool SetButtonState(int value)
    {
        var bridge = TryResolve();
        if (bridge?.GetLiveInstance() is not { } instance)
            return false;

        try
        {
            if (ReadMember(instance, bridge.MainWindowMember) is not { } window)
                return false;

            bridge.StateProperty.SetValue(window, Enum.ToObject(bridge.ButtonStateType, value));
            return true;
        }
        catch (Exception e)
        {
            log.Warning(e, "Failed to set Workshoppa button state");
            return false;
        }
    }

    private static object? ReadMember(object owner, MemberInfo member) => member switch
    {
        FieldInfo field => field.GetValue(owner),
        PropertyInfo property => property.GetValue(owner),
        _ => null,
    };

    private static MemberInfo? FindMember(Type type, string name, BindingFlags flags)
    {
        return (MemberInfo?)type.GetField(name, flags) ?? type.GetProperty(name, flags);
    }

    private Resolved? TryResolve()
    {
        if (resolved?.GetLiveInstance() != null)
            return resolved;

        var fresh = TryResolveViwiModule() ?? TryResolveStandalone();
        resolved = fresh;
        return fresh;
    }

    private Resolved? TryResolveViwiModule()
    {
        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == ViwiAssemblyName);
            if (assembly == null)
                return null;

            var moduleType = assembly.GetType(ViwiModuleTypeName);
            if (moduleType == null)
            {
                SetReason($"VIWI is loaded but {ViwiModuleTypeName} does not exist (version mismatch).");
                return null;
            }

            var instanceProperty = moduleType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (instanceProperty == null)
            {
                SetReason("VIWI WorkshoppaModule.Instance not found (version mismatch).");
                return null;
            }

            if (instanceProperty.GetValue(null) is not { } module)
            {
                SetReason("VIWI is loaded but its Workshoppa module is not initialized.");
                return null;
            }

            var enabledProperty = moduleType.GetProperty("Enabled", BindingFlags.Static | BindingFlags.Public);
            if (enabledProperty?.GetValue(null) is false)
            {
                SetReason("The Workshoppa module is disabled in VIWI's settings.");
                return null;
            }

            var resolved = BuildResolved("VIWI module", moduleType, module, windowFieldName: "_mainWindow");
            if (resolved != null)
                return resolved;

            SetReason($"VIWI's Workshoppa module internals do not match the expected version.");
            return null;
        }
        catch (Exception e)
        {
            log.Warning(e, "VIWI Workshoppa module resolution failed");
            return null;
        }
    }

    private Resolved? TryResolveStandalone()
    {
        try
        {
            foreach (var exposed in pluginInterface.InstalledPlugins)
            {
                if (!string.Equals(exposed.InternalName, StandaloneInternalName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!exposed.IsLoaded)
                {
                    SetReason("Workshoppa is installed but not loaded.");
                    return null;
                }

                // ExposedPlugin wraps the internal LocalPlugin; grab it by field type, then the
                // live plugin instance from LocalPlugin's private "instance" field.
                var localPluginField = exposed.GetType()
                    .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .FirstOrDefault(f => f.FieldType.Name == "LocalPlugin");
                if (localPluginField?.GetValue(exposed) is not { } localPlugin)
                {
                    SetReason("Workshoppa is not loaded.");
                    return null;
                }

                var instanceField = localPlugin.GetType()
                    .GetField("instance", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (instanceField?.GetValue(localPlugin) is not { } pluginInstance)
                {
                    SetReason("Workshoppa instance is null.");
                    return null;
                }

                var pluginType = pluginInstance.GetType();
                if (pluginType.FullName != StandalonePluginTypeName)
                {
                    SetReason($"Unexpected Workshoppa type: {pluginType.FullName}");
                    return null;
                }

                var resolved = BuildResolved("standalone Workshoppa", pluginType, pluginInstance, windowFieldName: "_mainWindow");
                if (resolved != null)
                    return resolved;

                SetReason("Workshoppa internals do not match the expected version.");
                return null;
            }

            return null;
        }
        catch (Exception e)
        {
            log.Warning(e, "Standalone Workshoppa resolution failed");
            return null;
        }
    }

    private static Resolved? BuildResolved(string layoutName, Type pluginType, object instance, string windowFieldName)
    {
        var mainWindowMember = FindMember(pluginType, windowFieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var configurationMember = FindMember(pluginType, "_configuration", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var currentStageProperty = pluginType.GetProperty("CurrentStage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (mainWindowMember == null || configurationMember == null || currentStageProperty == null)
            return null;

        if (ReadMember(instance, mainWindowMember) is not { } window)
            return null;

        var stateProperty = window.GetType().GetProperty("State", BindingFlags.Instance | BindingFlags.Public);
        if (stateProperty == null || !stateProperty.CanWrite)
            return null;

        var configuration = ReadMember(instance, configurationMember);
        if (configuration == null)
            return null;

        var currentItemProperty = configuration.GetType().GetProperty("CurrentlyCraftedItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var itemQueueProperty = configuration.GetType().GetProperty("ItemQueue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (currentItemProperty == null || itemQueueProperty == null)
            return null;

        return new Resolved
        {
            LayoutName = layoutName,
            InstanceRef = new WeakReference(instance),
            MainWindowMember = mainWindowMember,
            StateProperty = stateProperty,
            ButtonStateType = stateProperty.PropertyType,
            ConfigurationMember = configurationMember,
            CurrentItemProperty = currentItemProperty,
            ItemQueueProperty = itemQueueProperty,
            CurrentStageProperty = currentStageProperty,
        };
    }

    private void SetReason(string reason)
    {
        UnavailableReason = reason;
        resolved = null;
    }

    public void Dispose()
    {
        resolved = null;
    }
}
