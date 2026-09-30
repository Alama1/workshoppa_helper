using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace WorkshoppaHelper.Retainers;

/// <summary>
/// Raw IPC interop with AutoRetainer (PunishXIV). We only use it for safety:
/// availability checks, busy checks, suppression while we drive the bell ourselves,
/// and the item protection list. Mirrors the IPC names registered by AutoRetainer.
/// </summary>
public sealed class AutoRetainerInterop
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;

    public AutoRetainerInterop(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
    }

    public bool IsInstalled
    {
        get
        {
            try
            {
                pluginInterface.GetIpcSubscriber<object>("AutoRetainer.Init").InvokeAction();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool IsBusy
    {
        get
        {
            try
            {
                return pluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.IsBusy").InvokeFunc();
            }
            catch
            {
                return false;
            }
        }
    }

    public bool IsItemProtected(uint itemId)
    {
        try
        {
            return pluginInterface.GetIpcSubscriber<uint, bool>("AutoRetainer.PluginState.IsItemProtected").InvokeFunc(itemId);
        }
        catch
        {
            return false;
        }
    }

    public void SetSuppressed(bool suppressed)
    {
        try
        {
            pluginInterface.GetIpcSubscriber<bool, object>("AutoRetainer.SetSuppressed").InvokeAction(suppressed);
        }
        catch (Exception e)
        {
            log.Warning(e, "Failed to set AutoRetainer suppression");
        }
    }
}
