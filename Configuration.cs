using System;
using System.Collections.Generic;

namespace WorkshoppaHelper;

[Serializable]
public sealed class Configuration : Dalamud.Configuration.IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Free-slot threshold that triggers an automatic stash run while Workshoppa is crafting.</summary>
    public int FreeSlotThreshold { get; set; } = 4;

    public bool AutoStashEnabled { get; set; } = true;

    /// <summary>When pulling materials, stop withdrawing once the player has this few free slots left.</summary>
    public int PullReserveSlots { get; set; } = 2;

    public bool SuppressAutoRetainer { get; set; } = true;

    public bool RespectProtectionList { get; set; } = true;

    public int MovementTimeoutSeconds { get; set; } = 45;

    public int AddonTimeoutSeconds { get; set; } = 20;

    public bool VerboseChat { get; set; } = true;

    public List<string> ExcludedRetainers { get; set; } = [];
}
