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

    [Export]
    public string ExportPreset { get; set; } = string.Empty;

    [Export]
    public string ExportOutputPath { get; set; } = "build/windows/game.exe";

    [Export]
    public bool BuildWithDebug { get; set; }

    [Export]
    public SteamDeployConfig? SteamConfig { get; set; }

    [Export]
    public ItchIoDeployConfig? ItchIoConfig { get; set; }
}
#endif
