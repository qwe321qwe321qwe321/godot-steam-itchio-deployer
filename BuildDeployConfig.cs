#if TOOLS
#nullable enable
using Godot;
using SteamItchIoDeployerCore;

namespace GodotSteamItchIoDeployer;

[Tool, GlobalClass]
public partial class BuildDeployConfig : Resource
{
    [Export(PropertyHint.Flags, "Steam,itch.io")]
    public DeployTargets Targets { get; set; } = DeployTargets.Steam;

    // The export output location is owned by the selected preset's export_path in
    // export_presets.cfg (read via ExportPresetReader), so it is not duplicated here.
    [Export]
    public string ExportPreset { get; set; } = string.Empty;

    [Export]
    public bool BuildWithDebug { get; set; }

    [Export]
    public SteamDeployConfig? SteamConfig { get; set; }

    [Export]
    public ItchIoDeployConfig? ItchIoConfig { get; set; }
}
#endif
